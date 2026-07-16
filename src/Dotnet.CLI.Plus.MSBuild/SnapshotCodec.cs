using System.Collections.Immutable;
using Dotnet.CLI.Plus.Transport;

namespace Dotnet.CLI.Plus.MSBuild;

internal static class SnapshotCodec
{
    internal static RpcValue Encode(EvaluationSnapshot snapshot) =>
        WorkerProtocol.Map(
            ("projectPath", RpcValue.NewString(snapshot.ProjectPath)),
            (
                "properties",
                WorkerProtocol.Array(
                    snapshot.Properties,
                    property =>
                        WorkerProtocol.Map(
                            ("name", RpcValue.NewString(property.Name)),
                            ("value", RpcValue.NewString(property.Value))
                        )
                )
            ),
            (
                "items",
                WorkerProtocol.Array(
                    snapshot.Items,
                    item =>
                        WorkerProtocol.Map(
                            ("itemType", RpcValue.NewString(item.ItemType)),
                            ("include", RpcValue.NewString(item.EvaluatedInclude)),
                            ("resolvedPath", Optional(item.ResolvedPath)),
                            ("dimension", RpcValue.NewString(item.Dimension.TargetFramework)),
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
                        )
                )
            ),
            ("projectReferences", References(snapshot.ProjectReferences)),
            ("references", References(snapshot.References)),
            (
                "packages",
                WorkerProtocol.Array(
                    snapshot.Packages,
                    package =>
                        WorkerProtocol.Map(
                            ("id", RpcValue.NewString(package.Id)),
                            ("version", Optional(package.Version))
                        )
                )
            ),
            (
                "targetFrameworks",
                WorkerProtocol.Array(snapshot.TargetFrameworks, RpcValue.NewString)
            ),
            ("analyzers", WorkerProtocol.Array(snapshot.Analyzers, RpcValue.NewString)),
            ("imports", WorkerProtocol.Array(snapshot.Imports, RpcValue.NewString)),
            ("watchInputs", WorkerProtocol.Array(snapshot.WatchInputs, RpcValue.NewString)),
            ("globRoots", WorkerProtocol.Array(snapshot.GlobRoots, RpcValue.NewString)),
            ("capabilityProfile", RpcValue.NewString(snapshot.CapabilityProfile.ToString())),
            ("capabilities", WorkerProtocol.Array(snapshot.Capabilities, RpcValue.NewString)),
            (
                "diagnostics",
                WorkerProtocol.Array(
                    snapshot.Diagnostics,
                    diagnostic =>
                        WorkerProtocol.Map(
                            ("code", RpcValue.NewString(diagnostic.Code)),
                            ("message", RpcValue.NewString(diagnostic.Message)),
                            ("transient", RpcValue.NewBoolean(diagnostic.IsTransient))
                        )
                )
            )
        );

    internal static EvaluationSnapshot Decode(RpcValue value)
    {
        var fields = RpcValueModule.requireMap("snapshot", value);
        RpcValueModule.ensureOnly(
            "snapshot",
            [
                "projectPath",
                "properties",
                "items",
                "projectReferences",
                "references",
                "packages",
                "targetFrameworks",
                "analyzers",
                "imports",
                "watchInputs",
                "globRoots",
                "capabilityProfile",
                "capabilities",
                "diagnostics",
            ],
            fields
        );
        return new EvaluationSnapshot(
            RequiredString(fields, "projectPath"),
            Values(fields, "properties", Property),
            Values(fields, "items", Item),
            Values(fields, "projectReferences", Reference),
            Values(fields, "references", Reference),
            Values(fields, "packages", Package),
            Strings(fields, "targetFrameworks"),
            Strings(fields, "analyzers"),
            Strings(fields, "imports"),
            Strings(fields, "watchInputs"),
            Strings(fields, "globRoots"),
            Enum.Parse<MsBuildCapabilityProfile>(
                RequiredString(fields, "capabilityProfile"),
                false
            ),
            Strings(fields, "capabilities"),
            Values(fields, "diagnostics", Diagnostic)
        );
    }

    private static RpcValue References(ImmutableArray<EvaluatedReference> references) =>
        WorkerProtocol.Array(
            references,
            reference =>
                WorkerProtocol.Map(
                    ("include", RpcValue.NewString(reference.Include)),
                    ("resolvedPath", Optional(reference.ResolvedPath))
                )
        );

    private static RpcValue Optional(string? value) =>
        value is null ? RpcValue.Nil : RpcValue.NewString(value);

    private static EvaluatedProperty Property(RpcValue value)
    {
        var fields = RpcValueModule.requireMap("property", value);
        return new EvaluatedProperty(
            RequiredString(fields, "name"),
            RequiredString(fields, "value")
        );
    }

    private static EvaluatedItem Item(RpcValue value)
    {
        var fields = RpcValueModule.requireMap("item", value);
        return new EvaluatedItem(
            RequiredString(fields, "itemType"),
            RequiredString(fields, "include"),
            OptionalString(fields, "resolvedPath"),
            Values(fields, "metadata", Metadata),
            new EvaluationDimension(RequiredString(fields, "dimension"))
        );
    }

    private static EvaluatedMetadata Metadata(RpcValue value)
    {
        var fields = RpcValueModule.requireMap("metadata", value);
        return new EvaluatedMetadata(
            RequiredString(fields, "name"),
            RequiredString(fields, "value")
        );
    }

    private static EvaluatedReference Reference(RpcValue value)
    {
        var fields = RpcValueModule.requireMap("reference", value);
        return new EvaluatedReference(
            RequiredString(fields, "include"),
            OptionalString(fields, "resolvedPath")
        );
    }

    private static EvaluatedPackage Package(RpcValue value)
    {
        var fields = RpcValueModule.requireMap("package", value);
        return new EvaluatedPackage(
            RequiredString(fields, "id"),
            OptionalString(fields, "version")
        );
    }

    private static MsBuildDiagnostic Diagnostic(RpcValue value)
    {
        var fields = RpcValueModule.requireMap("diagnostic", value);
        return new MsBuildDiagnostic(
            RequiredString(fields, "code"),
            RequiredString(fields, "message"),
            RequiredBoolean(fields, "transient")
        );
    }

    private static ImmutableArray<T> Values<T>(
        ImmutableDictionary<string, RpcValue> fields,
        string name,
        Func<RpcValue, T> decode
    ) =>
        RpcValueModule
            .requireArray(name, RpcValueModule.requireField(name, fields))
            .Select(decode)
            .ToImmutableArray();

    private static ImmutableArray<string> Strings(
        ImmutableDictionary<string, RpcValue> fields,
        string name
    ) => Values(fields, name, value => RpcValueModule.requireString(name, value));

    private static string RequiredString(
        ImmutableDictionary<string, RpcValue> fields,
        string name
    ) => RpcValueModule.requireString(name, RpcValueModule.requireField(name, fields));

    private static string? OptionalString(
        ImmutableDictionary<string, RpcValue> fields,
        string name
    ) =>
        RpcValueModule.optionalField(name, fields) is { } value && value.Value != RpcValue.Nil
            ? RpcValueModule.requireString(name, value.Value)
            : null;

    private static bool RequiredBoolean(
        ImmutableDictionary<string, RpcValue> fields,
        string name
    ) =>
        RpcValueModule.requireField(name, fields) is RpcValue.Boolean value
            ? value.Item
            : throw new ArgumentException("Expected a boolean.", name);
}
