using System.Collections.Immutable;
using Dotnet.CLI.Plus.Core;
using Dotnet.CLI.Plus.Transport;

namespace Dotnet.CLI.Plus.MSBuild;

internal static class SnapshotCodec
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

    internal static RpcValue Encode(EvaluationSnapshot snapshot) =>
        WorkerProtocol.Map(
            ("projectPath", RpcValue.NewString(snapshot.ProjectPath.Value)),
            ("dimensions", WorkerProtocol.Array(snapshot.Dimensions, EncodeDimension)),
            ("imports", Paths(snapshot.Imports)),
            ("watchInputs", Paths(snapshot.WatchInputs)),
            ("globRoots", Paths(snapshot.GlobRoots)),
            ("capabilityProfile", RpcValue.NewString(snapshot.CapabilityProfile.ToString())),
            (
                "capabilities",
                WorkerProtocol.Array(
                    snapshot.Capabilities,
                    capability => RpcValue.NewString(capability.Value)
                )
            ),
            ("diagnostics", WorkerProtocol.Array(snapshot.Diagnostics, EncodeDiagnostic))
        );

    internal static EvaluationSnapshot Decode(RpcValue value)
    {
        var fields = WorkerProtocol.ExactMap("snapshot", value, SnapshotFields);
        return new EvaluationSnapshot(
            WorkspaceArtifactPath.Create(WorkerProtocol.String(fields, "projectPath")),
            Values(fields, "dimensions", DecodeDimension),
            Paths(fields, "imports"),
            Paths(fields, "watchInputs"),
            Paths(fields, "globRoots"),
            ParseProfile(WorkerProtocol.String(fields, "capabilityProfile")),
            Values(fields, "capabilities", DecodeCapability),
            Values(fields, "diagnostics", DecodeDiagnostic)
        );
    }

    private static RpcValue EncodeDimension(EvaluationDimensionSnapshot dimension) =>
        WorkerProtocol.Map(
            (
                "targetFramework",
                dimension.TargetFramework is { } framework
                    ? RpcValue.NewString(framework.Value)
                    : RpcValue.Nil
            ),
            (
                "properties",
                WorkerProtocol.Array(
                    dimension.Properties,
                    property =>
                        WorkerProtocol.Map(
                            ("name", RpcValue.NewString(property.Name)),
                            ("value", RpcValue.NewString(property.Value))
                        )
                )
            ),
            ("items", WorkerProtocol.Array(dimension.Items, EncodeItem)),
            (
                "projectReferences",
                WorkerProtocol.Array(dimension.ProjectReferences, EncodeReference)
            ),
            ("references", WorkerProtocol.Array(dimension.References, EncodeReference)),
            (
                "packages",
                WorkerProtocol.Array(
                    dimension.Packages,
                    package =>
                        WorkerProtocol.Map(
                            ("id", RpcValue.NewString(package.Id)),
                            ("version", OptionalString(package.Version))
                        )
                )
            ),
            ("analyzers", Paths(dimension.Analyzers))
        );

    private static EvaluationDimensionSnapshot DecodeDimension(RpcValue value)
    {
        var fields = WorkerProtocol.ExactMap(
            "dimension",
            value,
            [
                "targetFramework",
                "properties",
                "items",
                "projectReferences",
                "references",
                "packages",
                "analyzers",
            ]
        );
        var targetFramework = OptionalString(fields, "targetFramework");
        return new EvaluationDimensionSnapshot(
            targetFramework is null ? null : new TargetFramework(targetFramework),
            Values(fields, "properties", DecodeProperty),
            Values(fields, "items", DecodeItem),
            Values(fields, "projectReferences", DecodeReference),
            Values(fields, "references", DecodeReference),
            Values(fields, "packages", DecodePackage),
            Paths(fields, "analyzers")
        );
    }

    private static RpcValue EncodeItem(EvaluatedItem item) =>
        WorkerProtocol.Map(
            ("itemType", RpcValue.NewString(item.ItemType)),
            ("include", RpcValue.NewString(item.EvaluatedInclude)),
            ("resolvedPath", OptionalPath(item.ResolvedPath)),
            (
                "metadata",
                WorkerProtocol.Array(
                    item.Metadata,
                    metadata =>
                        WorkerProtocol.Map(
                            ("name", RpcValue.NewString(metadata.Name)),
                            ("value", RpcValue.NewString(metadata.Value))
                        )
                )
            )
        );

    private static EvaluatedProperty DecodeProperty(RpcValue value)
    {
        var fields = WorkerProtocol.ExactMap("property", value, ["name", "value"]);
        return new EvaluatedProperty(
            WorkerProtocol.String(fields, "name"),
            WorkerProtocol.String(fields, "value")
        );
    }

    private static EvaluatedItem DecodeItem(RpcValue value)
    {
        var fields = WorkerProtocol.ExactMap(
            "item",
            value,
            ["itemType", "include", "resolvedPath", "metadata"]
        );
        return new EvaluatedItem(
            WorkerProtocol.String(fields, "itemType"),
            WorkerProtocol.String(fields, "include"),
            OptionalPath(fields, "resolvedPath"),
            Values(fields, "metadata", DecodeMetadata)
        );
    }

    private static EvaluatedMetadata DecodeMetadata(RpcValue value)
    {
        var fields = WorkerProtocol.ExactMap("metadata", value, ["name", "value"]);
        return new EvaluatedMetadata(
            WorkerProtocol.String(fields, "name"),
            WorkerProtocol.String(fields, "value")
        );
    }

    private static RpcValue EncodeReference(EvaluatedReference reference) =>
        WorkerProtocol.Map(
            ("include", RpcValue.NewString(reference.Include)),
            ("resolvedPath", OptionalPath(reference.ResolvedPath))
        );

    private static EvaluatedReference DecodeReference(RpcValue value)
    {
        var fields = WorkerProtocol.ExactMap("reference", value, ["include", "resolvedPath"]);
        return new EvaluatedReference(
            WorkerProtocol.String(fields, "include"),
            OptionalPath(fields, "resolvedPath")
        );
    }

    private static EvaluatedPackage DecodePackage(RpcValue value)
    {
        var fields = WorkerProtocol.ExactMap("package", value, ["id", "version"]);
        return new EvaluatedPackage(
            WorkerProtocol.String(fields, "id"),
            OptionalString(fields, "version")
        );
    }

    private static RpcValue EncodeDiagnostic(WorkspaceDiagnostic diagnostic) =>
        WorkerProtocol.Map(
            ("severity", RpcValue.NewString(diagnostic.DiagnosticSeverity.ToString())),
            ("code", RpcValue.NewString(diagnostic.DiagnosticCode.Value)),
            ("message", RpcValue.NewString(diagnostic.Message)),
            (
                "path",
                diagnostic.DiagnosticArtifactPath is { } path
                    ? RpcValue.NewString(path.Value.Value)
                    : RpcValue.Nil
            ),
            ("retryable", RpcValue.NewBoolean(diagnostic.Retryable))
        );

    private static WorkspaceDiagnostic DecodeDiagnostic(RpcValue value)
    {
        var fields = WorkerProtocol.ExactMap(
            "diagnostic",
            value,
            ["severity", "code", "message", "path", "retryable"]
        );
        return CoreOutcomes.Diagnostic(
            WorkerProtocol.String(fields, "code"),
            WorkerProtocol.String(fields, "message"),
            OptionalPath(fields, "path"),
            RequiredBoolean(fields, "retryable"),
            Enum.Parse<WorkspaceDiagnosticSeverity>(
                WorkerProtocol.String(fields, "severity"),
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
        WorkerProtocol.Array(paths, path => RpcValue.NewString(path.Value));

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
        var value = WorkerProtocol.Field(fields, name);
        return value == RpcValue.Nil
            ? null
            : WorkspaceArtifactPath.Create(RpcValueModule.requireString(name, value));
    }

    private static RpcValue OptionalString(string? value) =>
        value is null ? RpcValue.Nil : RpcValue.NewString(value);

    private static string? OptionalString(ImmutableDictionary<string, RpcValue> fields, string name)
    {
        var value = WorkerProtocol.Field(fields, name);
        return value == RpcValue.Nil ? null : RpcValueModule.requireString(name, value);
    }

    private static ImmutableArray<T> Values<T>(
        ImmutableDictionary<string, RpcValue> fields,
        string name,
        Func<RpcValue, T> decode
    ) =>
        RpcValueModule
            .requireArray(name, WorkerProtocol.Field(fields, name))
            .Select(decode)
            .ToImmutableArray();

    private static bool RequiredBoolean(
        ImmutableDictionary<string, RpcValue> fields,
        string name
    ) =>
        WorkerProtocol.Field(fields, name) is RpcValue.Boolean value
            ? value.Item
            : throw new ArgumentException("Expected a boolean.", name);
}
