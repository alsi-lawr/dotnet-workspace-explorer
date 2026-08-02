using System.Collections.Immutable;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal sealed record DotnetSdkSelection(
    WorkspaceArtifactPath SdkPath,
    WorkspaceArtifactPath? GlobalJsonPath
);

internal sealed record ProjectEvaluationBinding(
    WorkspaceArtifactPath WorkspacePath,
    DotnetSdkSelection SdkSelection
);

internal sealed record ProjectInvalidationResult(
    ImmutableArray<WorkspaceArtifactPath> InvalidatedProjects
);

internal enum ProjectEvaluationReadiness
{
    Ready,
}
