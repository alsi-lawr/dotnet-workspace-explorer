namespace Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
type RequestedPackageOperation =
    | InstallLatest of package: PackageId
    | InstallVersion of package: PackageId * version: NuGetVersion
    | UpdateLatest of package: PackageId
    | UpdateVersion of package: PackageId * version: NuGetVersion
    | Uninstall of package: PackageId
    | ConsolidateVersion of package: PackageId * destination: NuGetVersion

type PackagePreviewPrecondition =
    { WorkspaceRevision: string
      FileFingerprints: Map<string, string> }

type PackagePreviewPreconditionRequest =
    { Operation: RequestedPackageOperation
      Targets: NonEmptyList<PackageTargetScope>
      BrowseSource: PackageSourceId option }

type PackageOperationRequest =
    { Operation: RequestedPackageOperation
      Targets: NonEmptyList<PackageTargetScope>
      BrowseSource: PackageSourceId option
      Precondition: PackagePreviewPrecondition }

type PackageUpdateSelection =
    private
        { Package: PackageId
          Version: NuGetVersion option
          Target: PackageTargetScope }

[<RequireQualifiedAccess>]
module PackageUpdateSelection =
    let latest package target =
        { Package = package
          Version = None
          Target = target }

    let version package version target =
        { Package = package
          Version = Some version
          Target = target }

    let package selection = selection.Package
    let requestedVersion selection = selection.Version
    let target selection = selection.Target

type PackageUpdateBatchPreconditionRequest =
    { Updates: NonEmptyList<PackageUpdateSelection>
      BrowseSource: PackageSourceId option }

type PackageUpdateBatchRequest =
    { Updates: NonEmptyList<PackageUpdateSelection>
      BrowseSource: PackageSourceId option
      Precondition: PackagePreviewPrecondition }

[<RequireQualifiedAccess>]
type ProposedPackageState =
    | Direct of version: NuGetVersion
    | CentrallyManaged of version: NuGetVersion * ownerFile: string

[<RequireQualifiedAccess>]
type PackageConsolidationPosition =
    | AlreadyOnDestination
    | BelowDestination
    | AboveDestination
    | Unusable

[<RequireQualifiedAccess>]
type PackageGraphFreshness =
    | Current
    | AwaitingBackgroundRestore

[<RequireQualifiedAccess>]
type PackageTargetChange =
    | Install of current: InstalledPackageState option * proposed: ProposedPackageState
    | Update of current: InstalledPackageState * proposed: ProposedPackageState
    | Uninstall of current: InstalledPackageState
    | Consolidate of
        current: InstalledPackageState option *
        position: PackageConsolidationPosition *
        proposed: ProposedPackageState option

[<RequireQualifiedAccess>]
type PackageMetadataImpact =
    | Known of
        dependencies: (PackageId * NuGetVersionRange) list *
        deprecation: PackageDeprecation *
        vulnerabilities: PackageVulnerability list *
        license: string option
    | Unknown

[<RequireQualifiedAccess>]
type PackageSourceMappingImpact =
    | ApplyAllowed of sources: PackageSourceId list
    | BrowseSourceDoesNotConstrainApply of
        browseSource: PackageSourceId *
        allowedSources: PackageSourceId list
    | UnknownTransitiveConsequences of
        allowedSources: PackageSourceId list *
        browseSource: PackageSourceId option

[<RequireQualifiedAccess>]
type PackageRestoreImpact = RequiredWithUnknownOutcome of graph: PackageGraphFreshness

type PackageTargetImpact =
    { Metadata: PackageMetadataImpact
      SourceMapping: PackageSourceMappingImpact
      Restore: PackageRestoreImpact }

type PackageTargetPreview =
    private
        { Target: PackageTargetScope
          Change: PackageTargetChange
          OwnerFiles: NonEmptyList<string>
          GraphFreshness: PackageGraphFreshness
          Impact: PackageTargetImpact }

[<RequireQualifiedAccess>]
module PackageTargetPreview =
    let internal create target change ownerFiles graphFreshness impact =
        let owners = ownerFiles |> NonEmptyList.toList

        let restoreMatches =
            match impact.Restore with
            | PackageRestoreImpact.RequiredWithUnknownOutcome evidence -> evidence = graphFreshness

        if owners |> List.exists System.String.IsNullOrWhiteSpace then
            Error(PackageContractViolation.MissingValue "ownerFiles")
        elif not restoreMatches then
            Error(PackageContractViolation.InvalidValue "graphFreshness")
        else
            Ok
                { Target = target
                  Change = change
                  OwnerFiles = ownerFiles
                  GraphFreshness = graphFreshness
                  Impact = impact }

    let target preview = preview.Target
    let change preview = preview.Change
    let ownerFiles preview = preview.OwnerFiles
    let graphFreshness preview = preview.GraphFreshness
    let impact preview = preview.Impact

