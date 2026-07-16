using System.Collections.Immutable;
using Dotnet.CLI.Plus.Transport;

namespace Dotnet.CLI.Plus.MSBuild;

internal static class WorkerProtocol
{
    internal const string ProfileName = "dotnet-cli-plus/msbuild";
    internal const int ProtocolMajor = 1;
    internal const int ProtocolMinor = 0;

    internal static readonly RpcProfile Profile = RpcHost.CreateProfile(
        ProfileName,
        ProtocolMajor,
        ProtocolMinor,
        ["initialize", "msbuild/evaluate", "msbuild/invalidate", "shutdown"]
    );

    internal static RpcValue Map(params (string Name, RpcValue Value)[] values) =>
        RpcValueModule.map(values.Select(value => Tuple.Create(value.Name, value.Value)));

    internal static RpcValue Array<T>(IEnumerable<T> values, Func<T, RpcValue> encode) =>
        RpcValueModule.array(values.Select(encode));

    internal static RpcInteropResponse Initialize(RpcValue parameters)
    {
        try
        {
            var fields = RpcValueModule.requireMap("parameters", parameters);
            RpcValueModule.ensureOnly(
                "parameters",
                ["protocolVersion", "profile", "limits"],
                fields
            );
            var profile = RpcValueModule.requireString(
                "profile",
                RpcValueModule.requireField("profile", fields)
            );
            var version = RpcValueModule.requireMap(
                "protocolVersion",
                RpcValueModule.requireField("protocolVersion", fields)
            );
            var major = RpcValueModule.requireInteger(
                "major",
                RpcValueModule.requireField("major", version)
            );

            if (!StringComparer.Ordinal.Equals(profile, ProfileName) || major != ProtocolMajor)
            {
                return RpcInteropResponse.Fail(
                    RpcErrors.invalidParams(
                        "The requested private protocol profile is not supported."
                    )
                );
            }

            return RpcInteropResponse.Ok(
                Map(
                    ("profile", RpcValue.NewString(ProfileName)),
                    (
                        "protocolVersion",
                        Map(
                            ("major", RpcValue.NewInteger(ProtocolMajor)),
                            ("minor", RpcValue.NewInteger(ProtocolMinor))
                        )
                    ),
                    (
                        "limits",
                        Map(
                            (
                                "maxFrameBytes",
                                RpcValue.NewInteger(RpcCodec.secureLimits.MaximumValueBytes)
                            )
                        )
                    )
                ),
                false
            );
        }
        catch (ArgumentException)
        {
            return RpcInteropResponse.Fail(
                RpcErrors.invalidParams("The initialize parameters are malformed.")
            );
        }
    }

    internal static bool TryProjectPath(RpcValue parameters, out string projectPath)
    {
        try
        {
            var fields = RpcValueModule.requireMap("parameters", parameters);
            RpcValueModule.ensureOnly("parameters", ["projectPath"], fields);
            projectPath = RpcValueModule.requireString(
                "projectPath",
                RpcValueModule.requireField("projectPath", fields)
            );
            return !string.IsNullOrWhiteSpace(projectPath);
        }
        catch (ArgumentException)
        {
            projectPath = string.Empty;
            return false;
        }
    }

    internal static bool TryPaths(RpcValue parameters, out ImmutableArray<string> paths)
    {
        try
        {
            var fields = RpcValueModule.requireMap("parameters", parameters);
            RpcValueModule.ensureOnly("parameters", ["paths"], fields);
            paths = RpcValueModule
                .requireArray("paths", RpcValueModule.requireField("paths", fields))
                .Select(value => RpcValueModule.requireString("paths", value))
                .ToImmutableArray();
            return true;
        }
        catch (ArgumentException)
        {
            paths = [];
            return false;
        }
    }
}
