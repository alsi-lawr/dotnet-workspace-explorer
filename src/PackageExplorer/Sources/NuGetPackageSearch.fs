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

    let private decodeContinuation continuation =
        match continuation with
        | None -> Ok 0
        | Some token ->
            try
                let bytes = Convert.FromBase64String token
                let text = Encoding.ASCII.GetString bytes

                match Int32.TryParse text with
                | true, value when value >= 0 -> Ok value
                | _ ->
                    Error(
                        NuGetSourceFailures.invalidRequest
                            "The package search continuation is invalid."
                    )
            with :? FormatException ->
                Error(
                    NuGetSourceFailures.invalidRequest "The package search continuation is invalid."
                )

    let private encodeContinuation (offset: int) =
        offset.ToString(Globalization.CultureInfo.InvariantCulture)
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
                            request.PageSize.Value,
                            logger,
                            token
                        )
                        |> Async.AwaitTask

                    let normalized =
                        metadata |> Seq.map (PackageMetadata.summary source.Model.Id) |> Seq.toList

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
                        | Ok catalog, Ok skip ->
                            match
                                NuGetSources.sourceSelection catalog request.Value.Search.Source
                            with
                            | Error failure -> return Error failure
                            | Ok sources ->
                                let! outcomes =
                                    sources
                                    |> List.map (searchSource request.Value skip cancellation.Token)
                                    |> Async.Parallel

                                let items =
                                    outcomes
                                    |> Array.toList
                                    |> List.collect (function
                                        | Ok values -> values
                                        | Error _ -> [])

                                let failures =
                                    outcomes
                                    |> Array.toList
                                    |> List.choose (function
                                        | Error failure -> Some failure
                                        | Ok _ -> None)

                                let hasMore =
                                    outcomes
                                    |> Array.exists (function
                                        | Ok values -> values.Length = request.Value.PageSize.Value
                                        | Error _ -> false)

                                return
                                    Ok
                                        { Items = items
                                          Continuation =
                                            if hasMore then
                                                Some(
                                                    encodeContinuation (
                                                        skip + request.Value.PageSize.Value
                                                    )
                                                )
                                            else
                                                None
                                          SourceFailures = failures }
                    with :? OperationCanceledException when cancellation.IsCancellationRequested ->
                        return Error(NuGetSourceFailures.cancelled ())
                finally
                    requests.TryRemove request.Id |> ignore
        }