type PackagePreview =
    private
        { Operation: RequestedPackageOperation
          Targets: NonEmptyList<PackageTargetPreview>
          OwnerFiles: NonEmptyList<string>
          WorkspaceRevision: string
          FileFingerprints: Map<string, string>
          ConfirmationToken: string }

[<RequireQualifiedAccess>]
module PackagePreview =
    let private createConfirmationToken
        (operation: RequestedPackageOperation)
        (targets: NonEmptyList<PackageTargetPreview>)
        (ownerFiles: NonEmptyList<string>)
        (workspaceRevision: string)
        (fileFingerprints: Map<string, string>)
        =
        let values =
            [ $"operation:{operation:A}"
              $"targets:{targets:A}"
              yield!
                  ownerFiles
                  |> NonEmptyList.toList
                  |> List.map (fun value -> $"owner:{value.Length}:{value}")
              $"revision:{workspaceRevision.Length}:{workspaceRevision}"
              yield!
                  fileFingerprints
                  |> Map.toList
                  |> List.map (fun (path, fingerprint) ->
                      $"fingerprint:{path.Length}:{path}:{fingerprint.Length}:{fingerprint}") ]

        values
        |> String.concat "\u0000"
        |> System.Text.Encoding.UTF8.GetBytes
        |> System.Security.Cryptography.SHA256.HashData
        |> System.Convert.ToHexString

    let private operationMatches operation target =
        match operation, PackageTargetPreview.change target with
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          PackageTargetChange.Install _
        | (RequestedPackageOperation.UpdateLatest _ | RequestedPackageOperation.UpdateVersion _),
          PackageTargetChange.Update _
        | RequestedPackageOperation.Uninstall _, PackageTargetChange.Uninstall _
        | RequestedPackageOperation.ConsolidateVersion _, PackageTargetChange.Consolidate _ -> true
        | _ -> false

    let private proposedVersion =
        function
        | ProposedPackageState.Direct version
        | ProposedPackageState.CentrallyManaged(version, _) -> version

    let private currentVersion =
        function
        | InstalledPackageState.Direct(_, version)
        | InstalledPackageState.CentrallyManagedDirect(_, version, _)
        | InstalledPackageState.Transitive version
        | InstalledPackageState.FrameworkProvided version -> Some version
        | InstalledPackageState.FrameworkProvidedWithoutVersion
        | InstalledPackageState.UnresolvedDirect _
        | InstalledPackageState.UnresolvedCentrallyManagedDirect _ -> None

    let private proposalMatchesOwner comparison current proposed =
        match current, proposed with
        | InstalledPackageState.Direct _, ProposedPackageState.Direct _
        | InstalledPackageState.UnresolvedDirect _, ProposedPackageState.Direct _ -> true
        | InstalledPackageState.CentrallyManagedDirect(_, _, currentOwner),
          ProposedPackageState.CentrallyManaged(_, proposedOwner)
        | InstalledPackageState.UnresolvedCentrallyManagedDirect(_, currentOwner),
          ProposedPackageState.CentrallyManaged(_, proposedOwner) ->
            System.String.Equals(currentOwner, proposedOwner, comparison)
        | _ -> false

    let internal validChange comparison operation target =
        match operation, PackageTargetPreview.change target with
        | RequestedPackageOperation.InstallLatest _, PackageTargetChange.Install(current, _) ->
            current
            |> Option.forall (function
                | InstalledPackageState.Transitive _ -> true
                | _ -> false)
        | RequestedPackageOperation.InstallVersion(_, destination),
          PackageTargetChange.Install(current, proposed) ->
            current
            |> Option.forall (function
                | InstalledPackageState.Transitive _ -> true
                | _ -> false)
            && proposedVersion proposed = destination
        | RequestedPackageOperation.UpdateLatest _, PackageTargetChange.Update(current, proposed) ->
            proposalMatchesOwner comparison current proposed
        | RequestedPackageOperation.UpdateVersion(_, destination),
          PackageTargetChange.Update(current, proposed) ->
            proposalMatchesOwner comparison current proposed
            && proposedVersion proposed = destination
        | RequestedPackageOperation.Uninstall _, PackageTargetChange.Uninstall current ->
            match current with
            | InstalledPackageState.Direct _
            | InstalledPackageState.CentrallyManagedDirect _
            | InstalledPackageState.UnresolvedDirect _
            | InstalledPackageState.UnresolvedCentrallyManagedDirect _ -> true
            | _ -> false
        | RequestedPackageOperation.ConsolidateVersion(_, destination),
          PackageTargetChange.Consolidate(current, position, proposed) ->
            match position, current, proposed with
            | PackageConsolidationPosition.AlreadyOnDestination,
              Some(InstalledPackageState.Direct(_, version)),
              None
            | PackageConsolidationPosition.AlreadyOnDestination,
              Some(InstalledPackageState.CentrallyManagedDirect(_, version, _)),
              None -> version = destination
            | (PackageConsolidationPosition.BelowDestination | PackageConsolidationPosition.AboveDestination),
              Some state,
              Some proposal ->
                currentVersion state |> Option.exists ((<>) destination)
                && proposedVersion proposal = destination
                && proposalMatchesOwner comparison state proposal
            | PackageConsolidationPosition.Unusable, None, None
            | PackageConsolidationPosition.Unusable,
              Some(InstalledPackageState.UnresolvedDirect _),
              None
            | PackageConsolidationPosition.Unusable,
              Some(InstalledPackageState.UnresolvedCentrallyManagedDirect _),
              None -> true
            | _ -> false
        | _ -> false

    let internal create
        pathComparison
        operation
        targets
        ownerFiles
        workspaceRevision
        fileFingerprints
        =
        let ownerPaths = ownerFiles |> NonEmptyList.toList
        let targetValues = targets |> NonEmptyList.toList

        let validFingerprints =
            ownerPaths
            |> List.forall (fun path ->
                not (System.String.IsNullOrWhiteSpace path)
                && fileFingerprints
                   |> Map.tryFind path
                   |> Option.exists (System.String.IsNullOrWhiteSpace >> not))

        let exactOwners =
            targetValues
            |> List.collect (PackageTargetPreview.ownerFiles >> NonEmptyList.toList)
            |> Set.ofList
            |> (=) (Set.ofList ownerPaths)

        let exactFingerprints =
            fileFingerprints |> Map.keys |> Set.ofSeq |> (=) (Set.ofList ownerPaths)

        let uniqueTargets =
            targetValues
            |> List.map PackageTargetPreview.target
            |> List.distinct
            |> List.length
            |> (=) targetValues.Length

        if System.String.IsNullOrWhiteSpace workspaceRevision then
            Error(PackageContractViolation.MissingValue "workspaceRevision")
        elif not (targetValues |> List.forall (operationMatches operation)) then
            Error(PackageContractViolation.InvalidValue "targetChanges")
        elif not (targetValues |> List.forall (validChange pathComparison operation)) then
            Error(PackageContractViolation.InvalidValue "targetChanges")
        elif not uniqueTargets then
            Error(PackageContractViolation.InvalidValue "targets")
        elif not exactOwners then
            Error(PackageContractViolation.InvalidValue "ownerFiles")
        elif not exactFingerprints then
            Error(PackageContractViolation.InvalidValue "fileFingerprints")
        elif not validFingerprints then
            Error(PackageContractViolation.MissingValue "fileFingerprints")
        else
            Ok
                { Operation = operation
                  Targets = targets
                  OwnerFiles = ownerFiles
                  WorkspaceRevision = workspaceRevision
                  FileFingerprints = fileFingerprints
                  ConfirmationToken =
                    createConfirmationToken
                        operation
                        targets
                        ownerFiles
                        workspaceRevision
                        fileFingerprints }

    let operation preview = preview.Operation
    let targets preview = preview.Targets
    let ownerFiles preview = preview.OwnerFiles
    let workspaceRevision preview = preview.WorkspaceRevision
    let fileFingerprints preview = preview.FileFingerprints
    let confirmationToken preview = preview.ConfirmationToken

