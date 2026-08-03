namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open Dotnet.WorkspaceExplorer.Packages
open NuGet.Common
open NuGet.Protocol.Core.Types

[<RequireQualifiedAccess>]
module internal NuGetPackageSearch =
    let private logger = NullLogger.Instance

    type private SearchCursor = { SourceIndex: int; SourceOffset: int }

    let private decodeContinuation continuation =
        match continuation with
        | None -> Ok { SourceIndex = 0; SourceOffset = 0 }
        | Some token ->
            try
                let bytes = Convert.FromBase64String token
                let text = Encoding.ASCII.GetString bytes

                match text.Split(':') with
                | [| source; offset |] ->
                    match Int32.TryParse source, Int32.TryParse offset with
                    | (true, sourceIndex), (true, sourceOffset) when
                        sourceIndex >= 0 && sourceOffset >= 0
                        ->
                        Ok
                            { SourceIndex = sourceIndex
                              SourceOffset = sourceOffset }
                    | _ ->
                        Error(
                            NuGetSourceFailures.invalidRequest
                                "The package search continuation is invalid."
                        )
                | _ ->
                    Error(
                        NuGetSourceFailures.invalidRequest
                            "The package search continuation is invalid."
                    )
            with :? FormatException ->
                Error(
                    NuGetSourceFailures.invalidRequest "The package search continuation is invalid."
                )

    let private encodeContinuation cursor =
        String.Concat(
            cursor.SourceIndex.ToString(Globalization.CultureInfo.InvariantCulture),
            ":",
            cursor.SourceOffset.ToString(Globalization.CultureInfo.InvariantCulture)
        )
        |> Encoding.ASCII.GetBytes
        |> Convert.ToBase64String

    let private searchTerm (search: PackageSearch) =
        match search.Term with
        | PackageSearchTerm.AllPackages -> String.Empty
        | PackageSearchTerm.Matching term ->
            PackageMetadata.text PackageMetadata.limits.Summary term
            |> Option.defaultValue String.Empty

    let private searchSource
        (request: PackageSearchRequest)
        (skip: int)
        (take: int)
        (token: CancellationToken)
        (source: ConfiguredSource)
        =
        async {
            try
                let! resource =
                    source.Repository.GetResourceAsync<PackageSearchResource>(token)
                    |> Async.AwaitTask

                if isNull resource then
                    return
                        Error(
                            PackageSourceFailure.create
                                source.Model.Id
                                PackageSourceFailureKind.Unavailable
                        )
                else
                    let includePrerelease =
                        request.Search.Prerelease = PrereleaseSelection.IncludePrerelease

                    let filter = SearchFilter includePrerelease
                    filter.IncludeDelisted <- false

                    let! metadata =
                        resource.SearchAsync(
                            searchTerm request.Search,
                            filter,
                            skip,
                            take,
                            logger,
                            token
                        )
                        |> Async.AwaitTask

                    let boundedMetadata = metadata |> Seq.truncate (take + 1) |> Seq.toList

                    let normalized =
                        boundedMetadata
                        |> List.truncate take
                        |> Seq.map (PackageMetadata.summary source.Model.Id)
                        |> Seq.toList

                    match
                        normalized
                        |> List.tryPick (function
                            | Error violation -> Some violation
                            | _ -> None)
                    with
                    | Some _ ->
                        return
                            Error(
                                PackageSourceFailure.create
                                    source.Model.Id
                                    PackageSourceFailureKind.Malformed
                            )
                    | None ->
                        return
                            Ok(
                                normalized
                                |> List.choose (function
                                    | Ok value -> Some value
                                    | _ -> None)
                            )
            with
            | _ when token.IsCancellationRequested ->
                return raise (OperationCanceledException token)
            | error -> return Error(NuGetSourceFailures.sourceFailure source.Model.Id error)
        }

    let rec private fillPage
        request
        token
        (sources: ConfiguredSource list)
        cursor
        remaining
        items
        failures
        =
        async {
            if remaining = 0 then
                return items, failures, Some cursor
            elif cursor.SourceIndex >= sources.Length then
                return items, failures, None
            else
                let source = sources[cursor.SourceIndex]

                let! outcome = searchSource request cursor.SourceOffset remaining token source

                match outcome with
                | Error failure ->
                    return!
                        fillPage
                            request
                            token
                            sources
                            { SourceIndex = cursor.SourceIndex + 1
                              SourceOffset = 0 }
                            remaining
                            items
                            (failures @ [ failure ])
                | Ok available ->
                    let accepted = available |> List.truncate remaining
                    let consumed = accepted.Length
                    let pageItems = items @ accepted

                    if consumed = 0 then
                        return!
                            fillPage
                                request
                                token
                                sources
                                { SourceIndex = cursor.SourceIndex + 1
                                  SourceOffset = 0 }
                                remaining
                                pageItems
                                failures
                    elif consumed = remaining then
                        return
                            pageItems,
                            failures,
                            Some
                                { SourceIndex = cursor.SourceIndex
                                  SourceOffset = cursor.SourceOffset + consumed }
                    else
                        return!
                            fillPage
                                request
                                token
                                sources
                                { SourceIndex = cursor.SourceIndex + 1
                                  SourceOffset = 0 }
                                (remaining - consumed)
                                pageItems
                                failures
        }

    let search
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (request: PackageRequest<PackageSearchRequest>)
        =
        async {
            let! ambient = Async.CancellationToken
            use cancellation = CancellationTokenSource.CreateLinkedTokenSource ambient

            if not (requests.TryAdd(request.Id, cancellation)) then
                return
                    Error(
                        NuGetSourceFailures.invalidRequest
                            "The package request identifier is already active."
                    )
            else
                try
                    try
                        match
                            NuGetSources.loadCatalog request.Target,
                            decodeContinuation request.Value.Continuation
                        with
                        | Error failure, _ -> return Error failure
                        | _, Error failure -> return Error failure
                        | Ok catalog, Ok cursor ->
                            match
                                NuGetSources.sourceSelection catalog request.Value.Search.Source
                            with
                            | Error failure -> return Error failure
                            | Ok sources when cursor.SourceIndex > sources.Length ->
                                return
                                    Error(
                                        NuGetSourceFailures.invalidRequest
                                            "The package search continuation is invalid."
                                    )
                            | Ok sources ->
                                let! items, failures, next =
                                    fillPage
                                        request.Value
                                        cancellation.Token
                                        sources
                                        cursor
                                        request.Value.PageSize.Value
                                        []
                                        []

                                return
                                    Ok
                                        { Items = items
                                          Continuation = next |> Option.map encodeContinuation
                                          SourceFailures = failures }
                    with :? OperationCanceledException when cancellation.IsCancellationRequested ->
                        return Error(NuGetSourceFailures.cancelled ())
                finally
                    requests.TryRemove request.Id |> ignore
        }
