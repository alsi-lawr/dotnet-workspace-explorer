namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.IO
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
module internal PackageOperationPreviews =
    let private failure (kind: PackageFailureKind) (message: string) (retry: PackageFailureRetry) =
        PackageFailure.create kind message retry |> Result.defaultWith (failwithf "%A")

    let private unsupported (message: string) =
        failure PackageFailureKind.Unsupported message PackageFailureRetry.AfterUserAction

    let private stale (message: string) =
        failure PackageFailureKind.StaleState message PackageFailureRetry.AfterUserAction

    let private invalid (message: string) =
        failure PackageFailureKind.InvalidRequest message PackageFailureRetry.AfterUserAction

    let private packageOf (operation: RequestedPackageOperation) =
        match operation with
        | RequestedPackageOperation.InstallLatest package
        | RequestedPackageOperation.InstallVersion(package, _)
        | RequestedPackageOperation.UpdateLatest package
        | RequestedPackageOperation.UpdateVersion(package, _)
        | RequestedPackageOperation.Uninstall package
        | RequestedPackageOperation.ConsolidateVersion(package, _) -> package

    let private targetProject (target: PackageTargetScope) =
        match target with
        | PackageTargetScope.Project project
        | PackageTargetScope.Framework(project, _)
        | PackageTargetScope.Runtime(project, _, _) -> project

    let private pathIdentity sensitivity path =
        let full = Path.GetFullPath path

        if sensitivity = FileSystemCaseSensitivity.Insensitive then
            full.ToUpperInvariant()
        else
            full

    let private targetKey sensitivity (target: PackageTargetScope) =
        match target with
        | PackageTargetScope.Project project -> pathIdentity sensitivity project.Value, "", ""
        | PackageTargetScope.Framework(project, framework) ->
            pathIdentity sensitivity project.Value, framework.Value, ""
        | PackageTargetScope.Runtime(project, framework, runtime) ->
            pathIdentity sensitivity project.Value, framework.Value, runtime.Value

    let private sameProject sensitivity (left: PackageTargetScope) (right: PackageTargetScope) =
        String.Equals(
            (targetProject left).Value,
            (targetProject right).Value,
            if sensitivity = FileSystemCaseSensitivity.Insensitive then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal
        )

    let private expandTargets
        sensitivity
        (installed: InstalledPackageGraph list)
        (requested: NonEmptyList<PackageTargetScope>)
        =
        requested
        |> NonEmptyList.toList
        |> List.collect (fun target ->
            match target with
            | PackageTargetScope.Project _ ->
                let restored =
                    installed |> List.map _.Target |> List.filter (sameProject sensitivity target)

                if List.isEmpty restored then [ target ] else restored
            | _ -> [ target ])
        |> List.distinctBy (targetKey sensitivity)
        |> List.sortBy (targetKey sensitivity)

    let private graphFor
        sensitivity
        (target: PackageTargetScope)
        (graphs: InstalledPackageGraph list)
        =
        let key = targetKey sensitivity target
        graphs |> List.filter (fun graph -> targetKey sensitivity graph.Target = key)

    let private currentPackage (package: PackageId) (graph: InstalledPackageGraph) =
        graph.Packages
        |> List.filter (fun installed ->
            String.Equals(
                installed.Identity.Value,
                package.Value,
                StringComparison.OrdinalIgnoreCase
            ))
        |> function
            | [] -> Ok None
            | [ installed ] -> Ok(Some installed)
            | _ -> Error(invalid "The restored package graph contains duplicate package entries.")

    let private targetState
        sensitivity
        (package: PackageId)
        (graphs: InstalledPackageGraph list)
        (target: PackageTargetScope)
        =
        match graphFor sensitivity target graphs with
        | [] ->
            Error(
                stale
                    "The selected target has no restored package graph; refresh packages before previewing changes."
            )
        | [ graph ] ->
            match graph.State with
            | InstalledPackageGraphState.Current ->
                currentPackage package graph
                |> Result.map (fun installed -> installed, PackageGraphFreshness.Current)
            | InstalledPackageGraphState.UnverifiablyFreshRestoreGraph ->
                currentPackage package graph
                |> Result.map (fun installed ->
                    installed, PackageGraphFreshness.AwaitingBackgroundRestore)
            | InstalledPackageGraphState.MissingRestoreGraph ->
                Error(stale "The selected target has not been restored.")
            | InstalledPackageGraphState.MismatchedRestoreGraph ->
                Error(stale "The selected target restore graph belongs to another project state.")
            | InstalledPackageGraphState.StaleRestoreGraph ->
                Error(stale "The selected target restore graph is stale.")
        | _ -> Error(invalid "The restored package graph contains duplicate target entries.")

    let private selectedVersion
        sensitivity
        (operation: RequestedPackageOperation)
        (package: PackageId)
        (project: PackageProjectId)
        (details: Map<PackageId * PackageProjectId * NuGetVersion, PackageDetails>)
        =
        match operation with
        | RequestedPackageOperation.InstallVersion(_, version)
        | RequestedPackageOperation.UpdateVersion(_, version)
        | RequestedPackageOperation.ConsolidateVersion(_, version) -> Ok(Some version)
        | RequestedPackageOperation.InstallLatest _
        | RequestedPackageOperation.UpdateLatest _ ->
            details
            |> Map.toList
            |> List.choose (fun ((candidate, candidateProject, _), value) ->
                if
                    String.Equals(
                        candidate.Value,
                        package.Value,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && pathIdentity sensitivity candidateProject.Value = pathIdentity
                        sensitivity
                        project.Value
                then
                    Some value
                else
                    None)
            |> List.sortByDescending (fun value ->
                NuGet.Versioning.NuGetVersion.Parse(value.Summary.Version.Value))
            |> List.tryHead
            |> function
                | Some value -> Ok(Some value.Summary.Version)
                | None ->
                    Error(
                        unsupported
                            "Package metadata for the selected package is required to preview a latest-version change."
                    )
        | RequestedPackageOperation.Uninstall _ -> Ok None

    let private resolvedVersion (installed: InstalledPackage option) =
        match installed |> Option.map _.State with
        | Some(InstalledPackageState.Direct(_, version))
        | Some(InstalledPackageState.CentrallyManagedDirect(_, version, _))
        | Some(InstalledPackageState.Transitive version)
        | Some(InstalledPackageState.FrameworkProvided version) -> Some version
        | _ -> None

    let private consolidationPosition
        (destination: NuGetVersion)
        (installed: InstalledPackage option)
        =
        match resolvedVersion installed with
        | None -> PackageConsolidationPosition.Unusable
        | Some current ->
            try
                let currentVersion = NuGet.Versioning.NuGetVersion.Parse current.Value
                let destinationVersion = NuGet.Versioning.NuGetVersion.Parse destination.Value
                let comparison = currentVersion.CompareTo destinationVersion

                if comparison = 0 then
                    PackageConsolidationPosition.AlreadyOnDestination
                elif comparison < 0 then
                    PackageConsolidationPosition.BelowDestination
                else
                    PackageConsolidationPosition.AboveDestination
            with :? ArgumentException ->
                PackageConsolidationPosition.Unusable

    let private targetChange
        (operation: RequestedPackageOperation)
        (version: NuGetVersion option)
        (ownership: PackageOwnership)
        (installed: InstalledPackage option)
        =
        match operation, version, installed with
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          Some selected,
          current ->
            Ok(
                PackageTargetChange.Install(
                    current |> Option.map _.State,
                    PackageOwnership.proposed selected ownership
                )
            )
        | (RequestedPackageOperation.UpdateLatest _ | RequestedPackageOperation.UpdateVersion _),
          Some selected,
          Some current ->
            Ok(
                PackageTargetChange.Update(
                    current.State,
                    PackageOwnership.proposed selected ownership
                )
            )
        | RequestedPackageOperation.Uninstall _, _, Some current ->
            Ok(PackageTargetChange.Uninstall current.State)
        | RequestedPackageOperation.ConsolidateVersion(_, destination), Some _, current ->
            let position = consolidationPosition destination current

            let proposed =
                match position with
                | PackageConsolidationPosition.BelowDestination
                | PackageConsolidationPosition.AboveDestination ->
                    Some(PackageOwnership.proposed destination ownership)
                | PackageConsolidationPosition.AlreadyOnDestination
                | PackageConsolidationPosition.Unusable -> None

            Ok(PackageTargetChange.Consolidate(current |> Option.map _.State, position, proposed))
        | _ -> Error(invalid "The package operation does not identify a valid state change.")

    let private validateCurrent
        (operation: RequestedPackageOperation)
        (installed: InstalledPackage option)
        =
        match operation, installed with
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          Some { State = InstalledPackageState.Direct _ }
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          Some { State = InstalledPackageState.CentrallyManagedDirect _ }
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          Some { State = InstalledPackageState.UnresolvedDirect _ }
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          Some { State = InstalledPackageState.UnresolvedCentrallyManagedDirect _ } ->
            Error(invalid "The selected package is already declared directly in this target.")
        | (RequestedPackageOperation.UpdateLatest _ | RequestedPackageOperation.UpdateVersion _ | RequestedPackageOperation.Uninstall _),
          None -> Error(invalid "The selected package is not installed in this target.")
        | _ -> Ok()

    let private frameworkOf (target: PackageTargetScope) =
        match target with
        | PackageTargetScope.Project _ -> None
        | PackageTargetScope.Framework(_, framework)
        | PackageTargetScope.Runtime(_, framework, _) -> Some framework

    let private compatibleDependencies
        (target: PackageTargetScope)
        (groups: Map<TargetFramework option, (PackageId * NuGetVersionRange) list>)
        =
        match frameworkOf target with
        | None -> Map.tryFind None groups |> Option.defaultValue []
        | Some framework ->
            let targetFramework = NuGet.Frameworks.NuGetFramework.ParseFolder framework.Value

            let candidates =
                groups
                |> Map.toList
                |> List.choose (fun (candidate, dependencies) ->
                    candidate
                    |> Option.map (fun value ->
                        NuGet.Frameworks.NuGetFramework.ParseFolder value.Value, dependencies))

            let nearest =
                NuGet.Frameworks
                    .FrameworkReducer()
                    .GetNearest(targetFramework, candidates |> List.map fst)

            Option.ofObj nearest
            |> Option.bind (fun selected ->
                candidates |> List.tryFind (fun (candidate, _) -> candidate = selected))
            |> Option.map snd
            |> Option.orElseWith (fun () -> Map.tryFind None groups)
            |> Option.defaultValue []

    let private metadataImpact
        sensitivity
        (package: PackageId)
        (project: PackageProjectId)
        (effectiveVersion: NuGetVersion option)
        (target: PackageTargetScope)
        (details: Map<PackageId * PackageProjectId * NuGetVersion, PackageDetails>)
        =
        effectiveVersion
        |> Option.bind (fun version ->
            details
            |> Map.toList
            |> List.tryPick (fun ((candidate, candidateProject, candidateVersion), value) ->
                if
                    String.Equals(
                        candidate.Value,
                        package.Value,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && pathIdentity sensitivity candidateProject.Value = pathIdentity
                        sensitivity
                        project.Value
                    && candidateVersion = version
                then
                    Some(version, value)
                else
                    None))
        |> Option.filter (fun (version, value) ->
            value.Summary.Version = version
            && String.Equals(
                value.Summary.Identity.Value,
                package.Value,
                StringComparison.OrdinalIgnoreCase
            ))
        |> function
            | None -> PackageMetadataImpact.Unknown
            | Some(_, value) ->
                let dependencies =
                    compatibleDependencies target value.DependencyGroups
                    |> List.sortBy (fun (identity, range) -> identity.Value, range.Value)

                let vulnerabilities =
                    value.Vulnerabilities
                    |> List.sortBy (fun item -> item.Severity, item.Advisory.AbsoluteUri)

                PackageMetadataImpact.Known(
                    dependencies,
                    value.Deprecation,
                    vulnerabilities,
                    value.License
                )

    let private sortedSources (sources: PackageSourceId list) =
        sources |> List.distinct |> List.sortBy _.Value

    let private mappingImpact
        (package: PackageId)
        (browseSource: PackageSourceId option)
        (freshness: PackageGraphFreshness)
        (policy: PackageSourceMappingPolicy)
        =
        let unknown sources =
            PackageSourceMappingImpact.UnknownTransitiveConsequences(
                sortedSources sources,
                browseSource
            )

        match policy with
        | PackageSourceMappingPolicy.KnownConflict(conflict, _) when conflict = package ->
            Error(
                unsupported
                    $"Package source mapping does not allow '{package.Value}' for the selected operation."
            )
        | PackageSourceMappingPolicy.KnownConflict(_, sources) -> Ok(unknown sources)
        | PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence sources ->
            Ok(unknown sources)
        | PackageSourceMappingPolicy.Allowed sources when
            freshness = PackageGraphFreshness.AwaitingBackgroundRestore
            ->
            Ok(unknown sources)
        | PackageSourceMappingPolicy.Allowed sources ->
            let allowed = sortedSources sources

            match browseSource with
            | Some browsed ->
                Ok(PackageSourceMappingImpact.BrowseSourceDoesNotConstrainApply(browsed, allowed))
            | None -> Ok(PackageSourceMappingImpact.ApplyAllowed allowed)

    let private pathKey sensitivity path = pathIdentity sensitivity path

    let private orderedPaths root paths =
        paths
        |> List.map Path.GetFullPath
        |> List.distinctBy (pathKey root)
        |> List.sortBy (pathKey root)

    let private normalizeFingerprints sensitivity (fingerprints: Map<string, string>) =
        let keyed =
            fingerprints
            |> Map.toList
            |> List.map (fun (path, fingerprint) ->
                pathKey sensitivity path, (Path.GetFullPath path, fingerprint))

        if keyed |> List.countBy fst |> List.exists (snd >> ((<) 1)) then
            Error(stale "Package preview fingerprints contain ambiguous path identities.")
        else
            Ok(Map.ofList keyed)

    let private verifyPrecondition
        (precondition: PackagePreviewPrecondition)
        (evidence: PackageOperationPreviewEvidence)
        (ownerFiles: string list)
        =
        if precondition.WorkspaceRevision <> evidence.WorkspaceRevision then
            Error(stale "The workspace revision changed before the package preview was created.")
        else
            let normalized =
                match
                    normalizeFingerprints evidence.CaseSensitivity precondition.FileFingerprints,
                    normalizeFingerprints evidence.CaseSensitivity evidence.FileFingerprints
                with
                | Ok expected, Ok current -> Ok(expected, current)
                | Error error, _
                | _, Error error -> Error error

            match normalized with
            | Error error -> Error error
            | Ok(expected, current) ->
                let unchanged =
                    ownerFiles
                    |> List.forall (fun path ->
                        let key = pathKey evidence.CaseSensitivity path

                        match Map.tryFind key expected, Map.tryFind key current with
                        | Some(_, expectedValue), Some(_, currentValue) ->
                            expectedValue = currentValue
                        | _ -> false)

                if unchanged then
                    ownerFiles
                    |> List.map (fun path ->
                        let key = pathKey evidence.CaseSensitivity path
                        let currentPath, fingerprint = current[key]
                        currentPath, fingerprint)
                    |> Map.ofList
                    |> Ok
                else
                    Error(stale "A package owner file changed before the preview was created.")

    let private planTarget
        (evidence: PackageOperationPreviewEvidence)
        (operation: RequestedPackageOperation)
        (browseSource: PackageSourceId option)
        (package: PackageId)
        (version: NuGetVersion option)
        (target: PackageTargetScope)
        =
        targetState evidence.CaseSensitivity package evidence.Installed target
        |> Result.bind (fun (installed, freshness) ->
            validateCurrent operation installed
            |> Result.bind (fun () ->
                let project = targetProject target

                let policy =
                    evidence.SourceMappings
                    |> Map.toList
                    |> List.tryPick (fun ((candidatePackage, candidateProject), policy) ->
                        if
                            String.Equals(
                                candidatePackage.Value,
                                package.Value,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && pathIdentity evidence.CaseSensitivity candidateProject.Value = pathIdentity
                                evidence.CaseSensitivity
                                project.Value
                        then
                            Some policy
                        else
                            None)
                    |> Option.defaultValue (
                        PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence []
                    )

                mappingImpact package browseSource freshness policy
                |> Result.bind (fun mapping ->
                    PackageOwnership.resolve
                        evidence.WorkspaceRoot
                        evidence.CaseSensitivity
                        evidence.Evaluations
                        operation
                        package
                        target
                        installed
                    |> Result.mapError (PackageOwnership.failureMessage >> unsupported)
                    |> Result.bind (fun ownership ->
                        targetChange operation version ownership installed
                        |> Result.bind (fun change ->
                            let owners =
                                PackageOwnership.ownerFiles operation ownership
                                |> orderedPaths evidence.CaseSensitivity
                                |> NonEmptyList.tryCreate
                                |> Option.defaultWith (fun () ->
                                    invalidOp
                                        "Package ownership must identify at least one file.")

                            let effectiveMetadataVersion =
                                match operation with
                                | RequestedPackageOperation.Uninstall _ ->
                                    resolvedVersion installed
                                | _ -> version

                            let impact =
                                { Metadata =
                                    metadataImpact
                                        evidence.CaseSensitivity
                                        package
                                        project
                                        effectiveMetadataVersion
                                        target
                                        evidence.Details
                                  SourceMapping = mapping
                                  Restore =
                                    PackageRestoreImpact.RequiredWithUnknownOutcome freshness }

                            PackageTargetPreview.create target change owners freshness impact
                            |> Result.mapError (fun violation ->
                                invalid
                                    $"The package target preview is invalid ({violation})."))))))

    let private collect results =
        results
        |> List.fold
            (fun state item ->
                state
                |> Result.bind (fun previews ->
                    item |> Result.map (fun preview -> preview :: previews)))
            (Ok [])
        |> Result.map List.rev

    let plan
        (request: PackageRequest<PackageOperationRequest>)
        (evidence: PackageOperationPreviewEvidence)
        =
        let package = packageOf request.Value.Operation

        let targets =
            expandTargets evidence.CaseSensitivity evidence.Installed request.Value.Targets

        match
            targets
            |> List.map (fun target ->
                selectedVersion
                    evidence.CaseSensitivity
                    request.Value.Operation
                    package
                    (targetProject target)
                    evidence.Details
                |> Result.bind (fun version ->
                    planTarget
                        evidence
                        request.Value.Operation
                        request.Value.BrowseSource
                        package
                        version
                        target))
            |> collect
        with
        | Error error -> Error error
        | Ok previews ->
            match NonEmptyList.tryCreate previews with
            | None -> Error(invalid "A package preview requires at least one target.")
            | Some previewTargets ->
                let owners =
                    previews
                    |> List.collect (PackageTargetPreview.ownerFiles >> NonEmptyList.toList)
                    |> orderedPaths evidence.CaseSensitivity

                match NonEmptyList.tryCreate owners with
                | None -> Error(invalid "A package preview requires an owner file.")
                | Some ownerFiles ->
                    match verifyPrecondition request.Value.Precondition evidence owners with
                    | Error error -> Error error
                    | Ok fingerprints ->
                        PackagePreview.create
                            (if
                                 evidence.CaseSensitivity = FileSystemCaseSensitivity.Insensitive
                             then
                                 StringComparison.OrdinalIgnoreCase
                             else
                                 StringComparison.Ordinal)
                            request.Value.Operation
                            previewTargets
                            ownerFiles
                            evidence.WorkspaceRevision
                            fingerprints
                        |> Result.mapError (fun violation ->
                            invalid $"The package preview is invalid ({violation}).")

    let private updateOperation selection =
        match PackageUpdateSelection.requestedVersion selection with
        | Some version ->
            RequestedPackageOperation.UpdateVersion(
                PackageUpdateSelection.package selection,
                version
            )
        | None -> RequestedPackageOperation.UpdateLatest(PackageUpdateSelection.package selection)

    let private packageTargetKey sensitivity (package: PackageId) (target: PackageTargetScope) =
        package.Value.ToUpperInvariant(), targetKey sensitivity target

    let planUpdateBatch
        (request: PackageRequest<PackageUpdateBatchRequest>)
        (evidence: PackageOperationPreviewEvidence)
        =
        let updates =
            request.Value.Updates
            |> NonEmptyList.toList
            |> List.collect (fun selection ->
                expandTargets
                    evidence.CaseSensitivity
                    evidence.Installed
                    (NonEmptyList.singleton (PackageUpdateSelection.target selection))
                |> List.map (fun target -> selection, target))
            |> List.sortBy (fun (selection, target) ->
                packageTargetKey
                    evidence.CaseSensitivity
                    (PackageUpdateSelection.package selection)
                    target)

        let duplicateSelection =
            updates
            |> List.countBy (fun (selection, target) ->
                packageTargetKey
                    evidence.CaseSensitivity
                    (PackageUpdateSelection.package selection)
                    target)
            |> List.exists (snd >> ((<) 1))

        if duplicateSelection then
            Error(invalid "A package update batch contains duplicate package-target entries.")
        else
            let planned =
                updates
                |> List.map (fun (selection, target) ->
                    let operation = updateOperation selection
                    let package = PackageUpdateSelection.package selection

                    selectedVersion
                        evidence.CaseSensitivity
                        operation
                        package
                        (targetProject target)
                        evidence.Details
                    |> Result.bind (fun version ->
                        planTarget
                            evidence
                            operation
                            request.Value.BrowseSource
                            package
                            version
                            target)
                    |> Result.map (fun preview ->
                        PackageUpdateTargetPreview.create
                            package
                            (PackageUpdateSelection.requestedVersion selection)
                            preview))
                |> collect

            match planned with
            | Error error -> Error error
            | Ok previews ->
                match NonEmptyList.tryCreate previews with
                | None -> Error(invalid "A package update preview requires at least one update.")
                | Some previewUpdates ->
                    let owners =
                        previews
                        |> List.collect (
                            PackageUpdateTargetPreview.target
                            >> PackageTargetPreview.ownerFiles
                            >> NonEmptyList.toList
                        )
                        |> orderedPaths evidence.CaseSensitivity

                    match NonEmptyList.tryCreate owners with
                    | None -> Error(invalid "A package update preview requires an owner file.")
                    | Some ownerFiles ->
                        match verifyPrecondition request.Value.Precondition evidence owners with
                        | Error error -> Error error
                        | Ok fingerprints ->
                            PackageUpdateBatchPreview.create
                                (if
                                     evidence.CaseSensitivity = FileSystemCaseSensitivity.Insensitive
                                 then
                                     StringComparison.OrdinalIgnoreCase
                                 else
                                     StringComparison.Ordinal)
                                previewUpdates
                                ownerFiles
                                evidence.WorkspaceRevision
                                fingerprints
                            |> Result.mapError (fun violation ->
                                invalid $"The package update preview is invalid ({violation}).")

    let create (readEvidence: ReadPackageOperationPreviewEvidence) : PreviewPackageOperation =
        fun request ->
            async {
                let! evidence = readEvidence request
                return evidence |> Result.bind (plan request)
            }

    let createUpdateBatch
        (readEvidence: ReadPackageUpdateBatchPreviewEvidence)
        : PreviewPackageUpdateBatch =
        fun request ->
            async {
                let! evidence = readEvidence request
                return evidence |> Result.bind (planUpdateBatch request)
            }
