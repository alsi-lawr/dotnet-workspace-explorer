namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System.Collections.Concurrent
open System.Threading
open Dotnet.WorkspaceExplorer.Packages

type private Mapping = PackageSourceMappingPolicy

[<RequireQualifiedAccess>]
module internal PackageInventories =
    let private failure kind message retry =
        PackageFailure.create kind message retry |> Result.defaultWith (failwithf "%A")

    let private duplicateRequestFailure () =
        failure
            PackageFailureKind.InvalidRequest
            "The package request identifier is already active."
            PackageFailureRetry.Never

    let private cancelledFailure () =
        failure
            PackageFailureKind.Cancelled
            "The package work was cancelled."
            PackageFailureRetry.Never

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
        (installed:
            PackageRequest<unit> -> Async<Result<InstalledPackageGraph list, PackageFailure>>)
        (configuredSources: ConfiguredPackageSources)
        (sourceMapping: ReadPackageSourceMapping)
        (details: ReadPackageDetails)
        (request: PackageRequest<PrereleaseSelection>)
        (cancellation: CancellationToken)
        (sink: PackageBatchSink<PackageUpdate>)
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

                    let rec produce metadataByPackage =
                        function
                        | [] -> async.Return(Ok())
                        | (package: InstalledPackage, current) :: remaining ->
                            async {
                                cancellation.ThrowIfCancellationRequested()
                                let key = package.Identity.Value.ToUpperInvariant()

                                let! metadata, updatedMetadata =
                                    match metadataByPackage |> Map.tryFind key with
                                    | Some value -> async.Return(value, metadataByPackage)
                                    | None ->
                                        async {
                                            let! mapping =
                                                sourceMapping
                                                    { Id = request.Id
                                                      Target = request.Target
                                                      Value =
                                                        { Package = package.Identity
                                                          CandidateSource = None
                                                          RestoredTransitives = Some [] } }

                                            let allowed =
                                                match mapping with
                                                | Ok policy ->
                                                    match policy with
                                                    | Mapping.Allowed values -> values |> Set.ofList
                                                    | Mapping.InsufficientRestoredTransitiveEvidence values ->
                                                        values |> Set.ofList
                                                    | Mapping.KnownConflict(_, values) ->
                                                        values |> Set.ofList
                                                | Error _ -> Set.empty

                                            let! value =
                                                sources
                                                |> List.filter (fun source ->
                                                    allowed.Contains source.Id)
                                                |> fun selected ->
                                                    detailsForPackage
                                                        details
                                                        unitRequest
                                                        selected
                                                        package.Identity

                                            return value, metadataByPackage |> Map.add key value
                                        }

                                match
                                    metadata
                                    |> Option.map (fun value ->
                                        newerVersions request.Value current value.Versions)
                                    |> Option.bind NonEmptyList.tryCreate
                                with
                                | Some available ->
                                    do!
                                        sink
                                            cancellation
                                            (NonEmptyList.singleton
                                                { Installed = package
                                                  Available = available })

                                    cancellation.ThrowIfCancellationRequested()
                                | None -> ()

                                return! produce updatedMetadata remaining
                            }

                    return! produce Map.empty (directPackages graphs)
        }

    let updates
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (installed:
            PackageRequest<unit> -> Async<Result<InstalledPackageGraph list, PackageFailure>>)
        (configuredSources: ConfiguredPackageSources)
        (sourceMapping: ReadPackageSourceMapping)
        (details: ReadPackageDetails)
        (request: PackageRequest<PrereleaseSelection>)
        sink
        =
        PackageProducer.cancellable
            requests
            request.Id
            (duplicateRequestFailure ())
            (cancelledFailure ())
            (fun cancellation ->
                updatesCore
                    installed
                    configuredSources
                    sourceMapping
                    details
                    request
                    cancellation
                    sink)

    let private consolidationForEntries entries =
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
                        |> List.map (fun (package: InstalledPackage, _) -> package.Target)
                        |> NonEmptyList.tryCreate
                    with
                    | Ok parsed, Some targets -> Some(parsed, targets)
                    | _ -> None)

            match
                currentVersions |> NonEmptyList.tryCreate,
                currentVersions |> List.map fst |> List.rev |> NonEmptyList.tryCreate
            with
            | Some versions, Some candidates ->
                let package = entries.Head |> fst

                Some
                    { Identity = package.Identity
                      CurrentVersions = versions
                      CandidateVersions = candidates }
            | _ -> None

    let private consolidationCore
        (installed:
            PackageRequest<unit> -> Async<Result<InstalledPackageGraph list, PackageFailure>>)
        (request: PackageRequest<unit>)
        (cancellation: CancellationToken)
        (sink: PackageBatchSink<PackageConsolidation>)
        =
        async {
            let! result = installed request

            match result with
            | Error failure -> return Error failure
            | Ok graphs ->
                let groups =
                    directPackages graphs
                    |> List.groupBy (fun (package: InstalledPackage, _) ->
                        package.Identity.Value.ToUpperInvariant())

                let rec produce =
                    function
                    | [] -> async.Return(Ok())
                    | (_, entries) :: remaining ->
                        async {
                            cancellation.ThrowIfCancellationRequested()

                            match consolidationForEntries entries with
                            | Some consolidation ->
                                do! sink cancellation (NonEmptyList.singleton consolidation)

                                cancellation.ThrowIfCancellationRequested()
                            | None -> ()

                            return! produce remaining
                        }

                return! produce groups
        }

    let consolidation
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (installed:
            PackageRequest<unit> -> Async<Result<InstalledPackageGraph list, PackageFailure>>)
        (request: PackageRequest<unit>)
        sink
        =
        PackageProducer.cancellable
            requests
            request.Id
            (duplicateRequestFailure ())
            (cancelledFailure ())
            (fun cancellation -> consolidationCore installed request cancellation sink)
