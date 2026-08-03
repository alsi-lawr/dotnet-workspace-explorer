namespace Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
type RequestedPackageOperation =
    | InstallLatest of package: PackageId
    | InstallVersion of package: PackageId * version: NuGetVersion
    | UpdateLatest of package: PackageId
    | UpdateVersion of package: PackageId * version: NuGetVersion
    | Uninstall of package: PackageId

type PackageOperationRequest =
    { Operation: RequestedPackageOperation
      Targets: NonEmptyList<PackageTargetScope> }

[<RequireQualifiedAccess>]
type PackageStateChange =
    | Add of target: PackageTargetScope * proposed: NuGetVersion
    | Change of target: PackageTargetScope * current: NuGetVersion * proposed: NuGetVersion
    | Remove of target: PackageTargetScope * current: NuGetVersion

[<RequireQualifiedAccess>]
type PackageImpact =
    | DependencyChange of string
    | DeprecationWarning of string
    | VulnerabilityWarning of PackageVulnerability
    | LicenseNotice of string
    | RestoreRequired
    | UnsupportedPolicy of string

type PackagePreview =
    private
        { Operation: RequestedPackageOperation
          Changes: NonEmptyList<PackageStateChange>
          OwnerFiles: NonEmptyList<string>
          Impacts: PackageImpact list
          WorkspaceRevision: string
          FileFingerprints: Map<string, string> }

[<RequireQualifiedAccess>]
module PackagePreview =
    let create operation changes ownerFiles impacts workspaceRevision fileFingerprints =
        let ownerPaths = ownerFiles |> NonEmptyList.toList

        let validFingerprints =
            ownerPaths
            |> List.forall (fun path ->
                not (System.String.IsNullOrWhiteSpace path)
                && fileFingerprints
                   |> Map.tryFind path
                   |> Option.exists (System.String.IsNullOrWhiteSpace >> not))

        if System.String.IsNullOrWhiteSpace workspaceRevision then
            Error(PackageContractViolation.MissingValue "workspaceRevision")
        elif not validFingerprints then
            Error(PackageContractViolation.MissingValue "fileFingerprints")
        else
            Ok
                { Operation = operation
                  Changes = changes
                  OwnerFiles = ownerFiles
                  Impacts = impacts
                  WorkspaceRevision = workspaceRevision
                  FileFingerprints = fileFingerprints }

    let operation preview = preview.Operation
    let changes preview = preview.Changes
    let ownerFiles preview = preview.OwnerFiles
    let impacts preview = preview.Impacts
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
