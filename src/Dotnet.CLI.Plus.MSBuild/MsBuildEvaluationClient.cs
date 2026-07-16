using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Dotnet.CLI.Plus.Transport;

namespace Dotnet.CLI.Plus.MSBuild;

public sealed class MsBuildEvaluationClient : IAsyncDisposable
{
    private readonly string hostExecutable;
    private readonly string? hostAssembly;
    private readonly ConcurrentDictionary<string, WorkerClient> workers = new(
        StringComparer.Ordinal
    );

    public MsBuildEvaluationClient(string? hostExecutable = null, string? hostAssembly = null)
    {
        this.hostExecutable =
            hostExecutable
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The host executable path is unavailable.");
        this.hostAssembly = hostAssembly;
    }

    public async Task<EvaluationOutcome> EvaluateAsync(
        string projectPath,
        string workspacePath,
        CancellationToken cancellationToken = default
    )
    {
        var selected = await DotnetSdkDiscovery.DiscoverAsync(workspacePath, cancellationToken);
        if (selected is ToolsetDiscoveryOutcome.Failure failure)
        {
            return new EvaluationOutcome.Failure(
                failure.Code,
                failure.Message,
                failure.IsCancelled
            );
        }

        var selection = ((ToolsetDiscoveryOutcome.Success)selected).Selection;
        var worker = workers.GetOrAdd(
            selection.Key,
            _ => new WorkerClient(hostExecutable, hostAssembly, selection)
        );
        return await worker.EvaluateAsync(projectPath, cancellationToken);
    }

