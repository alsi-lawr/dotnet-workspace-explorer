using Dotnet.WorkspaceExplorer.Rpc;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal static class ProjectEvaluationServer
{
    internal static async Task<int> RunAsync(string sdkPath, CancellationToken cancellationToken)
    {
        var frameLimit = MessagePackRpcCodec.secureLimits.MaximumValueBytes;
        using var evaluator = new ProjectSnapshotEvaluator(sdkPath);
        return await RpcHost.RunAsync(
            ProjectEvaluationRpc.Profile,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.Error,
            new Func<int>(() => frameLimit),
            new Func<RpcValue, CancellationToken, Task<HostedRpcResponse>>(
                (parameters, _) =>
                    Task.FromResult(
                        ProjectEvaluationRpc.Initialize(parameters, value => frameLimit = value)
                    )
            ),
            new Func<string, RpcValue, CancellationToken, Task<HostedRpcResponse>>(
                (method, parameters, _) => Task.FromResult(Dispatch(evaluator, method, parameters))
            ),
            cancellationToken
        );
    }

    private static HostedRpcResponse Dispatch(
        ProjectSnapshotEvaluator evaluator,
        string method,
        RpcValue parameters
    )
    {
        try
        {
            return method switch
            {
                "project-evaluation/evaluate" => Evaluate(evaluator, parameters),
                "project-evaluation/invalidate" => Invalidate(evaluator, parameters),
                "shutdown" => Shutdown(parameters),
                _ => HostedRpcResponse.Fail(RpcErrors.unknownMethod(method)),
            };
        }
        catch (ArgumentException)
        {
            return HostedRpcResponse.Fail(
                RpcErrors.invalidParams("The request parameters are malformed.")
            );
        }
    }

    private static HostedRpcResponse Evaluate(
        ProjectSnapshotEvaluator evaluator,
        RpcValue parameters
    )
    {
        var projectPath = ProjectEvaluationRpc.DecodeProjectPath(parameters);
        var outcome = evaluator.Evaluate(projectPath);
        return ProjectEvaluationOutcomes.TrySuccess(outcome, out var snapshot, out var failure)
            ? HostedRpcResponse.Ok(ProjectEvaluationMessagePack.Encode(snapshot!), false)
            : HostedRpcResponse.Fail(ProjectEvaluationOutcomes.ToRpcError(failure!));
    }

    private static HostedRpcResponse Invalidate(
        ProjectSnapshotEvaluator evaluator,
        RpcValue parameters
    ) =>
        HostedRpcResponse.Ok(
            ProjectEvaluationRpc.EncodeInvalidation(
                evaluator.Invalidate(ProjectEvaluationRpc.DecodePaths(parameters))
            ),
            false
        );

    private static HostedRpcResponse Shutdown(RpcValue parameters)
    {
        ProjectEvaluationRpc.ValidateShutdownRequest(parameters);
        return HostedRpcResponse.Ok(ProjectEvaluationRpc.ShutdownResult, true);
    }
}
