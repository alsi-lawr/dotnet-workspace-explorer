using System.Collections.Immutable;
using Dotnet.WorkspaceExplorer.Rpc;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal static class ProjectEvaluationMessagePack
{
    private static readonly string[] SnapshotFields =
    [
        "projectPath",
        "dimensions",
        "imports",
        "watchInputs",
        "globRoots",
        "capabilityProfile",
        "capabilities",
        "diagnostics",
    ];

    internal static RpcValue Encode(ProjectEvaluationSnapshot snapshot) =>
        ProjectEvaluationRpc.Map(
            ("projectPath", RpcValue.NewString(snapshot.ProjectPath.Value)),
            ("dimensions", ProjectEvaluationRpc.Array(snapshot.Dimensions, EncodeDimension)),
            ("imports", Paths(snapshot.Imports)),
            ("watchInputs", Paths(snapshot.WatchInputs)),
            ("globRoots", Paths(snapshot.GlobRoots)),
            ("capabilityProfile", RpcValue.NewString(snapshot.CapabilityProfile.ToString())),
            (
                "capabilities",
                ProjectEvaluationRpc.Array(
                    snapshot.Capabilities,
                    capability => RpcValue.NewString(capability.Value)
                )
            ),
            ("diagnostics", ProjectEvaluationRpc.Array(snapshot.Diagnostics, EncodeDiagnostic))
        );

    internal static ProjectEvaluationSnapshot Decode(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap("snapshot", value, SnapshotFields);
        return new ProjectEvaluationSnapshot(
            WorkspaceArtifactPath.Create(ProjectEvaluationRpc.String(fields, "projectPath")),
            Values(fields, "dimensions", DecodeDimension),
            Paths(fields, "imports"),
            Paths(fields, "watchInputs"),
            Paths(fields, "globRoots"),
            ParseProfile(ProjectEvaluationRpc.String(fields, "capabilityProfile")),
            Values(fields, "capabilities", DecodeCapability),
            Values(fields, "diagnostics", DecodeDiagnostic)
        );
    }

    private static RpcValue EncodeDimension(ProjectEvaluationDimension dimension) =>
        ProjectEvaluationRpc.Map(
            (
                "targetFramework",
                dimension.TargetFramework is { } framework
                    ? RpcValue.NewString(framework.Value)
                    : RpcValue.Nil
            ),
            (
                "properties",
                ProjectEvaluationRpc.Array(
                    dimension.Properties,
                    property =>
                        ProjectEvaluationRpc.Map(
                            ("name", RpcValue.NewString(property.Name)),
                            ("value", RpcValue.NewString(property.Value))
                        )
                )
            ),
            ("items", ProjectEvaluationRpc.Array(dimension.Items, EncodeItem)),
            (
                "projectReferences",
                ProjectEvaluationRpc.Array(dimension.ProjectReferences, EncodeReference)
            ),
            ("references", ProjectEvaluationRpc.Array(dimension.References, EncodeReference)),
            (
                "packages",
                ProjectEvaluationRpc.Array(
                    dimension.Packages,
                    package =>
                        ProjectEvaluationRpc.Map(
                            ("id", RpcValue.NewString(package.Id)),
                            ("version", OptionalString(package.Version))
                        )
                )
            ),
            (
                "packageMemberships",
                ProjectEvaluationRpc.Array(dimension.PackageMemberships, EncodePackageMembership)
            ),
            (
                "packageVersions",
                ProjectEvaluationRpc.Array(dimension.PackageVersions, EncodePackageVersion)
            ),
            ("analyzers", Paths(dimension.Analyzers))
        );

    private static ProjectEvaluationDimension DecodeDimension(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap(
            "dimension",
            value,
            [
                "targetFramework",
                "properties",
                "items",
                "projectReferences",
                "references",
                "packages",
                "packageMemberships",
                "packageVersions",
                "analyzers",
            ]
        );
        var targetFramework = OptionalString(fields, "targetFramework");
        return new ProjectEvaluationDimension(
            targetFramework is null ? null : new EvaluatedTargetFramework(targetFramework),
            Values(fields, "properties", DecodeProperty),
            Values(fields, "items", DecodeItem),
            Values(fields, "projectReferences", DecodeReference),
            Values(fields, "references", DecodeReference),
            Values(fields, "packages", DecodePackage),
            Paths(fields, "analyzers")
        )
        {
            PackageMemberships = Values(fields, "packageMemberships", DecodePackageMembership),
            PackageVersions = Values(fields, "packageVersions", DecodePackageVersion),
        };
    }

    private static RpcValue EncodeItem(EvaluatedItem item) =>
        ProjectEvaluationRpc.Map(
            ("itemType", RpcValue.NewString(item.ItemType)),
            ("include", RpcValue.NewString(item.EvaluatedInclude)),
            ("resolvedPath", OptionalPath(item.ResolvedPath)),
            (
                "metadata",
                ProjectEvaluationRpc.Array(
                    item.Metadata,
                    metadata =>
                        ProjectEvaluationRpc.Map(
                            ("name", RpcValue.NewString(metadata.Name)),
                            ("value", RpcValue.NewString(metadata.Value))
                        )
                )
            )
        );

    private static EvaluatedProperty DecodeProperty(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap("property", value, ["name", "value"]);
        return new EvaluatedProperty(
            ProjectEvaluationRpc.String(fields, "name"),
            ProjectEvaluationRpc.String(fields, "value")
        );
    }

    private static EvaluatedItem DecodeItem(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap(
            "item",
            value,
            ["itemType", "include", "resolvedPath", "metadata"]
        );
        return new EvaluatedItem(
            ProjectEvaluationRpc.String(fields, "itemType"),
            ProjectEvaluationRpc.String(fields, "include"),
            OptionalPath(fields, "resolvedPath"),
            Values(fields, "metadata", DecodeMetadata)
        );
    }

    private static EvaluatedMetadata DecodeMetadata(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap("metadata", value, ["name", "value"]);
        return new EvaluatedMetadata(
            ProjectEvaluationRpc.String(fields, "name"),
            ProjectEvaluationRpc.String(fields, "value")
        );
    }

    private static RpcValue EncodeReference(EvaluatedReference reference) =>
        ProjectEvaluationRpc.Map(
            ("include", RpcValue.NewString(reference.Include)),
            ("resolvedPath", OptionalPath(reference.ResolvedPath))
        );

    private static EvaluatedReference DecodeReference(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap("reference", value, ["include", "resolvedPath"]);
        return new EvaluatedReference(
            ProjectEvaluationRpc.String(fields, "include"),
            OptionalPath(fields, "resolvedPath")
        );
    }

    private static EvaluatedPackage DecodePackage(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap("package", value, ["id", "version"]);
        return new EvaluatedPackage(
            ProjectEvaluationRpc.String(fields, "id"),
            OptionalString(fields, "version")
        );
    }

    private static RpcValue EncodePackageMembership(EvaluatedPackageMembership value) =>
        ProjectEvaluationRpc.Map(
            ("id", RpcValue.NewString(value.Id)),
            ("version", OptionalString(value.Version)),
            ("declaringPath", RpcValue.NewString(value.DeclaringPath.Value)),
            ("condition", RpcValue.NewString(value.Condition))
        );

    private static EvaluatedPackageMembership DecodePackageMembership(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap(
            "packageMembership",
            value,
            ["id", "version", "declaringPath", "condition"]
        );
        return new EvaluatedPackageMembership(
            ProjectEvaluationRpc.String(fields, "id"),
            OptionalString(fields, "version"),
            WorkspaceArtifactPath.Create(ProjectEvaluationRpc.String(fields, "declaringPath")),
            ProjectEvaluationRpc.String(fields, "condition")
        );
    }

    private static RpcValue EncodePackageVersion(EvaluatedPackageVersion value) =>
        ProjectEvaluationRpc.Map(
            ("id", RpcValue.NewString(value.Id)),
            ("version", OptionalString(value.Version)),
            ("declaringPath", RpcValue.NewString(value.DeclaringPath.Value)),
            ("condition", RpcValue.NewString(value.Condition))
        );

    private static EvaluatedPackageVersion DecodePackageVersion(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap(
            "packageVersion",
            value,
            ["id", "version", "declaringPath", "condition"]
        );
        return new EvaluatedPackageVersion(
            ProjectEvaluationRpc.String(fields, "id"),
            OptionalString(fields, "version"),
            WorkspaceArtifactPath.Create(ProjectEvaluationRpc.String(fields, "declaringPath")),
            ProjectEvaluationRpc.String(fields, "condition")
        );
    }

    private static RpcValue EncodeDiagnostic(WorkspaceDiagnostic diagnostic) =>
        ProjectEvaluationRpc.Map(
            ("severity", RpcValue.NewString(diagnostic.Severity.ToString())),
            ("code", RpcValue.NewString(diagnostic.Code.Value)),
            ("message", RpcValue.NewString(diagnostic.Message)),
            (
                "path",
                diagnostic.ArtifactPath is { } path
                    ? RpcValue.NewString(path.Value.Value)
                    : RpcValue.Nil
            ),
            ("retryable", RpcValue.NewBoolean(diagnostic.Retryable))
        );

    private static WorkspaceDiagnostic DecodeDiagnostic(RpcValue value)
    {
        var fields = ProjectEvaluationRpc.ExactMap(
            "diagnostic",
            value,
            ["severity", "code", "message", "path", "retryable"]
        );
        return ProjectEvaluationOutcomes.Diagnostic(
            ProjectEvaluationRpc.String(fields, "code"),
            ProjectEvaluationRpc.String(fields, "message"),
            OptionalPath(fields, "path"),
            RequiredBoolean(fields, "retryable"),
            Enum.Parse<WorkspaceDiagnosticSeverity>(
                ProjectEvaluationRpc.String(fields, "severity"),
                false
            )
        );
    }

    private static WorkspaceCapabilityProfile ParseProfile(string value) =>
        value switch
        {
            nameof(WorkspaceCapabilityProfile.Full) => WorkspaceCapabilityProfile.Full,
            nameof(WorkspaceCapabilityProfile.ReadOnly) => WorkspaceCapabilityProfile.ReadOnly,
            nameof(WorkspaceCapabilityProfile.UnknownProjectSystem) =>
                WorkspaceCapabilityProfile.UnknownProjectSystem,
            _ => throw new ArgumentException("The capability profile is invalid.", nameof(value)),
        };

    private static WorkspaceCapabilityId DecodeCapability(RpcValue value) =>
        RpcValueModule.requireString("capability", value) switch
        {
            "workspace.read" => WorkspaceCapabilityId.Read,
            "workspace.write" => WorkspaceCapabilityId.Write,
            _ => throw new ArgumentException(
                "The capability identifier is invalid.",
                nameof(value)
            ),
        };

    private static RpcValue Paths(ImmutableArray<WorkspaceArtifactPath> paths) =>
        ProjectEvaluationRpc.Array(paths, path => RpcValue.NewString(path.Value));

    private static ImmutableArray<WorkspaceArtifactPath> Paths(
        ImmutableDictionary<string, RpcValue> fields,
        string name
    ) =>
        Values(
            fields,
            name,
            value => WorkspaceArtifactPath.Create(RpcValueModule.requireString(name, value))
        );

    private static RpcValue OptionalPath(WorkspaceArtifactPath? path) =>
        path is null ? RpcValue.Nil : RpcValue.NewString(path.Value);

    private static WorkspaceArtifactPath? OptionalPath(
        ImmutableDictionary<string, RpcValue> fields,
        string name
    )
    {
        var value = ProjectEvaluationRpc.Field(fields, name);
        return value == RpcValue.Nil
            ? null
            : WorkspaceArtifactPath.Create(RpcValueModule.requireString(name, value));
    }

    private static RpcValue OptionalString(string? value) =>
        value is null ? RpcValue.Nil : RpcValue.NewString(value);

    private static string? OptionalString(ImmutableDictionary<string, RpcValue> fields, string name)
    {
        var value = ProjectEvaluationRpc.Field(fields, name);
        return value == RpcValue.Nil ? null : RpcValueModule.requireString(name, value);
    }

    private static ImmutableArray<T> Values<T>(
        ImmutableDictionary<string, RpcValue> fields,
        string name,
        Func<RpcValue, T> decode
    ) =>
        RpcValueModule
            .requireArray(name, ProjectEvaluationRpc.Field(fields, name))
            .Select(decode)
            .ToImmutableArray();

    private static bool RequiredBoolean(
        ImmutableDictionary<string, RpcValue> fields,
        string name
    ) =>
        ProjectEvaluationRpc.Field(fields, name) is RpcValue.Boolean value
            ? value.Item
            : throw new ArgumentException("Expected a boolean.", name);
}
