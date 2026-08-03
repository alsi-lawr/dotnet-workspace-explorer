namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.Collections.Concurrent
open System.Threading
open Dotnet.WorkspaceExplorer.Packages
open NuGet.Common
open NuGet.Frameworks
open NuGet.Protocol
open NuGet.Protocol.Core.Types
open NuGet.Versioning

[<RequireQualifiedAccess>]
module internal NuGetPackageDetails =
    type private ResultBuilder() =
        member _.Bind(value, binding) = Result.bind binding value
        member _.Return value = Ok value
        member _.ReturnFrom value = value

    let private result = ResultBuilder()
    let private logger = NullLogger.Instance

    let private selectMetadata
        (selection: PackageVersionSelection)
        (metadata: IPackageSearchMetadata list)
        =
        let ordered =
            metadata
            |> List.sortWith (fun left right ->
                right.Identity.Version.CompareTo left.Identity.Version)

        let exact (value: string) (item: IPackageSearchMetadata) =
            try
                item.Identity.Version = NuGet.Versioning.NuGetVersion.Parse value
            with :? ArgumentException ->
                false

        let inRange (value: string) (item: IPackageSearchMetadata) =
            try
                VersionRange.Parse(value).Satisfies item.Identity.Version
            with :? ArgumentException ->
                false

        match selection with
        | PackageVersionSelection.Latest ->
            ordered
            |> List.tryFind (fun item -> not item.Identity.Version.IsPrerelease)
            |> Option.orElseWith (fun () -> List.tryHead ordered)
        | PackageVersionSelection.Exact value -> ordered |> List.tryFind (exact value.Value)
        | PackageVersionSelection.Range value -> ordered |> List.tryFind (inRange value.Value)

    let private dependencyGroups (metadata: IPackageSearchMetadata) =
        if isNull metadata.DependencySets then
            Ok Map.empty
        else
            metadata.DependencySets
            |> PackageMetadata.dependencyGroups
            |> Seq.fold
                (fun state group ->
                    result {
                        let! accumulated = state

                        let! framework =
                            if
                                group.TargetFramework.IsAny
                                || group.TargetFramework = NuGetFramework.AnyFramework
                            then
                                Ok None
                            else
                                TargetFramework.create (group.TargetFramework.GetShortFolderName())
                                |> Result.map Some

                        let! dependencies =
                            group.Packages
                            |> PackageMetadata.dependencies
                            |> Seq.map (fun dependency ->
                                result {
                                    let! identity = PackageMetadata.packageId dependency.Id

                                    let! range =
                                        PackageMetadata.versionRange dependency.VersionRange

                                    return identity, range
                                })
                            |> Seq.fold
                                (fun dependencies dependency ->
                                    match dependencies, dependency with
                                    | Ok items, Ok item -> Ok(item :: items)
                                    | Error error, _
                                    | _, Error error -> Error error)
                                (Ok [])
                            |> Result.map List.rev

                        let existing =
                            accumulated |> Map.tryFind framework |> Option.defaultValue []

                        return
                            accumulated
                            |> Map.add
                                framework
                                (PackageMetadata.mergeDependencies existing dependencies)
                    })
                (Ok Map.empty)

    let private deprecation (token: CancellationToken) (metadata: IPackageSearchMetadata) =
        async {
            try
                let! value = metadata.GetDeprecationMetadataAsync() |> Async.AwaitTask

                if isNull value then
                    return PackageDeprecation.NotDeprecated
                else
                    let reasons =
                        value.Reasons
                        |> Seq.choose (
                            PackageMetadata.text PackageMetadata.limits.DeprecationReason
                        )
                        |> Seq.truncate 16
                        |> Seq.toList

                    let alternate =
                        if isNull value.AlternatePackage then
                            None
                        else
                            match PackageMetadata.packageId value.AlternatePackage.PackageId with
                            | Error _ -> None
                            | Ok identity ->
                                let range =
                                    if isNull value.AlternatePackage.Range then
                                        None
                                    else
                                        PackageMetadata.versionRange value.AlternatePackage.Range
                                        |> Result.toOption

                                Some { Identity = identity; Range = range }

                    match NonEmptyList.tryCreate reasons with
                    | Some nonEmpty -> return PackageDeprecation.Deprecated(nonEmpty, alternate)
                    | None ->
                        return
                            PackageDeprecation.Deprecated(
                                NonEmptyList.singleton "Deprecated",
                                alternate
                            )
            with
            | :? OperationCanceledException when token.IsCancellationRequested ->
                return raise (OperationCanceledException token)
            | _ -> return PackageDeprecation.NotDeprecated
        }

    let private vulnerabilities (metadata: IPackageSearchMetadata) =
        if isNull metadata.Vulnerabilities then
            []
        else
            metadata.Vulnerabilities
            |> Seq.choose (fun vulnerability ->
                PackageMetadata.safeUri vulnerability.AdvisoryUrl
                |> Option.map (fun advisory ->
                    let severity =
                        match vulnerability.Severity with
                        | value when value <= 0 ->
                            Dotnet.WorkspaceExplorer.Packages.PackageVulnerabilitySeverity.Low
                        | 1 ->
                            Dotnet.WorkspaceExplorer.Packages.PackageVulnerabilitySeverity.Moderate
                        | 2 -> Dotnet.WorkspaceExplorer.Packages.PackageVulnerabilitySeverity.High
                        | _ ->
                            Dotnet.WorkspaceExplorer.Packages.PackageVulnerabilitySeverity.Critical

                    { Severity = severity
                      Advisory = advisory }))
            |> Seq.truncate 128
            |> Seq.toList

    let private detailsSource
        (request: PackageDetailsRequest)
        (token: CancellationToken)
        (source: ConfiguredSource)
        =
        async {
            try
                let! resource =
                    source.Repository.GetResourceAsync<PackageMetadataResource>(token)
                    |> Async.AwaitTask

                if isNull resource then
                    return
                        Error(
                            PackageSourceFailure.create
                                source.Model.Id
                                PackageSourceFailureKind.Unavailable
                        )
                else
                    use cache = new SourceCacheContext()

                    let! available =
                        resource.GetMetadataAsync(
                            request.Package.Value,
                            true,
                            false,
                            cache,
                            logger,
                            token
                        )
                        |> Async.AwaitTask

                    let available = available |> PackageMetadata.availableVersions |> Seq.toList

                    match selectMetadata request.Version available with
                    | None -> return Ok None
                    | Some selected ->
                        let! deprecationState = deprecation token selected

                        let normalizedVersions =
                            available
                            |> List.sortWith (fun left right ->
                                right.Identity.Version.CompareTo left.Identity.Version)
                            |> List.map (fun item -> PackageMetadata.version item.Identity.Version)

                        match
                            PackageMetadata.summary source.Model.Id selected,
                            dependencyGroups selected,
                            normalizedVersions
                            |> List.tryPick (function
                                | Error error -> Some error
                                | _ -> None)
                        with
                        | Error _, _, _
                        | _, Error _, _
                        | _, _, Some _ ->
                            return
                                Error(
                                    PackageSourceFailure.create
                                        source.Model.Id
                                        PackageSourceFailureKind.Malformed
                                )
                        | Ok summary, Ok groups, None ->
                            let license =
                                if isNull selected.LicenseMetadata then
                                    None
                                else
                                    PackageMetadata.safeTextOrUri
                                        PackageMetadata.limits.License
                                        selected.LicenseMetadata.License

                            let readme =
                                PackageMetadata.safeUri selected.ReadmeUrl
                                |> Option.orElseWith (fun () ->
                                    PackageMetadata.safeUriText selected.ReadmeFileUrl)

                            return
                                Ok(
                                    Some
                                        { Summary = summary
                                          Versions =
                                            normalizedVersions
                                            |> List.choose (function
                                                | Ok version -> Some version
                                                | _ -> None)
                                            |> List.distinct
                                          Authors = summary.Authors
                                          ProjectUrl = PackageMetadata.safeUri selected.ProjectUrl
                                          License = license
                                          LicenseUrl =
                                            if isNull selected.LicenseMetadata then
                                                PackageMetadata.safeUri selected.LicenseUrl
                                            else
                                                PackageMetadata.safeUri
                                                    selected.LicenseMetadata.LicenseUrl
                                          ReadmeUrl = readme
                                          DependencyGroups = groups
                                          Deprecation = deprecationState
                                          Vulnerabilities = vulnerabilities selected }
                                )
            with
            | _ when token.IsCancellationRequested ->
                return raise (OperationCanceledException token)
            | error -> return Error(NuGetSourceFailures.sourceFailure source.Model.Id error)
        }

    let details
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (request: PackageRequest<PackageDetailsRequest>)
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
                        match NuGetSources.loadCatalog request.Target with
                        | Error failure -> return Error failure
                        | Ok catalog ->
                            match
                                NuGetSources.sourceSelection catalog (Some request.Value.Source)
                            with
                            | Error failure -> return Error failure
                            | Ok [ source ] ->
                                let! outcome = detailsSource request.Value cancellation.Token source

                                match outcome with
                                | Ok(Some packageDetails) -> return Ok packageDetails
                                | Ok None ->
                                    return
                                        Error(
                                            NuGetSourceFailures.packageFailure
                                                PackageFailureKind.NotFound
                                                "The package or requested version was not found."
                                                PackageFailureRetry.Never
                                        )
                                | Error failure ->
                                    return
                                        Error(
                                            NuGetSourceFailures.sourceFailureAsPackageFailure
                                                failure
                                        )
                            | Ok _ ->
                                return
                                    Error(
                                        NuGetSourceFailures.packageFailure
                                            PackageFailureKind.Internal
                                            "The selected package source could not be resolved."
                                            PackageFailureRetry.Never
                                    )
                    with :? OperationCanceledException when cancellation.IsCancellationRequested ->
                        return Error(NuGetSourceFailures.cancelled ())
                finally
                    requests.TryRemove request.Id |> ignore
        }
