namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.Collections.Concurrent
open System.Threading
open Dotnet.WorkspaceExplorer.Packages

type private Mapping = PackageSourceMappingPolicy

[<RequireQualifiedAccess>]
module internal PackageInventories =
    let private failure kind message retry =
        PackageFailure.create kind message retry |> Result.defaultWith (failwithf "%A")

    let private cancellable
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        requestId
        operation
        =
        async {
            let! ambient = Async.CancellationToken
            use cancellation = CancellationTokenSource.CreateLinkedTokenSource ambient

            if not (requests.TryAdd(requestId, cancellation)) then
                return
                    Error(
                        failure
                            PackageFailureKind.InvalidRequest
                            "The package request identifier is already active."
                            PackageFailureRetry.Never
                    )
            else
                try
                    try
                        return!
                            Async.StartAsTask(
                                operation cancellation.Token,
                                cancellationToken = cancellation.Token
                            )
                            |> Async.AwaitTask
                    with :? OperationCanceledException when cancellation.IsCancellationRequested ->
                        return
                            Error(
                                failure
                                    PackageFailureKind.Cancelled
                                    "The package work was cancelled."
                                    PackageFailureRetry.Never
                            )
                finally
                    requests.TryRemove requestId |> ignore
        }

    let private resolvedVersion (installed: InstalledPackage) =
        match installed.State with
        | InstalledPackageState.Direct(_, version)
        | InstalledPackageState.CentrallyManagedDirect(_, version, _) -> Some version
        | InstalledPackageState.Transitive _
        | InstalledPackageState.FrameworkProvided _
        | InstalledPackageState.FrameworkProvidedWithoutVersion
        | InstalledPackageState.UnresolvedDirect _
        | InstalledPackageState.UnresolvedCentrallyManagedDirect _ -> None

    let private directPackages (graphs: InstalledPackageGraph list) =
        graphs
        |> List.collect (fun graph -> graph.Packages)
        |> List.choose (fun (installed: InstalledPackage) ->
            resolvedVersion installed |> Option.map (fun version -> installed, version))

    let private parsedVersion (version: NuGetVersion) =
        NuGet.Versioning.NuGetVersion.Parse version.Value

    let private newerVersions
        (prerelease: PrereleaseSelection)
        (current: NuGetVersion)
        (versions: NuGetVersion list)
        =
        let currentValue = parsedVersion current

        versions
        |> List.distinctBy (fun version -> version.Value.ToUpperInvariant())
        |> List.filter (fun (candidate: NuGetVersion) ->
            let parsed = parsedVersion candidate

            parsed > currentValue
            && (prerelease = PrereleaseSelection.IncludePrerelease || not parsed.IsPrerelease))
        |> List.sortByDescending parsedVersion

    let private availableSources (sources: PackageSource list) =
        sources
        |> List.filter (fun source -> source.Availability = PackageSourceAvailability.Available)

    let private detailsForPackage
        (details: ReadPackageDetails)
        (request: PackageRequest<unit>)
        (sources: PackageSource list)
        (package: PackageId)
        =
        let rec trySources =
            function
            | [] -> async.Return None
            | source: PackageSource :: remaining ->
                async {
                    let! result =
                        details
                            { Id = PackageRequestId.newId ()
                              Target = request.Target
                              Value =
                                { Package = package
                                  Version = PackageVersionSelection.Latest
                                  Source = source.Id } }

                    match result with
                    | Ok value -> return Some value
                    | Error _ -> return! trySources remaining
                }

        trySources sources

    let private updatesCore
        (installed: ReadInstalledPackages)
        (configuredSources: ConfiguredPackageSources)
        (sourceMapping: ReadPackageSourceMapping)
        (details: ReadPackageDetails)
        (request: PackageRequest<PrereleaseSelection>)
        =
        async {
            let unitRequest =
                { Id = request.Id
                  Target = request.Target
                  Value = () }

            let! installedResult = installed unitRequest

            match installedResult with
            | Error failure -> return Error failure
            | Ok graphs ->
                let! sourceResult = configuredSources unitRequest

                match sourceResult with
                | Error failure -> return Error failure
                | Ok sources ->
                    let sources = availableSources sources

                    let distinctPackages =
                        directPackages graphs
                        |> List.map (fun (installed, _) -> installed.Identity)
                        |> List.distinctBy (fun package -> package.Value.ToUpperInvariant())

                    let! metadata =
                        distinctPackages
                        |> List.map (fun (package: PackageId) ->
                            async {
                                let! mapping =
                                    sourceMapping
                                        { Id = request.Id
                                          Target = request.Target
                                          Value =
                                            { Package = package
                                              CandidateSource = None
                                              RestoredTransitives = Some [] } }

                                let allowed =
                                    match mapping with
                                    | Ok policy ->
                                        match policy with
                                        | Mapping.Allowed values -> values |> Set.ofList
                                        | Mapping.InsufficientRestoredTransitiveEvidence values ->
                                            values |> Set.ofList
                                        | Mapping.KnownConflict(_, values) -> values |> Set.ofList
                                    | Error _ -> Set.empty

                                let! value =
                                    sources
                                    |> List.filter (fun source -> allowed.Contains source.Id)
                                    |> fun selected ->
                                        detailsForPackage details unitRequest selected package

                                return package.Value.ToUpperInvariant(), value
                            })
                        |> Async.Sequential

                    let metadataByPackage = metadata |> Map.ofArray

                    return
                        directPackages graphs
                        |> List.choose (fun (package, current) ->
                            metadataByPackage
                            |> Map.tryFind (package.Identity.Value.ToUpperInvariant())
                            |> Option.flatten
                            |> Option.map (fun (value: PackageDetails) ->
                                newerVersions request.Value current value.Versions)
                            |> Option.bind NonEmptyList.tryCreate
                            |> Option.map (fun available ->
                                { Installed = package
                                  Available = available }))
                        |> Ok
        }

    let updates
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (installed: ReadInstalledPackages)
        (configuredSources: ConfiguredPackageSources)
        (sourceMapping: ReadPackageSourceMapping)
        (details: ReadPackageDetails)
        (request: PackageRequest<PrereleaseSelection>)
        =
        cancellable requests request.Id (fun cancellation ->
            async {
                cancellation.ThrowIfCancellationRequested()

                let! result = updatesCore installed configuredSources sourceMapping details request

                cancellation.ThrowIfCancellationRequested()
                return result
            })

    let private consolidationCore
        (installed: ReadInstalledPackages)
        (request: PackageRequest<unit>)
        =
        async {
            let! result = installed request

            return
                result
                |> Result.map (fun graphs ->
                    directPackages graphs
                    |> List.groupBy (fun (package: InstalledPackage, _) ->
                        package.Identity.Value.ToUpperInvariant())
                    |> List.choose (fun (_, entries) ->
                        let byVersion =
                            entries
                            |> List.groupBy (fun (_, version: NuGetVersion) -> version.Value)
                            |> List.sortBy (fst >> NuGet.Versioning.NuGetVersion.Parse)

                        if byVersion.Length < 2 then
                            None
                        else
                            let currentVersions =
                                byVersion
                                |> List.choose (fun (version, packages) ->
                                    match
                                        NuGetVersion.create version,
                                        packages
                                        |> List.map (fun (package: InstalledPackage, _) ->
                                            package.Target)
                                        |> NonEmptyList.tryCreate
                                    with
                                    | Ok parsed, Some targets -> Some(parsed, targets)
                                    | _ -> None)

                            match
                                currentVersions |> NonEmptyList.tryCreate,
                                currentVersions
                                |> List.map fst
                                |> List.rev
                                |> NonEmptyList.tryCreate
                            with
                            | Some versions, Some candidates ->
                                let package = entries.Head |> fst

                                Some
                                    { Identity = package.Identity
                                      CurrentVersions = versions
                                      CandidateVersions = candidates }
                            | _ -> None))
        }

    let consolidation
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (installed: ReadInstalledPackages)
        (request: PackageRequest<unit>)
        =
        cancellable requests request.Id (fun cancellation ->
            async {
                cancellation.ThrowIfCancellationRequested()
                let! result = consolidationCore installed request
                cancellation.ThrowIfCancellationRequested()
                return result
            })