    public async Task<MsBuildInvalidationKind> InvalidateAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default
    )
    {
        var changed = paths.Select(Path.GetFullPath).ToArray();
        var restarted = false;
        foreach (var pair in workers.ToArray())
        {
            if (
                pair.Value.Selection.GlobalJsonPath is { } globalJson
                && changed.Contains(globalJson, StringComparer.Ordinal)
            )
            {
                restarted = true;
                if (workers.TryRemove(pair.Key, out var removed))
                {
                    await removed.DisposeAsync();
                }
            }
            else
            {
                await pair.Value.InvalidateAsync(changed, cancellationToken);
            }
        }

        return restarted
            ? MsBuildInvalidationKind.ToolsetSelection
            : MsBuildInvalidationKind.ProjectOrImport;
    }

    public async Task RefreshAsync()
    {
        var active = workers.ToArray();
        workers.Clear();
        foreach (var pair in active)
        {
            await pair.Value.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await RefreshAsync();
}

public abstract record ToolsetDiscoveryOutcome
{
    private ToolsetDiscoveryOutcome() { }

    public sealed record Success(ToolsetSelection Selection) : ToolsetDiscoveryOutcome;

    public sealed record Failure(string Code, string Message, bool IsCancelled = false)
        : ToolsetDiscoveryOutcome;
}

public static class DotnetSdkDiscovery
{
    public static async Task<ToolsetDiscoveryOutcome> DiscoverAsync(
        string workspacePath,
        CancellationToken cancellationToken = default
    )
    {
        var workingDirectory = Directory.Exists(workspacePath)
            ? Path.GetFullPath(workspacePath)
            : Path.GetDirectoryName(Path.GetFullPath(workspacePath))
                ?? Directory.GetCurrentDirectory();
        var version = await RunDotnetAsync("--version", workingDirectory, cancellationToken);
        if (version is not CommandOutcome.Success selectedVersion)
        {
            return ToFailure(version);
        }

        var installed = await RunDotnetAsync("--list-sdks", workingDirectory, cancellationToken);
        if (installed is not CommandOutcome.Success installedSdks)
        {
            return ToFailure(installed);
        }

        var sdkVersion = selectedVersion.Output.Trim();
        var toolsetPath = FindSdkPath(installedSdks.Output, sdkVersion);
        return toolsetPath is null
            ? new ToolsetDiscoveryOutcome.Failure(
                "msbuild.sdk_not_found",
                "The selected workspace SDK could not be located."
            )
            : new ToolsetDiscoveryOutcome.Success(
                new ToolsetSelection(sdkVersion, toolsetPath, FindGlobalJson(workingDirectory))
            );
    }

    private static ToolsetDiscoveryOutcome.Failure ToFailure(CommandOutcome outcome) =>
        outcome switch
        {
            CommandOutcome.Failure failure => new ToolsetDiscoveryOutcome.Failure(
                failure.Code,
                failure.Message,
                failure.IsCancelled
            ),
            _ => new ToolsetDiscoveryOutcome.Failure(
                "msbuild.sdk_selection_failed",
                "The workspace SDK could not be selected."
            ),
        };

    private static async Task<CommandOutcome> RunDotnetAsync(
        string argument,
        string workingDirectory,
        CancellationToken cancellationToken
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
            {
                return new CommandOutcome.Failure(
                    "msbuild.sdk_start_failed",
                    "The dotnet SDK command could not be started."
                );
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);
            return process.ExitCode == 0
                ? new CommandOutcome.Success(output.Result)
                : new CommandOutcome.Failure(
                    "msbuild.sdk_selection_failed",
                    "The workspace SDK could not be selected."
                );
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            return new CommandOutcome.Failure(
                "msbuild.cancelled",
                "The SDK selection was cancelled.",
                true
            );
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new CommandOutcome.Failure(
                "msbuild.sdk_start_failed",
                "The dotnet SDK command could not be started."
            );
        }
    }

    private static string? FindSdkPath(string listing, string version) =>
        listing
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(" [", StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && StringComparer.Ordinal.Equals(parts[0], version))
            .Select(parts => Path.Combine(parts[1].TrimEnd(']'), version))
            .FirstOrDefault(Directory.Exists);

    private static string? FindGlobalJson(string directory)
    {
        for (
            var candidate = new DirectoryInfo(directory);
            candidate is not null;
            candidate = candidate.Parent
        )
        {
            var path = Path.Combine(candidate.FullName, "global.json");
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static void Terminate(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(true);
            process.WaitForExit();
        }
    }

    private abstract record CommandOutcome
    {
        public sealed record Success(string Output) : CommandOutcome;

        public sealed record Failure(string Code, string Message, bool IsCancelled = false)
            : CommandOutcome;
    }
}

internal sealed class WorkerClient : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string hostExecutable;
    private readonly string? hostAssembly;
    private Process? process;
    private uint nextRequestId = 1;
    private bool initialized;
    private bool disabled;

    internal WorkerClient(string hostExecutable, string? hostAssembly, ToolsetSelection selection)
    {
        this.hostExecutable = hostExecutable;
        this.hostAssembly = hostAssembly;
        Selection = selection;
    }

    internal ToolsetSelection Selection { get; }

    internal async Task<EvaluationOutcome> EvaluateAsync(
        string projectPath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new EvaluationOutcome.Failure(
                "msbuild.cancelled",
                "The MSBuild evaluation was cancelled.",
                true
            );
        }

        Exception? lastTransportFailure = null;
        try
        {
            if (disabled)
            {
                return new EvaluationOutcome.Failure(
                    "msbuild.worker_disabled",
                    "The MSBuild evaluator is disabled until refresh."
                );
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var response = await RequestAsync(
                        "msbuild/evaluate",
                        WorkerProtocol.Map(
                            ("projectPath", RpcValue.NewString(Path.GetFullPath(projectPath)))
                        ),
                        cancellationToken
                    );
                    return response.Error is null
                        ? new EvaluationOutcome.Success(SnapshotCodec.Decode(response.Result))
                        : new EvaluationOutcome.Failure(
                            response.Error.Value.Code,
                            response.Error.Value.Message
                        );
                }
                catch (OperationCanceledException)
                {
                    await StopAsync();
                    return new EvaluationOutcome.Failure(
                        "msbuild.cancelled",
                        "The MSBuild evaluation was cancelled.",
                        true
                    );
                }
                catch (Exception error) when (attempt == 0)
                {
                    lastTransportFailure = error;
                    await StopAsync();
                }
                catch (Exception)
                {
                    disabled = true;
                    await StopAsync();
                    return new EvaluationOutcome.Failure(
                        "msbuild.worker_crashed",
                        "The MSBuild evaluator stopped unexpectedly."
                    );
                }
            }

            throw new InvalidOperationException("The worker retry loop did not return.");
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task InvalidateAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken
    )
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!disabled && process is not null)
            {
                _ = await RequestAsync(
                    "msbuild/invalidate",
                    WorkerProtocol.Map(("paths", WorkerProtocol.Array(paths, RpcValue.NewString))),
                    cancellationToken
                );
            }
        }
        catch (OperationCanceledException)
        {
            await StopAsync();
            throw;
        }
        catch (Exception)
        {
            await StopAsync();
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
            await StopAsync();
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task<Response> RequestAsync(
        string method,
        RpcValue parameters,
        CancellationToken cancellationToken
    )
    {
        await EnsureStartedAsync(cancellationToken);
        return await SendRequestAsync(method, parameters, cancellationToken);
    }

    private async Task<Response> SendRequestAsync(
        string method,
        RpcValue parameters,
        CancellationToken cancellationToken
    )
    {
        var current =
            process ?? throw new InvalidOperationException("The MSBuild worker is unavailable.");
        var id = nextRequestId++;
        var bytes = RpcCodec.encodeFrame(RpcFrame.NewRequest(id, method, parameters));
        await current.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
        await current.StandardInput.BaseStream.FlushAsync(cancellationToken);
        return await ReadResponseAsync(current.StandardOutput.BaseStream, id, cancellationToken);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (process is { HasExited: false } && initialized)
        {
            return;
        }

        await StopAsync();
        var start = new ProcessStartInfo(hostAssembly is null ? hostExecutable : "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (hostAssembly is not null)
        {
            start.ArgumentList.Add(hostAssembly);
        }

        start.ArgumentList.Add("internal");
        start.ArgumentList.Add("msbuild-host");
        start.ArgumentList.Add("--toolset");
        start.ArgumentList.Add(Selection.ToolsetPath);
        process =
            Process.Start(start)
            ?? throw new InvalidOperationException("The MSBuild worker could not be started.");
        initialized = false;
        var response = await SendRequestAsync(
            "initialize",
            WorkerProtocol.Map(
                ("profile", RpcValue.NewString(WorkerProtocol.ProfileName)),
                (
                    "protocolVersion",
                    WorkerProtocol.Map(
                        ("major", RpcValue.NewInteger(WorkerProtocol.ProtocolMajor)),
                        ("minor", RpcValue.NewInteger(WorkerProtocol.ProtocolMinor))
                    )
                ),
                (
                    "limits",
                    WorkerProtocol.Map(
                        (
                            "maxFrameBytes",
                            RpcValue.NewInteger(RpcCodec.secureLimits.MaximumValueBytes)
                        )
                    )
                )
            ),
            cancellationToken
        );
        if (response.Error is not null)
        {
            throw new InvalidOperationException("The MSBuild worker rejected initialization.");
        }

        initialized = true;
    }

    private static async Task<Response> ReadResponseAsync(
        Stream stream,
        uint expectedId,
        CancellationToken cancellationToken
    )
    {
        var pending = new List<byte>();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("The MSBuild worker closed its output.");
            }

            pending.AddRange(buffer.AsSpan(0, count).ToArray());
            while (true)
            {
                var bytes = pending.ToArray();
                var length = RpcCodec.tryReadValueLength(RpcCodec.secureLimits, bytes);
                if (length.IsError && length.ErrorValue == RpcDecodeError.Incomplete)
                {
                    break;
                }

                if (length.IsError)
                {
                    throw new InvalidDataException(
                        "The MSBuild worker wrote an invalid RPC frame."
                    );
                }

                var frameBytes = bytes[..length.ResultValue];
                pending.RemoveRange(0, length.ResultValue);
                var decoded = RpcCodec.decodeFrame(RpcCodec.secureLimits, frameBytes);
                if (decoded.IsError || decoded.ResultValue is not RpcFrameDecodeResult.Frame frame)
                {
                    throw new InvalidDataException(
                        "The MSBuild worker wrote an invalid RPC response."
                    );
                }

                if (frame.Item is RpcFrame.Response response && response.messageId == expectedId)
                {
                    return new Response(response.error, response.result);
                }
            }
        }
    }

    private async Task StopAsync()
    {
        var current = Interlocked.Exchange(ref process, null);
        var wasInitialized = initialized;
        initialized = false;
        if (current is null)
        {
            return;
        }

        try
        {
            if (!current.HasExited && wasInitialized)
            {
                try
                {
                    await SendRequestAsync(
                        "shutdown",
                        RpcValueModule.emptyMap,
                        CancellationToken.None
                    );
                    await current.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    if (!current.HasExited)
                    {
                        current.Kill(true);
                        await current.WaitForExitAsync();
                    }
                }
            }
            else if (!current.HasExited)
            {
                current.Kill(true);
                await current.WaitForExitAsync();
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    private sealed record Response(
        Microsoft.FSharp.Core.FSharpOption<RpcError>? Error,
        RpcValue Result
    );
}