type PackageUpdateTargetPreview =
    private
        { Package: PackageId
          RequestedVersion: NuGetVersion option
          Target: PackageTargetPreview }

[<RequireQualifiedAccess>]
module PackageUpdateTargetPreview =
    let internal create package requestedVersion target =
        { Package = package
          RequestedVersion = requestedVersion
          Target = target }

    let package preview = preview.Package
    let requestedVersion preview = preview.RequestedVersion
    let target preview = preview.Target

    let selectedVersion preview =
        match PackageTargetPreview.change preview.Target with
        | PackageTargetChange.Update(_, ProposedPackageState.Direct version)
        | PackageTargetChange.Update(_, ProposedPackageState.CentrallyManaged(version, _)) ->
            version
        | _ -> invalidOp "A package update preview must contain an update change."

type PackageUpdateBatchPreview =
    private
        { Updates: NonEmptyList<PackageUpdateTargetPreview>
          OwnerFiles: NonEmptyList<string>
          WorkspaceRevision: string
          FileFingerprints: Map<string, string>
          ConfirmationToken: string }

[<RequireQualifiedAccess>]
module PackageUpdateBatchPreview =
    let private createConfirmationToken
        (updates: NonEmptyList<PackageUpdateTargetPreview>)
        (ownerFiles: NonEmptyList<string>)
        (workspaceRevision: string)
        (fileFingerprints: Map<string, string>)
        =
        [ $"updates:{updates:A}"
          yield!
              ownerFiles
              |> NonEmptyList.toList
              |> List.map (fun value -> $"owner:{value.Length}:{value}")
          $"revision:{workspaceRevision.Length}:{workspaceRevision}"
          yield!
              fileFingerprints
              |> Map.toList
              |> List.map (fun (path, fingerprint) ->
                  $"fingerprint:{path.Length}:{path}:{fingerprint.Length}:{fingerprint}") ]
        |> String.concat "\u0000"
        |> System.Text.Encoding.UTF8.GetBytes
        |> System.Security.Cryptography.SHA256.HashData
        |> System.Convert.ToHexString

    let internal create pathComparison updates ownerFiles workspaceRevision fileFingerprints =
        let pathKey path =
            let full = System.IO.Path.GetFullPath path

            if pathComparison = System.StringComparison.OrdinalIgnoreCase then
                full.ToUpperInvariant()
            else
                full

        let targetKey update =
            let target =
                update |> PackageUpdateTargetPreview.target |> PackageTargetPreview.target

            let project, framework, runtime =
                match target with
                | PackageTargetScope.Project project -> project.Value, "", ""
                | PackageTargetScope.Framework(project, framework) ->
                    project.Value, framework.Value, ""
                | PackageTargetScope.Runtime(project, framework, runtime) ->
                    project.Value, framework.Value, runtime.Value

            (PackageUpdateTargetPreview.package update).Value.ToUpperInvariant(),
            pathKey project,
            framework,
            runtime

        let updateValues = updates |> NonEmptyList.toList |> List.sortBy targetKey

        let duplicateUpdates =
            updateValues |> List.countBy targetKey |> List.exists (snd >> (<) 1)

        let ownerPaths = ownerFiles |> NonEmptyList.toList

        let validUpdates =
            updateValues
            |> List.forall (fun update ->
                let operation =
                    match PackageUpdateTargetPreview.requestedVersion update with
                    | Some version ->
                        RequestedPackageOperation.UpdateVersion(
                            PackageUpdateTargetPreview.package update,
                            version
                        )
                    | None ->
                        RequestedPackageOperation.UpdateLatest(
                            PackageUpdateTargetPreview.package update
                        )

                PackagePreview.validChange
                    pathComparison
                    operation
                    (PackageUpdateTargetPreview.target update))

        let exactOwners =
            updateValues
            |> List.collect (
                PackageUpdateTargetPreview.target
                >> PackageTargetPreview.ownerFiles
                >> NonEmptyList.toList
            )
            |> Set.ofList
            |> (=) (Set.ofList ownerPaths)

        let exactFingerprints =
            fileFingerprints |> Map.keys |> Set.ofSeq |> (=) (Set.ofList ownerPaths)

        let validFingerprints =
            ownerPaths
            |> List.forall (fun path ->
                not (System.String.IsNullOrWhiteSpace path)
                && fileFingerprints
                   |> Map.tryFind path
                   |> Option.exists (System.String.IsNullOrWhiteSpace >> not))

        if System.String.IsNullOrWhiteSpace workspaceRevision then
            Error(PackageContractViolation.MissingValue "workspaceRevision")
        elif duplicateUpdates then
            Error(PackageContractViolation.InvalidValue "updates")
        elif not validUpdates then
            Error(PackageContractViolation.InvalidValue "updates")
        elif not exactOwners then
            Error(PackageContractViolation.InvalidValue "ownerFiles")
        elif not exactFingerprints then
            Error(PackageContractViolation.InvalidValue "fileFingerprints")
        elif not validFingerprints then
            Error(PackageContractViolation.MissingValue "fileFingerprints")
        else
            let orderedUpdates =
                updateValues
                |> NonEmptyList.tryCreate
                |> Option.defaultWith (fun () ->
                    invalidOp "A package update preview requires at least one update.")

            Ok
                { Updates = orderedUpdates
                  OwnerFiles = ownerFiles
                  WorkspaceRevision = workspaceRevision
                  FileFingerprints = fileFingerprints
                  ConfirmationToken =
                    createConfirmationToken
                        orderedUpdates
                        ownerFiles
                        workspaceRevision
                        fileFingerprints }

    let updates preview = preview.Updates
    let ownerFiles preview = preview.OwnerFiles
    let workspaceRevision preview = preview.WorkspaceRevision
    let fileFingerprints preview = preview.FileFingerprints
    let confirmationToken preview = preview.ConfirmationToken

