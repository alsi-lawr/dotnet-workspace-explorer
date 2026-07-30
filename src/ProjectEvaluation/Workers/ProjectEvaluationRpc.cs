using System.Collections.Immutable;
using Dotnet.WorkspaceExplorer.Rpc;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal static class ProjectEvaluationRpc
{
    internal const string ProfileName = "dotnet-workspace-explorer/project-evaluation";
    internal const int ProtocolMajor = 2;
    internal const int ProtocolMinor = 0;

    internal static readonly RpcProfile Profile = RpcHost.CreateProfile(
        ProfileName,
        ProtocolMajor,
        ProtocolMinor,
        ["initialize", "project-evaluation/evaluate", "project-evaluation/invalidate", "shutdown"]
    );

    internal static RpcValue Map(params (string Name, RpcValue Value)[] values) =>
        RpcValueModule.map(values.Select(value => Tuple.Create(value.Name, value.Value)));

    internal static RpcValue Array<T>(IEnumerable<T> values, Func<T, RpcValue> encode) =>
        RpcValueModule.array(values.Select(encode));

    internal static HostedRpcResponse Initialize(RpcValue parameters, Action<int> setFrameLimit)
    {
        try
        {
            var fields = ExactMap(
                "parameters",
                parameters,
                ["profile", "protocolVersion", "limits"]
            );
            var version = ExactMap(
                "protocolVersion",
                Field(fields, "protocolVersion"),
                ["major", "minor"]
            );
            var limits = ExactMap("limits", Field(fields, "limits"), ["maxFrameBytes"]);
            var requestedLimit = Integer(limits, "maxFrameBytes");
            if (
                !StringComparer.Ordinal.Equals(String(fields, "profile"), ProfileName)
                || Integer(version, "major") != ProtocolMajor
                || Integer(version, "minor") < ProtocolMinor
                || requestedLimit < 1
                || requestedLimit > MessagePackRpcCodec.secureLimits.MaximumValueBytes
            )
            {
                return HostedRpcResponse.Fail(
                    RpcErrors.invalidParams(
                        "The requested private protocol profile or limits are unsupported."
                    )
                );
            }

            var negotiatedLimit = checked((int)requestedLimit);
            setFrameLimit(negotiatedLimit);
            return HostedRpcResponse.Ok(InitializePayload(negotiatedLimit), false);
        }
        catch (ArgumentException)
        {
            return HostedRpcResponse.Fail(
                RpcErrors.invalidParams("The initialize parameters are malformed.")
            );
        }
        catch (OverflowException)
        {
            return HostedRpcResponse.Fail(
                RpcErrors.invalidParams("The initialize parameters are malformed.")
            );
        }
    }

    internal static RpcValue InitializeRequest(int maximumFrameBytes) =>
        InitializePayload(maximumFrameBytes);

    internal static int DecodeInitializeResult(RpcValue value)
    {
        var fields = ExactMap("initializeResult", value, ["profile", "protocolVersion", "limits"]);
        var version = ExactMap(
            "protocolVersion",
            Field(fields, "protocolVersion"),
            ["major", "minor"]
        );
        var limits = ExactMap("limits", Field(fields, "limits"), ["maxFrameBytes"]);
        var maximumFrameBytes = Integer(limits, "maxFrameBytes");
        if (
            !StringComparer.Ordinal.Equals(String(fields, "profile"), ProfileName)
            || Integer(version, "major") != ProtocolMajor
            || Integer(version, "minor") != ProtocolMinor
            || maximumFrameBytes < 1
            || maximumFrameBytes > MessagePackRpcCodec.secureLimits.MaximumValueBytes
        )
        {
            throw new ArgumentException(
                "The worker initialize response is incompatible.",
                nameof(value)
            );
        }

        return checked((int)maximumFrameBytes);
    }

    internal static WorkspaceArtifactPath DecodeProjectPath(RpcValue parameters)
    {
        var fields = ExactMap("parameters", parameters, ["projectPath"]);
        return WorkspaceArtifactPath.Create(String(fields, "projectPath"));
    }

    internal static ImmutableArray<WorkspaceArtifactPath> DecodePaths(RpcValue parameters)
    {
        var fields = ExactMap("parameters", parameters, ["paths"]);
        return RpcValueModule
            .requireArray("paths", Field(fields, "paths"))
            .Select(value =>
                WorkspaceArtifactPath.Create(RpcValueModule.requireString("path", value))
            )
            .ToImmutableArray();
    }

    internal static RpcValue EncodeInvalidation(ProjectInvalidationResult result) =>
        Map(
            (
                "invalidatedProjects",
                Array(result.InvalidatedProjects, path => RpcValue.NewString(path.Value))
            )
        );

    internal static ProjectInvalidationResult DecodeInvalidation(RpcValue value)
    {
        var fields = ExactMap("invalidationResult", value, ["invalidatedProjects"]);
        return new ProjectInvalidationResult(
            RpcValueModule
                .requireArray("invalidatedProjects", Field(fields, "invalidatedProjects"))
                .Select(path =>
                    WorkspaceArtifactPath.Create(RpcValueModule.requireString("path", path))
                )
                .ToImmutableArray()
        );
    }

    internal static RpcValue ShutdownResult => Map(("accepted", RpcValue.NewBoolean(true)));

    internal static void ValidateShutdownRequest(RpcValue parameters) =>
        _ = ExactMap("parameters", parameters, []);

    internal static void ValidateShutdownResult(RpcValue value)
    {
        var fields = ExactMap("shutdownResult", value, ["accepted"]);
        if (Field(fields, "accepted") is not RpcValue.Boolean accepted || !accepted.Item)
        {
            throw new ArgumentException("The worker rejected shutdown.", nameof(value));
        }
    }

    internal static ImmutableDictionary<string, RpcValue> ExactMap(
        string name,
        RpcValue value,
        params string[] expected
    )
    {
        var fields = RpcValueModule.requireMap(name, value);
        RpcValueModule.ensureOnly(name, expected, fields);
        foreach (var field in expected)
        {
            _ = Field(fields, field);
        }

        return fields;
    }

    internal static RpcValue Field(ImmutableDictionary<string, RpcValue> fields, string name) =>
        RpcValueModule.requireField(name, fields);

    internal static string String(ImmutableDictionary<string, RpcValue> fields, string name) =>
        RpcValueModule.requireString(name, Field(fields, name));

    internal static long Integer(ImmutableDictionary<string, RpcValue> fields, string name) =>
        RpcValueModule.requireInteger(name, Field(fields, name));

    private static RpcValue InitializePayload(int maximumFrameBytes) =>
        Map(
            ("profile", RpcValue.NewString(ProfileName)),
            (
                "protocolVersion",
                Map(
                    ("major", RpcValue.NewInteger(ProtocolMajor)),
                    ("minor", RpcValue.NewInteger(ProtocolMinor))
                )
            ),
            ("limits", Map(("maxFrameBytes", RpcValue.NewInteger(maximumFrameBytes))))
        );
}
