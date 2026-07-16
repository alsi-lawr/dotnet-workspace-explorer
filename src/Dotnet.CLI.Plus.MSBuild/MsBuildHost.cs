using System.Runtime.CompilerServices;
using Dotnet.CLI.Plus.Transport;
using Microsoft.Build.Locator;

namespace Dotnet.CLI.Plus.MSBuild;

public static class MsBuildHost
{
    public static Task<int> RunAsync(string toolsetPath, CancellationToken cancellationToken) =>
        RegisterThenRunAsync(Path.GetFullPath(toolsetPath), cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<int> RegisterThenRunAsync(
        string toolsetPath,
        CancellationToken cancellationToken
    )
    {
        if (!Directory.Exists(toolsetPath))
        {
            return Task.FromResult(2);
        }

        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterMSBuildPath(toolsetPath);
        }

        return RunRegisteredAsync(cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<int> RunRegisteredAsync(CancellationToken cancellationToken) =>
        WorkerServer.RunAsync(cancellationToken);
}

internal static class WorkerServer
{
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var evaluator = new WorkerEvaluator();
        var result = await RpcHost.RunAsync(
            WorkerProtocol.Profile,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.Error,
            new Func<RpcValue, CancellationToken, Task<RpcInteropResponse>>(
                (parameters, _) => Task.FromResult(WorkerProtocol.Initialize(parameters))
            ),
            new Func<string, RpcValue, CancellationToken, Task<RpcInteropResponse>>(
                (method, parameters, _) => Task.FromResult(Dispatch(evaluator, method, parameters))
            ),
            cancellationToken
        );
        return result;
    }

    private static RpcInteropResponse Dispatch(
        WorkerEvaluator evaluator,
        string method,
        RpcValue parameters
    ) =>
        method switch
        {
            "msbuild/evaluate" => Evaluate(evaluator, parameters),
            "msbuild/invalidate" => Invalidate(evaluator, parameters),
            "shutdown" => RpcInteropResponse.Ok(
                WorkerProtocol.Map(("accepted", RpcValue.NewBoolean(true))),
                true
            ),
            _ => RpcInteropResponse.Fail(RpcErrors.unknownMethod(method)),
        };

    private static RpcInteropResponse Evaluate(WorkerEvaluator evaluator, RpcValue parameters)
    {
        if (!WorkerProtocol.TryProjectPath(parameters, out var projectPath))
        {
            return RpcInteropResponse.Fail(
                RpcErrors.invalidParams("The evaluate parameters are malformed.")
            );
        }

        return evaluator.Evaluate(projectPath) switch
        {
            EvaluationOutcome.Success success => RpcInteropResponse.Ok(
                SnapshotCodec.Encode(success.Snapshot),
                false
            ),
            EvaluationOutcome.Failure failure => RpcInteropResponse.Fail(
                RpcErrors.create(failure.Code, failure.Message, null)
            ),
            _ => RpcInteropResponse.Fail(RpcErrors.internalError),
        };
    }

    private static RpcInteropResponse Invalidate(WorkerEvaluator evaluator, RpcValue parameters)
    {
        if (!WorkerProtocol.TryPaths(parameters, out var paths))
        {
            return RpcInteropResponse.Fail(
                RpcErrors.invalidParams("The invalidate parameters are malformed.")
            );
        }

        var result = evaluator.Invalidate(paths);
        return RpcInteropResponse.Ok(
            WorkerProtocol.Map(
                (
                    "invalidatedProjects",
                    WorkerProtocol.Array(result.InvalidatedProjects, RpcValue.NewString)
                )
            ),
            false
        );
    }
}
