using System.Collections.Immutable;

namespace Dotnet.CLI.Plus.MSBuild;

public enum MsBuildCapabilityProfile
{
    Full,
    ReadOnly,
    UnknownProjectSystem,
}

public enum MsBuildInvalidationKind
{
    None,
    ProjectOrImport,
    ToolsetSelection,
}

public sealed record ToolsetSelection(string SdkVersion, string ToolsetPath, string? GlobalJsonPath)
{
    public string Key => ToolsetPath;
}

public sealed record EvaluationDimension(string TargetFramework)
{
    public static readonly EvaluationDimension Outer = new(string.Empty);
}

public sealed record EvaluatedProperty(string Name, string Value);

public sealed record EvaluatedMetadata(string Name, string Value);

public sealed record EvaluatedItem(
    string ItemType,
    string EvaluatedInclude,
    string? ResolvedPath,
    ImmutableArray<EvaluatedMetadata> Metadata,
    EvaluationDimension Dimension
);

public sealed record EvaluatedReference(string Include, string? ResolvedPath);

public sealed record EvaluatedPackage(string Id, string? Version);

public sealed record MsBuildDiagnostic(string Code, string Message, bool IsTransient);

public sealed record EvaluationSnapshot(
    string ProjectPath,
    ImmutableArray<EvaluatedProperty> Properties,
    ImmutableArray<EvaluatedItem> Items,
    ImmutableArray<EvaluatedReference> ProjectReferences,
    ImmutableArray<EvaluatedReference> References,
    ImmutableArray<EvaluatedPackage> Packages,
    ImmutableArray<string> TargetFrameworks,
    ImmutableArray<string> Analyzers,
    ImmutableArray<string> Imports,
    ImmutableArray<string> WatchInputs,
    ImmutableArray<string> GlobRoots,
    MsBuildCapabilityProfile CapabilityProfile,
    ImmutableArray<string> Capabilities,
    ImmutableArray<MsBuildDiagnostic> Diagnostics
);

public sealed record InvalidationResult(ImmutableArray<string> InvalidatedProjects);

public abstract record EvaluationOutcome
{
    private EvaluationOutcome() { }

    public sealed record Success(EvaluationSnapshot Snapshot) : EvaluationOutcome;

    public sealed record Failure(string Code, string Message, bool IsCancelled = false)
        : EvaluationOutcome;
}
