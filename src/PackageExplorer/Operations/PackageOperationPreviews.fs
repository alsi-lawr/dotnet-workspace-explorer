namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.IO
open Dotnet.WorkspaceExplorer.Packages

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

    let private targetKey (target: PackageTargetScope) =
        match target with
        | PackageTargetScope.Project project -> project.Value, "", ""
        | PackageTargetScope.Framework(project, framework) -> project.Value, framework.Value, ""
        | PackageTargetScope.Runtime(project, framework, runtime) ->
            project.Value, framework.Value, runtime.Value

    let private sameProject (left: PackageTargetScope) (right: PackageTargetScope) =
        String.Equals(
            (targetProject left).Value,
            (targetProject right).Value,
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal
        )

    let private expandTargets
        (installed: InstalledPackageGraph list)
        (requested: NonEmptyList<PackageTargetScope>)
        =
        requested
        |> NonEmptyList.toList
        |> List.collect (fun target ->
            match target with
            | PackageTargetScope.Project _ ->
                let restored = installed |> List.map _.Target |> List.filter (sameProject target)

                if List.isEmpty restored then [ target ] else restored
            | _ -> [ target ])
        |> List.distinct
        |> List.sortBy targetKey

    let private graphFor (target: PackageTargetScope) (graphs: InstalledPackageGraph list) =
        graphs |> List.filter (fun graph -> graph.Target = target)

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
        (package: PackageId)
        (graphs: InstalledPackageGraph list)
        (target: PackageTargetScope)
        =
        match graphFor target graphs with
        | [] ->
            Error(
                stale
                    "The selected target has no restored package graph; refresh packages before previewing changes."
            )
        | [ graph ] ->
            match graph.State with
            | InstalledPackageGraphState.Current
            | InstalledPackageGraphState.UnverifiablyFreshRestoreGraph ->
                currentPackage package graph
            | InstalledPackageGraphState.MissingRestoreGraph ->
                Error(stale "The selected target has not been restored.")
            | InstalledPackageGraphState.MismatchedRestoreGraph ->
                Error(stale "The selected target restore graph belongs to another project state.")
            | InstalledPackageGraphState.StaleRestoreGraph ->
                Error(stale "The selected target restore graph is stale.")
        | _ -> Error(invalid "The restored package graph contains duplicate target entries.")

    let private selectedVersion
        (operation: RequestedPackageOperation)
        (details: PackageDetails option)
        =
        match operation with
        | RequestedPackageOperation.InstallVersion(_, version)
        | RequestedPackageOperation.UpdateVersion(_, version)
        | RequestedPackageOperation.ConsolidateVersion(_, version) -> Ok(Some version)
        | RequestedPackageOperation.InstallLatest _
        | RequestedPackageOperation.UpdateLatest _ ->
            match details with
            | Some value -> Ok(Some value.Summary.Version)
            | None ->
                Error(
                    unsupported
                        "Package metadata is required to preview a latest-version package change."
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

    let private proposedState
        (operation: RequestedPackageOperation)
        (version: NuGetVersion option)
        (ownership: PackageOwnership)
        (installed: InstalledPackage option)
        =
        match operation, version with
        | RequestedPackageOperation.Uninstall _, _ -> Ok ProposedPackageState.NotInstalled, None
        | RequestedPackageOperation.ConsolidateVersion(_, destination), Some _ ->
            let position = consolidationPosition destination installed

            let proposed =
                match position with
                | PackageConsolidationPosition.BelowDestination
                | PackageConsolidationPosition.AboveDestination ->
                    PackageOwnership.proposed destination ownership
                | PackageConsolidationPosition.AlreadyOnDestination
                | PackageConsolidationPosition.Unusable -> ProposedPackageState.Unchanged

            Ok proposed, Some position
        | _, Some selected -> Ok(PackageOwnership.proposed selected ownership), None
        | _ -> Error(invalid "The package operation does not identify a proposed version."), None

    let private validateCurrent
        (operation: RequestedPackageOperation)
        (installed: InstalledPackage option)
        =
        match operation, installed with
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          Some { State = InstalledPackageState.Direct _ }
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          Some { State = InstalledPackageState.CentrallyManagedDirect _ } ->
            Error(invalid "The selected package is already installed directly in this target.")
        | (RequestedPackageOperation.UpdateLatest _ | RequestedPackageOperation.UpdateVersion _ | RequestedPackageOperation.Uninstall _),
          None -> Error(invalid "The selected package is not installed in this target.")
        | _ -> Ok()

    let private frameworkOf (target: PackageTargetScope) =
        match target with
        | PackageTargetScope.Project _ -> None
        | PackageTargetScope.Framework(_, framework)
        | PackageTargetScope.Runtime(_, framework, _) -> Some framework

    let private metadataImpact
        (package: PackageId)
        (version: NuGetVersion option)
        (target: PackageTargetScope)
        (details: PackageDetails option)
        =
        let matches =
            details
            |> Option.filter (fun value ->
                String.Equals(
                    value.Summary.Identity.Value,
                    package.Value,
                    StringComparison.OrdinalIgnoreCase
                )
                && (version |> Option.forall (fun selected -> selected = value.Summary.Version)))

        match matches with
        | None -> PackageMetadataImpact.Unknown
        | Some value ->
            let dependencies =
                frameworkOf target
                |> Option.bind (fun framework ->
                    Map.tryFind (Some framework) value.DependencyGroups)
                |> Option.orElseWith (fun () -> Map.tryFind None value.DependencyGroups)
                |> Option.defaultValue []
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
        (browseSource: PackageSourceId option)
        (policy: PackageSourceMappingPolicy)
        =
        match policy with
        | PackageSourceMappingPolicy.KnownConflict(package, _) ->
            Error(
                unsupported
                    $"Package source mapping does not allow '{package.Value}' for the selected operation."
            )
        | PackageSourceMappingPolicy.Allowed sources ->
            let allowed = sortedSources sources

            match browseSource with
            | Some browsed ->
                Ok(PackageSourceMappingImpact.BrowseSourceDoesNotConstrainApply(browsed, allowed))
            | None -> Ok(PackageSourceMappingImpact.ApplyAllowed allowed)
        | PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence sources ->
            Ok(
                PackageSourceMappingImpact.UnknownTransitiveConsequences(
                    sortedSources sources,
                    browseSource
                )
            )

    let private normalizeFingerprints (fingerprints: Map<string, string>) =
        fingerprints
        |> Map.toList
        |> List.map (fun (path, fingerprint) -> Path.GetFullPath path, fingerprint)
        |> Map.ofList

    let private verifyPrecondition
        (request: PackageOperationRequest)
        (evidence: PackageOperationPreviewEvidence)
        (ownerFiles: string list)
        =
        if request.Precondition.WorkspaceRevision <> evidence.WorkspaceRevision then
            Error(stale "The workspace revision changed before the package preview was created.")
        else
            let expected = normalizeFingerprints request.Precondition.FileFingerprints
            let current = normalizeFingerprints evidence.FileFingerprints

            let unchanged =
                ownerFiles
                |> List.forall (fun path ->
                    match Map.tryFind path expected, Map.tryFind path current with
                    | Some expectedValue, Some currentValue -> expectedValue = currentValue
                    | _ -> false)

            if unchanged then
                Ok(ownerFiles |> List.map (fun path -> path, current[path]) |> Map.ofList)
            else
                Error(stale "A package owner file changed before the preview was created.")

    let private planTarget
        (evidence: PackageOperationPreviewEvidence)
        (request: PackageOperationRequest)
        (package: PackageId)
        (version: NuGetVersion option)
        (mapping: PackageSourceMappingImpact)
        (target: PackageTargetScope)
        =
        targetState package evidence.Installed target
        |> Result.bind (fun installed ->
            validateCurrent request.Operation installed
            |> Result.bind (fun () ->
                PackageOwnership.resolve
                    evidence.WorkspaceRoot
                    evidence.Evaluations
                    request.Operation
                    package
                    target
                    installed
                |> Result.mapError (PackageOwnership.failureMessage >> unsupported)
                |> Result.bind (fun ownership ->
                    let proposed, consolidation =
                        proposedState request.Operation version ownership installed

                    proposed
                    |> Result.map (fun proposedState ->
                        let owners =
                            PackageOwnership.ownerFiles request.Operation ownership
                            |> List.map Path.GetFullPath
                            |> List.distinct
                            |> List.sort
                            |> NonEmptyList.tryCreate
                            |> Option.defaultWith (fun () ->
                                invalidOp "Package ownership must identify at least one file.")

                        { Target = target
                          Current = installed |> Option.map _.State
                          Proposed = proposedState
                          OwnerFiles = owners
                          Consolidation = consolidation
                          Impact =
                            { Metadata = metadataImpact package version target evidence.Details
                              SourceMapping = mapping
                              Restore = PackageRestoreImpact.RequiredWithUnknownOutcome } }))))

    let private collect (results: Result<PackageTargetPreview, PackageFailure> list) =
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
        let targets = expandTargets evidence.Installed request.Value.Targets

        match selectedVersion request.Value.Operation evidence.Details with
        | Error error -> Error error
        | Ok version ->
            match mappingImpact request.Value.BrowseSource evidence.SourceMapping with
            | Error error -> Error error
            | Ok mapping ->
                match
                    targets
                    |> List.map (planTarget evidence request.Value package version mapping)
                    |> collect
                with
                | Error error -> Error error
                | Ok previews ->
                    match NonEmptyList.tryCreate previews with
                    | None -> Error(invalid "A package preview requires at least one target.")
                    | Some previewTargets ->
                        let owners =
                            previews
                            |> List.collect (_.OwnerFiles >> NonEmptyList.toList)
                            |> List.distinct
                            |> List.sort

                        match NonEmptyList.tryCreate owners with
                        | None -> Error(invalid "A package preview requires an owner file.")
                        | Some ownerFiles ->
                            match verifyPrecondition request.Value evidence owners with
                            | Error error -> Error error
                            | Ok fingerprints ->
                                PackagePreview.create
                                    request.Value.Operation
                                    previewTargets
                                    ownerFiles
                                    evidence.WorkspaceRevision
                                    fingerprints
                                |> Result.mapError (fun violation ->
                                    invalid $"The package preview is invalid ({violation}).")

    let create (readEvidence: ReadPackageOperationPreviewEvidence) : PreviewPackageOperation =
        fun request ->
            async {
                let! evidence = readEvidence request
                return evidence |> Result.bind (plan request)
            }
