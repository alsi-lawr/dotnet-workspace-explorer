using System.Collections.Immutable;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

public sealed record ProjectEvaluationSnapshot(
    WorkspaceArtifactPath ProjectPath,
    ImmutableArray<ProjectEvaluationDimension> Dimensions,
    ImmutableArray<WorkspaceArtifactPath> Imports,
    ImmutableArray<WorkspaceArtifactPath> WatchInputs,
    ImmutableArray<WorkspaceArtifactPath> GlobRoots,
    WorkspaceCapabilityProfile CapabilityProfile,
    ImmutableArray<WorkspaceCapabilityId> Capabilities,
    ImmutableArray<WorkspaceDiagnostic> Diagnostics
);
