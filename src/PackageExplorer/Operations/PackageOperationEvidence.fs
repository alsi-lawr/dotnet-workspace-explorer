namespace Dotnet.WorkspaceExplorer.PackageExplorer

open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation

type internal PackageOperationPreviewEvidence =
    { WorkspaceRoot: string
      Evaluations: ProjectEvaluationSnapshot list
      Installed: InstalledPackageGraph list
      Details: Map<NuGetVersion, PackageDetails>
      SourceMappings: Map<PackageProjectId, PackageSourceMappingPolicy>
      CaseSensitivity: Dotnet.WorkspaceExplorer.Workspaces.FileSystemCaseSensitivity
      WorkspaceRevision: string
      FileFingerprints: Map<string, string> }

type internal ReadPackageOperationPreviewEvidence =
    PackageRequest<PackageOperationRequest>
        -> Async<Result<PackageOperationPreviewEvidence, PackageFailure>>