type PackageConfirmation =
    private
        { Preview: PackagePreview
          ConfirmationToken: string }

[<RequireQualifiedAccess>]
module PackageConfirmation =
    let create preview confirmationToken =
        if System.String.IsNullOrWhiteSpace confirmationToken then
            Error(PackageContractViolation.MissingValue "confirmationToken")
        elif
            not (
                System.String.Equals(
                    confirmationToken,
                    PackagePreview.confirmationToken preview,
                    System.StringComparison.Ordinal
                )
            )
        then
            Error(PackageContractViolation.InvalidValue "confirmationToken")
        else
            Ok
                { Preview = preview
                  ConfirmationToken = confirmationToken }

    let preview confirmation = confirmation.Preview
    let token confirmation = confirmation.ConfirmationToken

type PackageUpdateBatchConfirmation =
    private
        { Preview: PackageUpdateBatchPreview
          ConfirmationToken: string }

[<RequireQualifiedAccess>]
module PackageUpdateBatchConfirmation =
    let create preview confirmationToken =
        if System.String.IsNullOrWhiteSpace confirmationToken then
            Error(PackageContractViolation.MissingValue "confirmationToken")
        elif
            not (
                System.String.Equals(
                    confirmationToken,
                    PackageUpdateBatchPreview.confirmationToken preview,
                    System.StringComparison.Ordinal
                )
            )
        then
            Error(PackageContractViolation.InvalidValue "confirmationToken")
        else
            Ok
                { Preview = preview
                  ConfirmationToken = confirmationToken }

    let preview confirmation = confirmation.Preview
    let token confirmation = confirmation.ConfirmationToken

