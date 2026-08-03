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

type PackageOperationRequest =
    { Operation: RequestedPackageOperation
      Targets: NonEmptyList<PackageTargetScope>
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
    let create target change ownerFiles graphFreshness impact =
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
          FileFingerprints: Map<string, string> }

[<RequireQualifiedAccess>]
module PackagePreview =
    let private operationMatches operation target =
        match operation, PackageTargetPreview.change target with
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          PackageTargetChange.Install _
        | (RequestedPackageOperation.UpdateLatest _ | RequestedPackageOperation.UpdateVersion _),
          PackageTargetChange.Update _
        | RequestedPackageOperation.Uninstall _, PackageTargetChange.Uninstall _
        | RequestedPackageOperation.ConsolidateVersion _, PackageTargetChange.Consolidate _ -> true
        | _ -> false

    let create operation targets ownerFiles workspaceRevision fileFingerprints =
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
                  FileFingerprints = fileFingerprints }

    let operation preview = preview.Operation
    let targets preview = preview.Targets
    let ownerFiles preview = preview.OwnerFiles
    let workspaceRevision preview = preview.WorkspaceRevision
    let fileFingerprints preview = preview.FileFingerprints

type PackageConfirmation =
    private
        { Preview: PackagePreview
          ConfirmationToken: string }

[<RequireQualifiedAccess>]
module PackageConfirmation =
    let create preview confirmationToken =
        if System.String.IsNullOrWhiteSpace confirmationToken then
            Error(PackageContractViolation.MissingValue "confirmationToken")
        else
            Ok
                { Preview = preview
                  ConfirmationToken = confirmationToken }

    let preview confirmation = confirmation.Preview
    let token confirmation = confirmation.ConfirmationToken

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
