using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Dotnet.CLI.Plus.Core;
using Dotnet.CLI.Plus.Transport;

namespace Dotnet.CLI.Plus.MSBuild;

internal sealed class WorkerClient : IAsyncDisposable
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly WorkerLaunchSettings launchSettings;
    private readonly ToolsetSelection selection;
    private WorkerProcessState? active;
    private uint nextRequestId = 1;
    private bool disabled;
    private bool closed;

    internal WorkerClient(WorkerLaunchSettings launchSettings, ToolsetSelection selection)
    {
        this.launchSettings = launchSettings;
        this.selection = selection;
    }

    internal Task<WorkspaceOutcome<EvaluationSnapshot>> EvaluateAsync(
        WorkspaceArtifactPath projectPath,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            "msbuild/evaluate",
            WorkerProtocol.Map(("projectPath", RpcValue.NewString(projectPath.Value))),
            SnapshotCodec.Decode,
            projectPath,
            cancellationToken
        );

    internal Task<WorkspaceOutcome<InvalidationResult>> InvalidateAsync(
        ImmutableArray<WorkspaceArtifactPath> paths,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            "msbuild/invalidate",
            WorkerProtocol.Map(
                ("paths", WorkerProtocol.Array(paths, path => RpcValue.NewString(path.Value)))
            ),
            WorkerProtocol.DecodeInvalidation,
            null,
            cancellationToken
        );

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
            return CoreOutcomes.Cancelled<T>("The MSBuild operation was cancelled.");
        }

        try
        {
            if (closed)
            {
                return CoreOutcomes.WorkerClosed<T>();
            }

            if (disabled)
            {
                return CoreOutcomes.ExternalToolFailed<T>(
                    "msbuild-host",
                    -1,
                    MsBuildDiagnosticCodes.WorkerDisabled,
                    "The MSBuild evaluator is disabled until refresh."
                );
            }

            for (var attemptNumber = 0; attemptNumber < 2; attemptNumber++)
            {
                var attempt = await TryEnsureStartedAsync(cancellationToken);
                if (attempt is WorkerAttempt.Started ready)
                {
                    attempt = await SendAsync(ready.Worker, method, parameters, cancellationToken);
                }

                if (attempt is WorkerAttempt.Received received)
                {
                    try
                    {
                        return CoreOutcomes.Success(decode(received.Result));
                    }
                    catch (Exception error)
                        when (error is ArgumentException or FormatException or OverflowException)
                    {
                        attempt = new WorkerAttempt.TransportFailed();
                    }
                }

                switch (attempt)
                {
                    case WorkerAttempt.Cancelled:
                        await KillActiveAsync();
                        return CoreOutcomes.Cancelled<T>("The MSBuild operation was cancelled.");
                    case WorkerAttempt.RpcFailed rpc:
                        return CoreOutcomes.FromRpcError<T>(rpc.Error, projectPath);
                    case WorkerAttempt.StartupFailed startup:
                        await KillActiveAsync();
                        return StartupFailure<T>(startup);
                    case WorkerAttempt.TransportFailed when attemptNumber == 0:
                        await KillActiveAsync();
                        break;
                    case WorkerAttempt.TransportFailed:
                        disabled = true;
                        await KillActiveAsync();
                        return CoreOutcomes.ExternalToolFailed<T>(
                            "msbuild-host",
                            -1,
                            MsBuildDiagnosticCodes.WorkerCrashed,
                            "The MSBuild evaluator stopped unexpectedly after one restart.",
                            true
                        );
                    default:
                        return CoreOutcomes.Internal<T>(
                            MsBuildDiagnosticCodes.WorkerCrashed,
                            "The MSBuild evaluator retry policy did not complete safely."
                        );
                }
            }

            return CoreOutcomes.Internal<T>(
                MsBuildDiagnosticCodes.WorkerCrashed,
                "The MSBuild evaluator retry policy did not complete safely."
            );
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorkerAttempt> TryEnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (active is { Process.HasExited: false, Initialized: true } running)
        {
            return new WorkerAttempt.Started(running);
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
            return new WorkerAttempt.StartupFailed(WorkerStartupKind.HostStartFailed, -1);
        }

        if (process is null)
        {
            return new WorkerAttempt.StartupFailed(WorkerStartupKind.HostStartFailed, -1);
        }

        var state = new WorkerProcessState(process, DrainStandardErrorAsync(process.StandardError));
        active = state;
        launchSettings.ProcessStarted?.Invoke(process);

        var initialize = await SendAsync(
            state,
            "initialize",
            WorkerProtocol.InitializeRequest(RpcCodec.secureLimits.MaximumValueBytes),
            cancellationToken,
            RpcCodec.secureLimits.MaximumValueBytes
        );
        switch (initialize)
        {
            case WorkerAttempt.Received response:
                try
                {
                    state.FrameLimit = WorkerProtocol.DecodeInitializeResult(response.Result);
                    state.Initialized = true;
                    return new WorkerAttempt.Started(state);
                }
                catch (Exception error) when (error is ArgumentException or OverflowException)
                {
                    return new WorkerAttempt.TransportFailed();
                }
            case WorkerAttempt.RpcFailed:
                return new WorkerAttempt.StartupFailed(
                    WorkerStartupKind.ProtocolRejected,
                    ExitCode(state.Process)
                );
            case WorkerAttempt.TransportFailed:
                return await ClassifyStartupFailureAsync(state);
            default:
                return initialize;
        }
    }

    private async Task<WorkerAttempt> SendAsync(
        WorkerProcessState worker,
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
            bytes = RpcCodec.encodeFrame(RpcFrame.NewRequest(id, method, parameters));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new WorkerAttempt.TransportFailed();
        }

        var limit = frameLimit ?? worker.FrameLimit;
        if (bytes.Length > limit)
        {
            return new WorkerAttempt.TransportFailed();
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
            return new WorkerAttempt.Cancelled();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return new WorkerAttempt.TransportFailed();
        }
    }

    internal static async Task<WorkerAttempt> ReadResponseAsync(
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
                ? new WorkerAttempt.Cancelled()
                : new WorkerAttempt.TransportFailed();
        }

        var decoded = RpcCodec.decodeFrame(RpcCodec.secureLimits, read.ResultValue);
        if (decoded.IsError || decoded.ResultValue is not RpcFrameDecodeResult.Frame frame)
        {
            return new WorkerAttempt.TransportFailed();
        }

        if (frame.Item is not RpcFrame.Response response || response.messageId != expectedId)
        {
            return new WorkerAttempt.TransportFailed();
        }

        return response.error is { } error
            ? new WorkerAttempt.RpcFailed(error.Value)
            : new WorkerAttempt.Received(response.result);
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
            if (response is WorkerAttempt.Received shutdown)
            {
                try
                {
                    WorkerProtocol.ValidateShutdownResult(shutdown.Result);
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

    private static async Task<WorkerAttempt> ClassifyStartupFailureAsync(WorkerProcessState worker)
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
                return new WorkerAttempt.TransportFailed();
            }
        }

        var diagnostic = await worker.StderrDrain;
        return diagnostic == WorkerStartupKind.None
            ? new WorkerAttempt.TransportFailed()
            : new WorkerAttempt.StartupFailed(diagnostic, worker.Process.ExitCode);
    }

    private static async Task<WorkerStartupKind> DrainStandardErrorAsync(StreamReader error)
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
            return WorkerStartupKind.None;
        }

        var safe = retained.ToString();
        if (safe.Contains("msbuild-host:toolset-not-found", StringComparison.Ordinal))
        {
            return WorkerStartupKind.ToolsetNotFound;
        }

        if (safe.Contains("msbuild-host:locator-registration-failed", StringComparison.Ordinal))
        {
            return WorkerStartupKind.LocatorRegistrationFailed;
        }

        if (safe.Contains("msbuild-host:toolset-load-failed", StringComparison.Ordinal))
        {
            return WorkerStartupKind.ToolsetLoadFailed;
        }

        return WorkerStartupKind.None;
    }

    private static int ExitCode(Process process) => process.HasExited ? process.ExitCode : -1;

    private static WorkspaceOutcome<T> StartupFailure<T>(WorkerAttempt.StartupFailed startup) =>
        startup.Kind switch
        {
            WorkerStartupKind.HostStartFailed => CoreOutcomes.ExternalToolFailed<T>(
                "msbuild-host",
                startup.ExitCode,
                MsBuildDiagnosticCodes.SdkStartFailed,
                "The MSBuild evaluator process could not be started."
            ),
            WorkerStartupKind.ToolsetNotFound => CoreOutcomes.ExternalToolFailed<T>(
                "msbuild-host",
                startup.ExitCode,
                MsBuildDiagnosticCodes.ToolsetIncompatible,
                "The selected SDK toolset directory is unavailable."
            ),
            WorkerStartupKind.LocatorRegistrationFailed
            or WorkerStartupKind.ToolsetLoadFailed
            or WorkerStartupKind.ProtocolRejected => CoreOutcomes.ExternalToolFailed<T>(
                "msbuild-host",
                startup.ExitCode,
                MsBuildDiagnosticCodes.ToolsetIncompatible,
                "The selected SDK toolset could not initialize the MSBuild evaluator."
            ),
            _ => CoreOutcomes.ExternalToolFailed<T>(
                "msbuild-host",
                startup.ExitCode,
                MsBuildDiagnosticCodes.WorkerCrashed,
                "The MSBuild evaluator stopped during startup."
            ),
        };

    internal sealed class WorkerProcessState(Process process, Task<WorkerStartupKind> stderrDrain)
    {
        internal Process Process { get; } = process;
        internal Task<WorkerStartupKind> StderrDrain { get; } = stderrDrain;
        internal int FrameLimit { get; set; } = RpcCodec.secureLimits.MaximumValueBytes;
        internal bool Initialized { get; set; }
    }

    internal abstract record WorkerAttempt
    {
        private WorkerAttempt() { }

        internal sealed record Started(WorkerProcessState Worker) : WorkerAttempt;

        internal sealed record Received(RpcValue Result) : WorkerAttempt;

        internal sealed record RpcFailed(RpcError Error) : WorkerAttempt;

        internal sealed record StartupFailed(WorkerStartupKind Kind, int ExitCode) : WorkerAttempt;

        internal sealed record TransportFailed : WorkerAttempt
        {
            internal TransportFailed() { }
        }

        internal sealed record Cancelled : WorkerAttempt
        {
            internal Cancelled() { }
        }
    }

    internal enum WorkerStartupKind
    {
        None,
        HostStartFailed,
        ToolsetNotFound,
        LocatorRegistrationFailed,
        ToolsetLoadFailed,
        ProtocolRejected,
    }
}
