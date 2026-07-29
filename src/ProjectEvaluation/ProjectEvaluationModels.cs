using System.Collections.Immutable;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

public readonly record struct EvaluatedTargetFramework
{
    public EvaluatedTargetFramework(string value)
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

public sealed record EvaluatedPackageMembership(
    string Id,
    string? Version,
    WorkspaceArtifactPath DeclaringPath,
    string Condition
);

public sealed record EvaluatedPackageVersion(
    string Id,
    string? Version,
    WorkspaceArtifactPath DeclaringPath,
    string Condition
);

public sealed record ProjectEvaluationDimension(
    EvaluatedTargetFramework? TargetFramework,
    ImmutableArray<EvaluatedProperty> Properties,
    ImmutableArray<EvaluatedItem> Items,
    ImmutableArray<EvaluatedReference> ProjectReferences,
    ImmutableArray<EvaluatedReference> References,
    ImmutableArray<EvaluatedPackage> Packages,
    ImmutableArray<WorkspaceArtifactPath> Analyzers
)
{
    public bool IsOuterBuild => TargetFramework is null;

    public ImmutableArray<EvaluatedPackageMembership> PackageMemberships { get; init; } = [];

    public ImmutableArray<EvaluatedPackageVersion> PackageVersions { get; init; } = [];
}
