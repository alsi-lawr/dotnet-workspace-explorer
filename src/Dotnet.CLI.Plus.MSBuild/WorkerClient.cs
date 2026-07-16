using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Dotnet.CLI.Plus.Core;
using Dotnet.CLI.Plus.Transport;
using Microsoft.FSharp.Core;

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
            await GracefulStopAsync();
        }
        finally
        {
            gate.Release();
            gate.Dispose();
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
            if (disabled)
            {
                return CoreOutcomes.ExternalToolFailed<T>(
                    "msbuild-host",
                    -1,
                    MsBuildDiagnosticCodes.WorkerDisabled,
                    "The MSBuild evaluator is disabled until refresh."
                );
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var worker = await EnsureStartedAsync(cancellationToken);
                    var response = await SendAsync(worker, method, parameters, cancellationToken);
                    if (response.Error is { } rpcError)
                    {
                        return CoreOutcomes.FromRpcError<T>(rpcError.Value, projectPath);
                    }

                    try
                    {
                        return CoreOutcomes.Success(decode(response.Result));
                    }
                    catch (ArgumentException error)
                    {
                        throw new WorkerTransportException(
                            "The MSBuild worker response was malformed.",
                            error
                        );
                    }
                    catch (FormatException error)
                    {
                        throw new WorkerTransportException(
                            "The MSBuild worker response was malformed.",
                            error
                        );
                    }
                    catch (OverflowException error)
                    {
                        throw new WorkerTransportException(
                            "The MSBuild worker response was malformed.",
                            error
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    await KillActiveAsync();
                    return CoreOutcomes.Cancelled<T>("The MSBuild operation was cancelled.");
                }
                catch (WorkerStartupException startup)
                {
                    await KillActiveAsync();
                    return StartupFailure<T>(startup);
                }
                catch (WorkerTransportException) when (attempt == 0)
                {
                    await KillActiveAsync();
                }
                catch (WorkerTransportException)
                {
                    disabled = true;
                    await KillActiveAsync();
                    return CoreOutcomes.ExternalToolFailed<T>(
                        "msbuild-host",
                        -1,
                        MsBuildDiagnosticCodes.WorkerCrashed,
                        "The MSBuild evaluator stopped unexpectedly after one restart.",
                        true
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

    private async Task<WorkerProcessState> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (active is { Process.HasExited: false, Initialized: true } running)
        {
            return running;
        }

        if (active is not null)
        {
            await KillActiveAsync();
        }

        Process process;
        try
        {
            process =
                Process.Start(launchSettings.CreateStartInfo(selection))
                ?? throw new WorkerStartupException(WorkerStartupKind.HostStartFailed, -1);
        }
        catch (Win32Exception)
        {
            throw new WorkerStartupException(WorkerStartupKind.HostStartFailed, -1);
        }

        var state = new WorkerProcessState(process, DrainStandardErrorAsync(process.StandardError));
        active = state;
        launchSettings.ProcessStarted?.Invoke(process);

        try
        {
            var response = await SendAsync(
                state,
                "initialize",
                WorkerProtocol.InitializeRequest(RpcCodec.secureLimits.MaximumValueBytes),
                cancellationToken,
                RpcCodec.secureLimits.MaximumValueBytes
            );
            if (response.Error is { } error)
            {
                throw new WorkerStartupException(
                    WorkerStartupKind.ProtocolRejected,
                    (process.HasExited ? process.ExitCode : -1)
                );
            }

            state.FrameLimit = WorkerProtocol.DecodeInitializeResult(response.Result);
            state.Initialized = true;
            return state;
        }
        catch (WorkerTransportException)
        {
            var startup = await StartupFailureAsync(state);
            if (startup is not null)
            {
                throw startup;
            }

            throw;
        }
        catch (ArgumentException error)
        {
            throw new WorkerTransportException(
                "The MSBuild worker initialization was malformed.",
                error
            );
        }
    }

    private async Task<Response> SendAsync(
        WorkerProcessState worker,
        string method,
        RpcValue parameters,
        CancellationToken cancellationToken,
        int? frameLimit = null
    )
    {
        var id = nextRequestId++;
        var bytes = RpcCodec.encodeFrame(RpcFrame.NewRequest(id, method, parameters));
        var limit = frameLimit ?? worker.FrameLimit;
        if (bytes.Length > limit)
        {
            throw new WorkerTransportException(
                "The MSBuild worker request exceeded its negotiated frame limit."
            );
        }

        try
        {
            await worker.Process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            await worker.Process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            return await ReadResponseAsync(worker, id, limit, cancellationToken);
        }
        catch (IOException error)
        {
            throw new WorkerTransportException("The MSBuild worker transport failed.", error);
        }
        catch (InvalidOperationException error)
        {
            throw new WorkerTransportException("The MSBuild worker transport failed.", error);
        }
    }

    private static async Task<Response> ReadResponseAsync(
        WorkerProcessState worker,
        uint expectedId,
        int frameLimit,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[4096];
        while (true)
        {
            var bytes = worker.Pending.ToArray();
            var length = RpcCodec.tryReadValueLength(RpcCodec.secureLimits, bytes);
            if (!length.IsError)
            {
                if (length.ResultValue > frameLimit)
                {
                    throw new WorkerTransportException(
                        "The MSBuild worker exceeded its negotiated frame limit."
                    );
                }

                var frameBytes = bytes[..length.ResultValue];
                worker.Pending.RemoveRange(0, length.ResultValue);
                var decoded = RpcCodec.decodeFrame(RpcCodec.secureLimits, frameBytes);
                if (decoded.IsError || decoded.ResultValue is not RpcFrameDecodeResult.Frame frame)
                {
                    throw new WorkerTransportException(
                        "The MSBuild worker wrote an invalid RPC frame."
                    );
                }

                if (frame.Item is RpcFrame.Response response && response.messageId == expectedId)
                {
                    return new Response(response.error, response.result);
                }

                throw new WorkerTransportException(
                    "The MSBuild worker wrote an unexpected RPC frame."
                );
            }

            if (length.ErrorValue != RpcDecodeError.Incomplete)
            {
                throw new WorkerTransportException(
                    "The MSBuild worker wrote an invalid RPC frame."
                );
            }

            var count = await worker.Process.StandardOutput.BaseStream.ReadAsync(
                buffer,
                cancellationToken
            );
            if (count == 0)
            {
                throw new WorkerTransportException("The MSBuild worker closed its output.");
            }

            worker.Pending.AddRange(buffer.AsSpan(0, count).ToArray());
        }
    }

    private async Task GracefulStopAsync()
    {
        var worker = active;
        if (worker is null)
        {
            return;
        }

        try
        {
            if (worker.Initialized && !worker.Process.HasExited)
            {
                using var timeout = new CancellationTokenSource(ProcessExitTimeout);
                var response = await SendAsync(
                    worker,
                    "shutdown",
                    RpcValueModule.emptyMap,
                    timeout.Token
                );
                if (response.Error is not null)
                {
                    throw new WorkerTransportException("The MSBuild worker rejected shutdown.");
                }

                WorkerProtocol.ValidateShutdownResult(response.Result);
                await worker.Process.WaitForExitAsync(timeout.Token);
                if (worker.Process.ExitCode != 0)
                {
                    throw new WorkerTransportException("The MSBuild worker exited unsuccessfully.");
                }
            }
            else if (!worker.Process.HasExited)
            {
                await KillAndReapAsync(worker.Process);
            }
        }
        catch (OperationCanceledException)
        {
            await KillAndReapAsync(worker.Process);
        }
        catch (WorkerTransportException)
        {
            await KillAndReapAsync(worker.Process);
        }
        catch (ArgumentException)
        {
            await KillAndReapAsync(worker.Process);
        }
        finally
        {
            active = null;
            await worker.StderrDrain;
            worker.Process.Dispose();
        }
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
        catch (InvalidOperationException) when (process.HasExited) { }

        await process.WaitForExitAsync();
    }

    private static async Task<WorkerStartupException?> StartupFailureAsync(
        WorkerProcessState worker
    )
    {
        if (!worker.Process.HasExited)
        {
            try
            {
                await worker.Process.WaitForExitAsync().WaitAsync(ProcessExitTimeout);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        var diagnostic = await worker.StderrDrain;
        return diagnostic switch
        {
            WorkerStartupKind.None => null,
            _ => new WorkerStartupException(diagnostic, worker.Process.ExitCode),
        };
    }

    private static async Task<WorkerStartupKind> DrainStandardErrorAsync(StreamReader error)
    {
        var retained = new StringBuilder(512);
        var buffer = new char[1024];
        while (true)
        {
            var count = await error.ReadAsync(buffer);
            if (count == 0)
            {
                break;
            }

            if (retained.Length < retained.Capacity)
            {
                retained.Append(buffer, 0, Math.Min(count, retained.Capacity - retained.Length));
            }
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

    private static WorkspaceOutcome<T> StartupFailure<T>(WorkerStartupException startup) =>
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

    private sealed class WorkerProcessState(Process process, Task<WorkerStartupKind> stderrDrain)
    {
        internal Process Process { get; } = process;
        internal Task<WorkerStartupKind> StderrDrain { get; } = stderrDrain;
        internal List<byte> Pending { get; } = [];
        internal int FrameLimit { get; set; } = RpcCodec.secureLimits.MaximumValueBytes;
        internal bool Initialized { get; set; }
    }

    private sealed record Response(FSharpOption<RpcError>? Error, RpcValue Result);

    private sealed class WorkerTransportException : IOException
    {
        internal WorkerTransportException(string message)
            : base(message) { }

        internal WorkerTransportException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    private sealed class WorkerStartupException(WorkerStartupKind kind, int exitCode) : Exception
    {
        internal WorkerStartupKind Kind { get; } = kind;
        internal int ExitCode { get; } = exitCode;
    }

    private enum WorkerStartupKind
    {
        None,
        HostStartFailed,
        ToolsetNotFound,
        LocatorRegistrationFailed,
        ToolsetLoadFailed,
        ProtocolRejected,
    }
}
