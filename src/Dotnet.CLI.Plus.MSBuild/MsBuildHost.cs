using System.Runtime.CompilerServices;
using Dotnet.CLI.Plus.Transport;
using Microsoft.Build.Locator;

namespace Dotnet.CLI.Plus.MSBuild;

internal static class MsBuildHost
{
    private const int InvalidToolsetExitCode = 66;
    private const int ToolsetLoadExitCode = 70;

    internal static Task<int> RunAsync(string toolsetPath, CancellationToken cancellationToken) =>
        RegisterThenRunAsync(Path.GetFullPath(toolsetPath), cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<int> RegisterThenRunAsync(
        string toolsetPath,
        CancellationToken cancellationToken
    )
    {
        if (!Directory.Exists(toolsetPath))
        {
            Console.Error.WriteLine("msbuild-host:toolset-not-found");
            return Task.FromResult(InvalidToolsetExitCode);
        }

        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterMSBuildPath(toolsetPath);
            }
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("msbuild-host:locator-registration-failed");
            return Task.FromResult(ToolsetLoadExitCode);
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine("msbuild-host:locator-registration-failed");
            return Task.FromResult(ToolsetLoadExitCode);
        }

        return RunRegisteredAsync(cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunRegisteredAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await WorkerServer.RunAsync(cancellationToken);
        }
        catch (FileLoadException)
        {
            Console.Error.WriteLine("msbuild-host:toolset-load-failed");
            return ToolsetLoadExitCode;
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine("msbuild-host:toolset-load-failed");
            return ToolsetLoadExitCode;
        }
        catch (TypeLoadException)
        {
            Console.Error.WriteLine("msbuild-host:toolset-load-failed");
            return ToolsetLoadExitCode;
        }
    }
}

internal static class WorkerServer
{
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var frameLimit = RpcCodec.secureLimits.MaximumValueBytes;
        using var evaluator = new WorkerEvaluator();
        return await RpcHost.RunAsync(
            WorkerProtocol.Profile,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.Error,
            new Func<int>(() => frameLimit),
            new Func<RpcValue, CancellationToken, Task<RpcInteropResponse>>(
                (parameters, _) =>
                    Task.FromResult(
                        WorkerProtocol.Initialize(parameters, value => frameLimit = value)
                    )
            ),
            new Func<string, RpcValue, CancellationToken, Task<RpcInteropResponse>>(
                (method, parameters, _) => Task.FromResult(Dispatch(evaluator, method, parameters))
            ),
            cancellationToken
        );
    }

    private static RpcInteropResponse Dispatch(
        WorkerEvaluator evaluator,
        string method,
        RpcValue parameters
    )
    {
        try
        {
            return method switch
            {
                "msbuild/evaluate" => Evaluate(evaluator, parameters),
                "msbuild/invalidate" => Invalidate(evaluator, parameters),
                "shutdown" => Shutdown(parameters),
                _ => RpcInteropResponse.Fail(RpcErrors.unknownMethod(method)),
            };
        }
        catch (ArgumentException)
        {
            return RpcInteropResponse.Fail(
                RpcErrors.invalidParams("The request parameters are malformed.")
            );
        }
    }

    private static RpcInteropResponse Evaluate(WorkerEvaluator evaluator, RpcValue parameters)
    {
        var projectPath = WorkerProtocol.DecodeProjectPath(parameters);
        var outcome = evaluator.Evaluate(projectPath);
        return CoreOutcomes.TrySuccess(outcome, out var snapshot, out var failure)
            ? RpcInteropResponse.Ok(SnapshotCodec.Encode(snapshot!), false)
            : RpcInteropResponse.Fail(CoreOutcomes.ToRpcError(failure!));
    }

    private static RpcInteropResponse Invalidate(WorkerEvaluator evaluator, RpcValue parameters) =>
        RpcInteropResponse.Ok(
            WorkerProtocol.EncodeInvalidation(
                evaluator.Invalidate(WorkerProtocol.DecodePaths(parameters))
            ),
            false
        );

    private static RpcInteropResponse Shutdown(RpcValue parameters)
    {
        WorkerProtocol.ValidateShutdownRequest(parameters);
        return RpcInteropResponse.Ok(WorkerProtocol.ShutdownResult, true);
    }
}
