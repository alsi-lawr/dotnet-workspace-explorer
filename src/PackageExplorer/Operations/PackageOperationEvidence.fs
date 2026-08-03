namespace Dotnet.WorkspaceExplorer.PackageExplorer

open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation

type internal PackageOperationPreviewEvidence =
    { WorkspaceRoot: string
      Evaluations: ProjectEvaluationSnapshot list
      Installed: InstalledPackageGraph list
      Details: Map<NuGetVersion, PackageDetails>
      SourceMapping: PackageSourceMappingPolicy
      WorkspaceRevision: string
      FileFingerprints: Map<string, string> }

type internal ReadPackageOperationPreviewEvidence =
    PackageRequest<PackageOperationRequest>
        -> Async<Result<PackageOperationPreviewEvidence, PackageFailure>>
