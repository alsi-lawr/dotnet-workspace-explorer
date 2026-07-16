using System.Collections.Immutable;
using Dotnet.CLI.Plus.Core;

namespace Dotnet.CLI.Plus.MSBuild;

public enum MsBuildInvalidationKind
{
    None,
    ProjectOrImport,
    ToolsetSelection,
}

public readonly record struct TargetFramework
{
    public TargetFramework(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record EvaluatedProperty(string Name, string Value);

public sealed record EvaluatedMetadata(string Name, string Value);

public sealed record EvaluatedItem(
    string ItemType,
    string EvaluatedInclude,
    WorkspaceArtifactPath? ResolvedPath,
    ImmutableArray<EvaluatedMetadata> Metadata
);

public sealed record EvaluatedReference(string Include, WorkspaceArtifactPath? ResolvedPath);

public sealed record EvaluatedPackage(string Id, string? Version);

public sealed record EvaluationDimensionSnapshot(
    TargetFramework? TargetFramework,
    ImmutableArray<EvaluatedProperty> Properties,
    ImmutableArray<EvaluatedItem> Items,
    ImmutableArray<EvaluatedReference> ProjectReferences,
    ImmutableArray<EvaluatedReference> References,
    ImmutableArray<EvaluatedPackage> Packages,
    ImmutableArray<WorkspaceArtifactPath> Analyzers
)
{
    public bool IsOuterBuild => TargetFramework is null;
}

public sealed record EvaluationSnapshot(
    WorkspaceArtifactPath ProjectPath,
    ImmutableArray<EvaluationDimensionSnapshot> Dimensions,
    ImmutableArray<WorkspaceArtifactPath> Imports,
    ImmutableArray<WorkspaceArtifactPath> WatchInputs,
    ImmutableArray<WorkspaceArtifactPath> GlobRoots,
    WorkspaceCapabilityProfile CapabilityProfile,
    ImmutableArray<WorkspaceCapabilityId> Capabilities,
    ImmutableArray<WorkspaceDiagnostic> Diagnostics
);

internal sealed record ToolsetSelection(
    string SdkVersion,
    WorkspaceArtifactPath ToolsetPath,
    WorkspaceArtifactPath? GlobalJsonPath
);

internal sealed record WorkspaceBinding(
    WorkspaceArtifactPath WorkspacePath,
    ToolsetSelection Toolset
);

internal sealed record InvalidationResult(
    ImmutableArray<WorkspaceArtifactPath> InvalidatedProjects
);