[<RequireQualifiedAccess>]
type PackageExecutionState =
    | Completed
    | Compensated
    | Unchanged
    | Uncertain

type PackageExecutionEntry =
    { Package: PackageId
      Target: PackageTargetScope
      State: PackageExecutionState }

[<RequireQualifiedAccess>]
type PackageOperationStage =
    | Preparing
    | Applying
    | Restoring
    | Refreshing
    | Completed

type PackageProgress =
    private
        { Operation: PackageOperationId
          Stage: PackageOperationStage
          Completed: (int * int) option }

[<RequireQualifiedAccess>]
module PackageProgress =
    let indeterminate operation stage =
        { Operation = operation
          Stage = stage
          Completed = None }

    let determinate operation stage completed total =
        if total <= 0 || completed < 0 || completed > total then
            Error(PackageContractViolation.OutOfRange "progress")
        else
            Ok
                { Operation = operation
                  Stage = stage
                  Completed = Some(completed, total) }

    let operation progress = progress.Operation
    let stage progress = progress.Stage
    let completed progress = progress.Completed

[<RequireQualifiedAccess>]
type PackageCancellation =
    | Request of PackageRequestId
    | Operation of PackageOperationId

[<RequireQualifiedAccess>]
type PackageRestoreOutcome =
    | NotRequired
    | Completed

type PackageExecution =
    { Operation: PackageOperationId
      Entries: PackageExecutionEntry list
      ChangedFiles: string list
      Restore: PackageRestoreOutcome }
