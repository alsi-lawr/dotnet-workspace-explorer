using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Dotnet.WorkspaceExplorer.Rpc;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal sealed class ProjectEvaluationWorker : IAsyncDisposable
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly EvaluationWorkerLaunch launchSettings;
    private readonly DotnetSdkSelection selection;
    private EvaluationWorkerProcess? active;
    private uint nextRequestId = 1;
    private bool disabled;
    private bool closed;

    internal ProjectEvaluationWorker(
        EvaluationWorkerLaunch launchSettings,
        DotnetSdkSelection selection
    )
    {
        this.launchSettings = launchSettings;
        this.selection = selection;
    }

    internal Task<WorkspaceOutcome<ProjectEvaluationSnapshot>> EvaluateAsync(
        WorkspaceArtifactPath projectPath,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            "project-evaluation/evaluate",
            ProjectEvaluationRpc.Map(("projectPath", RpcValue.NewString(projectPath.Value))),
            ProjectEvaluationMessagePack.Decode,
            projectPath,
            cancellationToken
        );

    internal Task<WorkspaceOutcome<ProjectInvalidationResult>> InvalidateAsync(
        ImmutableArray<WorkspaceArtifactPath> paths,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            "project-evaluation/invalidate",
            ProjectEvaluationRpc.Map(
                ("paths", ProjectEvaluationRpc.Array(paths, path => RpcValue.NewString(path.Value)))
            ),
            ProjectEvaluationRpc.DecodeInvalidation,
            null,
            cancellationToken
        );

    internal async Task<WorkspaceOutcome<ProjectEvaluationReadiness>> WarmAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ProjectEvaluationOutcomes.Cancelled<ProjectEvaluationReadiness>(
                "The MSBuild evaluator warmup was cancelled."
            );
        }

        try
        {
            if (closed)
            {
                return ProjectEvaluationOutcomes.WorkerClosed<ProjectEvaluationReadiness>();
            }

            if (disabled)
            {
                return ProjectEvaluationOutcomes.ExternalToolFailed<ProjectEvaluationReadiness>(
                    "project-evaluation-host",
                    -1,
                    ProjectEvaluationDiagnosticCodes.WorkerDisabled,
                    "The MSBuild evaluator is disabled until refresh."
                );
            }

            for (var attemptNumber = 0; attemptNumber < 2; attemptNumber++)
            {
                var attempt = await TryEnsureStartedAsync(cancellationToken);

                switch (attempt)
                {
                    case EvaluationWorkerAttempt.Started:
                        return ProjectEvaluationOutcomes.Success(ProjectEvaluationReadiness.Ready);
                    case EvaluationWorkerAttempt.Cancelled:
                        await KillActiveAsync();
                        return ProjectEvaluationOutcomes.Cancelled<ProjectEvaluationReadiness>(
                            "The MSBuild evaluator warmup was cancelled."
                        );
                    case EvaluationWorkerAttempt.StartupFailed startup:
                        await KillActiveAsync();
                        return StartupFailure<ProjectEvaluationReadiness>(startup);
                    case EvaluationWorkerAttempt.TransportFailed when attemptNumber == 0:
                        await KillActiveAsync();
                        break;
                    case EvaluationWorkerAttempt.TransportFailed:
                        disabled = true;
                        await KillActiveAsync();
                        return ProjectEvaluationOutcomes.ExternalToolFailed<ProjectEvaluationReadiness>(
                            "project-evaluation-host",
                            -1,
                            ProjectEvaluationDiagnosticCodes.WorkerCrashed,
                            "The MSBuild evaluator stopped unexpectedly after one restart.",
                            true
                        );
                    default:
                        return ProjectEvaluationOutcomes.Internal<ProjectEvaluationReadiness>(
                            ProjectEvaluationDiagnosticCodes.WorkerCrashed,
                            "The MSBuild evaluator warmup did not complete safely."
                        );
                }
            }

            return ProjectEvaluationOutcomes.Internal<ProjectEvaluationReadiness>(
                ProjectEvaluationDiagnosticCodes.WorkerCrashed,
                "The MSBuild evaluator warmup did not complete safely."
            );
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (closed)
            {
                return;
            }

            closed = true;
            await GracefulStopAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorkspaceOutcome<T>> ExecuteAsync<T>(
        string method,
        RpcValue parameters,
        Func<RpcValue, T> decode,
        WorkspaceArtifactPath? projectPath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ProjectEvaluationOutcomes.Cancelled<T>("The MSBuild operation was cancelled.");
        }

        try
        {
            if (closed)
            {
                return ProjectEvaluationOutcomes.WorkerClosed<T>();
            }

            if (disabled)
            {
                return ProjectEvaluationOutcomes.ExternalToolFailed<T>(
                    "project-evaluation-host",
                    -1,
                    ProjectEvaluationDiagnosticCodes.WorkerDisabled,
                    "The MSBuild evaluator is disabled until refresh."
                );
            }

            for (var attemptNumber = 0; attemptNumber < 2; attemptNumber++)
            {
                var attempt = await TryEnsureStartedAsync(cancellationToken);
                if (attempt is EvaluationWorkerAttempt.Started ready)
                {
                    attempt = await SendAsync(ready.Worker, method, parameters, cancellationToken);
                }

                if (attempt is EvaluationWorkerAttempt.Received received)
                {
                    try
                    {
                        return ProjectEvaluationOutcomes.Success(decode(received.Result));
                    }
                    catch (Exception error)
                        when (error is ArgumentException or FormatException or OverflowException)
                    {
                        attempt = new EvaluationWorkerAttempt.TransportFailed();
                    }
                }

                switch (attempt)
                {
                    case EvaluationWorkerAttempt.Cancelled:
                        await KillActiveAsync();
                        return ProjectEvaluationOutcomes.Cancelled<T>(
                            "The MSBuild operation was cancelled."
                        );
                    case EvaluationWorkerAttempt.RpcFailed rpc:
                        return ProjectEvaluationOutcomes.FromRpcError<T>(rpc.Error, projectPath);
                    case EvaluationWorkerAttempt.StartupFailed startup:
                        await KillActiveAsync();
                        return StartupFailure<T>(startup);
                    case EvaluationWorkerAttempt.TransportFailed when attemptNumber == 0:
                        await KillActiveAsync();
                        break;
                    case EvaluationWorkerAttempt.TransportFailed:
                        disabled = true;
                        await KillActiveAsync();
                        return ProjectEvaluationOutcomes.ExternalToolFailed<T>(
                            "project-evaluation-host",
                            -1,
                            ProjectEvaluationDiagnosticCodes.WorkerCrashed,
                            "The MSBuild evaluator stopped unexpectedly after one restart.",
                            true
                        );
                    default:
                        return ProjectEvaluationOutcomes.Internal<T>(
                            ProjectEvaluationDiagnosticCodes.WorkerCrashed,
                            "The MSBuild evaluator retry policy did not complete safely."
                        );
                }
            }

            return ProjectEvaluationOutcomes.Internal<T>(
                ProjectEvaluationDiagnosticCodes.WorkerCrashed,
                "The MSBuild evaluator retry policy did not complete safely."
            );
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<EvaluationWorkerAttempt> TryEnsureStartedAsync(
        CancellationToken cancellationToken
    )
    {
        if (active is { Process.HasExited: false, Initialized: true } running)
        {
            return new EvaluationWorkerAttempt.Started(running);
        }

        if (active is not null)
        {
            await KillActiveAsync();
        }

        Process? process;
        try
        {
            process = Process.Start(launchSettings.CreateStartInfo(selection));
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new EvaluationWorkerAttempt.StartupFailed(
                EvaluationWorkerStartup.HostStartFailed,
                -1
            );
        }

        if (process is null)
        {
            return new EvaluationWorkerAttempt.StartupFailed(
                EvaluationWorkerStartup.HostStartFailed,
                -1
            );
        }

        var state = new EvaluationWorkerProcess(
            process,
            DrainStandardErrorAsync(process.StandardError)
        );
        active = state;
        var initialize = await SendAsync(
            state,
            "initialize",
            ProjectEvaluationRpc.InitializeRequest(
                MessagePackRpcCodec.secureLimits.MaximumValueBytes
            ),
            cancellationToken,
            MessagePackRpcCodec.secureLimits.MaximumValueBytes
        );
        switch (initialize)
        {
            case EvaluationWorkerAttempt.Received response:
                try
                {
                    state.FrameLimit = ProjectEvaluationRpc.DecodeInitializeResult(response.Result);
                    state.Initialized = true;
                    return new EvaluationWorkerAttempt.Started(state);
                }
                catch (Exception error) when (error is ArgumentException or OverflowException)
                {
                    return new EvaluationWorkerAttempt.TransportFailed();
                }
            case EvaluationWorkerAttempt.RpcFailed:
                return new EvaluationWorkerAttempt.StartupFailed(
                    EvaluationWorkerStartup.ProtocolRejected,
                    ExitCode(state.Process)
                );
            case EvaluationWorkerAttempt.TransportFailed:
                return await ClassifyStartupFailureAsync(state);
            default:
                return initialize;
        }
    }

    private async Task<EvaluationWorkerAttempt> SendAsync(
        EvaluationWorkerProcess worker,
        string method,
        RpcValue parameters,
        CancellationToken cancellationToken,
        int? frameLimit = null
    )
    {
        var id = nextRequestId++;
        byte[] bytes;
        try
        {
            bytes = MessagePackRpcCodec.encodeFrame(RpcFrame.NewRequest(id, method, parameters));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new EvaluationWorkerAttempt.TransportFailed();
        }

        var limit = frameLimit ?? worker.FrameLimit;
        if (bytes.Length > limit)
        {
            return new EvaluationWorkerAttempt.TransportFailed();
        }

        try
        {
            await worker.Process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            await worker.Process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            return await ReadResponseAsync(
                worker.Process.StandardOutput.BaseStream,
                id,
                limit,
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return new EvaluationWorkerAttempt.Cancelled();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return new EvaluationWorkerAttempt.TransportFailed();
        }
    }

    internal static async Task<EvaluationWorkerAttempt> ReadResponseAsync(
        Stream output,
        uint expectedId,
        int frameLimit,
        CancellationToken cancellationToken
    )
    {
        var read = await RpcFrameReader.ReadOneAsync(output, frameLimit, cancellationToken);
        if (read.IsError)
        {
            return read.ErrorValue == RpcFrameReadFailure.Cancelled
                ? new EvaluationWorkerAttempt.Cancelled()
                : new EvaluationWorkerAttempt.TransportFailed();
        }

        var decoded = MessagePackRpcCodec.decodeFrame(
            MessagePackRpcCodec.secureLimits,
            read.ResultValue
        );
        if (decoded.IsError || decoded.ResultValue is not RpcFrameDecodeResult.Frame frame)
        {
            return new EvaluationWorkerAttempt.TransportFailed();
        }

        if (frame.Item is not RpcFrame.Response response || response.messageId != expectedId)
        {
            return new EvaluationWorkerAttempt.TransportFailed();
        }

        return response.outcome.IsError
            ? new EvaluationWorkerAttempt.RpcFailed(response.outcome.ErrorValue)
            : new EvaluationWorkerAttempt.Received(response.outcome.ResultValue);
    }

    private async Task GracefulStopAsync()
    {
        var worker = active;
        if (worker is null)
        {
            return;
        }

        var stopped = worker.Process.HasExited;
        if (!stopped && worker.Initialized)
        {
            using var timeout = new CancellationTokenSource(ProcessExitTimeout);
            var response = await SendAsync(
                worker,
                "shutdown",
                RpcValueModule.emptyMap,
                timeout.Token
            );
            if (response is EvaluationWorkerAttempt.Received shutdown)
            {
                try
                {
                    ProjectEvaluationRpc.ValidateShutdownResult(shutdown.Result);
                    await worker.Process.WaitForExitAsync(timeout.Token);
                    stopped = worker.Process.ExitCode == 0;
                }
                catch (Exception error)
                    when (error is ArgumentException or OperationCanceledException)
                {
                    stopped = false;
                }
            }
        }

        if (!stopped && !worker.Process.HasExited)
        {
            await KillAndReapAsync(worker.Process);
        }

        active = null;
        await worker.StderrDrain;
        worker.Process.Dispose();
    }

    private async Task KillActiveAsync()
    {
        var worker = active;
        active = null;
        if (worker is null)
        {
            return;
        }

        await KillAndReapAsync(worker.Process);
        await worker.StderrDrain;
        worker.Process.Dispose();
    }

    private static async Task KillAndReapAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            await process.WaitForExitAsync();
            return;
        }

        await process.WaitForExitAsync();
    }

    private static async Task<EvaluationWorkerAttempt> ClassifyStartupFailureAsync(
        EvaluationWorkerProcess worker
    )
    {
        if (!worker.Process.HasExited)
        {
            using var timeout = new CancellationTokenSource(ProcessExitTimeout);
            try
            {
                await worker.Process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception error)
                when (error is OperationCanceledException or InvalidOperationException)
            {
                return new EvaluationWorkerAttempt.TransportFailed();
            }
        }

        var diagnostic = await worker.StderrDrain;
        return diagnostic == EvaluationWorkerStartup.None
            ? new EvaluationWorkerAttempt.TransportFailed()
            : new EvaluationWorkerAttempt.StartupFailed(diagnostic, worker.Process.ExitCode);
    }

    private static async Task<EvaluationWorkerStartup> DrainStandardErrorAsync(StreamReader error)
    {
        var retained = new StringBuilder(512);
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                var count = await error.ReadAsync(buffer);
                if (count == 0)
                {
                    break;
                }

                if (retained.Length < retained.Capacity)
                {
                    retained.Append(
                        buffer,
                        0,
                        Math.Min(count, retained.Capacity - retained.Length)
                    );
                }
            }
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            return EvaluationWorkerStartup.None;
        }

        var safe = retained.ToString();
        if (safe.Contains("project-evaluation-host:sdk-not-found", StringComparison.Ordinal))
        {
            return EvaluationWorkerStartup.ToolsetNotFound;
        }

        if (
            safe.Contains(
                "project-evaluation-host:locator-registration-failed",
                StringComparison.Ordinal
            )
        )
        {
            return EvaluationWorkerStartup.LocatorRegistrationFailed;
        }

        if (safe.Contains("project-evaluation-host:sdk-load-failed", StringComparison.Ordinal))
        {
            return EvaluationWorkerStartup.ToolsetLoadFailed;
        }

        return EvaluationWorkerStartup.None;
    }

    private static int ExitCode(Process process) => process.HasExited ? process.ExitCode : -1;

    private static WorkspaceOutcome<T> StartupFailure<T>(
        EvaluationWorkerAttempt.StartupFailed startup
    ) =>
        startup.Kind switch
        {
            EvaluationWorkerStartup.HostStartFailed =>
                ProjectEvaluationOutcomes.ExternalToolFailed<T>(
                    "project-evaluation-host",
                    startup.ExitCode,
                    ProjectEvaluationDiagnosticCodes.SdkStartFailed,
                    "The MSBuild evaluator process could not be started."
                ),
            EvaluationWorkerStartup.ToolsetNotFound =>
                ProjectEvaluationOutcomes.ExternalToolFailed<T>(
                    "project-evaluation-host",
                    startup.ExitCode,
                    ProjectEvaluationDiagnosticCodes.ToolsetIncompatible,
                    "The selected SDK toolset directory is unavailable."
                ),
            EvaluationWorkerStartup.LocatorRegistrationFailed
            or EvaluationWorkerStartup.ToolsetLoadFailed
            or EvaluationWorkerStartup.ProtocolRejected =>
                ProjectEvaluationOutcomes.ExternalToolFailed<T>(
                    "project-evaluation-host",
                    startup.ExitCode,
                    ProjectEvaluationDiagnosticCodes.ToolsetIncompatible,
                    "The selected SDK toolset could not initialize the MSBuild evaluator."
                ),
            _ => ProjectEvaluationOutcomes.ExternalToolFailed<T>(
                "project-evaluation-host",
                startup.ExitCode,
                ProjectEvaluationDiagnosticCodes.WorkerCrashed,
                "The MSBuild evaluator stopped during startup."
            ),
        };

    internal sealed class EvaluationWorkerProcess(
        Process process,
        Task<EvaluationWorkerStartup> stderrDrain
    )
    {
        internal Process Process { get; } = process;
        internal Task<EvaluationWorkerStartup> StderrDrain { get; } = stderrDrain;
        internal int FrameLimit { get; set; } = MessagePackRpcCodec.secureLimits.MaximumValueBytes;
        internal bool Initialized { get; set; }
    }

    internal abstract record EvaluationWorkerAttempt
    {
        private EvaluationWorkerAttempt() { }

        internal sealed record Started(EvaluationWorkerProcess Worker) : EvaluationWorkerAttempt;

        internal sealed record Received(RpcValue Result) : EvaluationWorkerAttempt;

        internal sealed record RpcFailed(RpcError Error) : EvaluationWorkerAttempt;

        internal sealed record StartupFailed(EvaluationWorkerStartup Kind, int ExitCode)
            : EvaluationWorkerAttempt;

        internal sealed record TransportFailed : EvaluationWorkerAttempt
        {
            internal TransportFailed() { }
        }

        internal sealed record Cancelled : EvaluationWorkerAttempt
        {
            internal Cancelled() { }
        }
    }

    internal enum EvaluationWorkerStartup
    {
        None,
        HostStartFailed,
        ToolsetNotFound,
        LocatorRegistrationFailed,
        ToolsetLoadFailed,
        ProtocolRejected,
    }
}
