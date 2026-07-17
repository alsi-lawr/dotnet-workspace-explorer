using System.Collections.Immutable;
using System.Diagnostics;
using Dotnet.CLI.Plus.Core;

namespace Dotnet.CLI.Plus.MSBuild;

public sealed class MsBuildEvaluationClient : IAsyncDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly WorkerLaunchSettings launchSettings;
    private readonly Dictionary<string, WorkspaceBinding> bindings = new(PathComparer);
    private readonly Dictionary<string, WorkerClient> workers = new(PathComparer);
    private bool closed;

    public MsBuildEvaluationClient()
        : this(WorkerLaunchSettings.ForCurrentProcess()) { }

    internal MsBuildEvaluationClient(WorkerLaunchSettings launchSettings)
    {
        this.launchSettings = launchSettings;
    }

    public Task<WorkspaceOutcome<EvaluationSnapshot>> EvaluateAsync(
        WorkspaceArtifactPath projectPath,
        WorkspaceArtifactPath workspacePath,
        CancellationToken cancellationToken = default
    ) =>
        RunLockedAsync(
            cancellationToken,
            async () =>
            {
                var canonicalWorkspace = WorkspaceArtifactPath.Create(workspacePath.Value);
                if (!bindings.TryGetValue(canonicalWorkspace.Value, out var binding))
                {
                    var discovery = await DotnetSdkDiscovery.DiscoverAsync(
                        canonicalWorkspace,
                        launchSettings.DotnetExecutable,
                        cancellationToken
                    );
                    if (!CoreOutcomes.TrySuccess(discovery, out var selection, out var failure))
                    {
                        return CoreOutcomes.Failure<EvaluationSnapshot>(failure!);
                    }

                    binding = new WorkspaceBinding(canonicalWorkspace, selection!);
                    bindings.Add(canonicalWorkspace.Value, binding);
                }

                var toolsetKey = binding.Toolset.ToolsetPath.Value;
                if (!workers.TryGetValue(toolsetKey, out var worker))
                {
                    worker = new WorkerClient(launchSettings, binding.Toolset);
                    workers.Add(toolsetKey, worker);
                }

                var outcome = await worker.EvaluateAsync(
                    WorkspaceArtifactPath.Create(projectPath.Value),
                    cancellationToken
                );

                if (!CoreOutcomes.TrySuccess(outcome, out var snapshot, out _))
                {
                    return outcome;
                }

                var watchInputs = binding.Toolset.GlobalJsonPath is { } globalJson
                    ? snapshot!
                        .WatchInputs.Append(globalJson)
                        .DistinctBy(path => path.Value, PathComparer)
                        .OrderBy(path => path.Value, PathComparer)
                        .ToImmutableArray()
                    : snapshot!.WatchInputs;
                return CoreOutcomes.Success(snapshot with { WatchInputs = watchInputs });
            }
        );

    public Task<WorkspaceOutcome<MsBuildInvalidationKind>> InvalidateAsync(
        IEnumerable<WorkspaceArtifactPath> paths,
        CancellationToken cancellationToken = default
    ) =>
        RunLockedAsync(
            cancellationToken,
            async () =>
            {
                var changed = paths
                    .Select(path => WorkspaceArtifactPath.Create(path.Value))
                    .DistinctBy(path => path.Value, PathComparer)
                    .ToImmutableArray();
                if (changed.IsDefaultOrEmpty)
                {
                    return CoreOutcomes.Success(MsBuildInvalidationKind.None);
                }

                var affectedBindings = bindings
                    .Values.Where(binding =>
                        changed.Any(path => IsApplicableGlobalJsonChange(binding, path.Value))
                    )
                    .ToArray();
                if (affectedBindings.Length > 0)
                {
                    foreach (var binding in affectedBindings)
                    {
                        bindings.Remove(binding.WorkspacePath.Value);
                    }

                    foreach (
                        var toolsetKey in affectedBindings
                            .Select(binding => binding.Toolset.ToolsetPath.Value)
                            .Distinct(PathComparer)
                    )
                    {
                        if (workers.Remove(toolsetKey, out var worker))
                        {
                            await worker.DisposeAsync();
                        }
                    }

                    return CoreOutcomes.Success(MsBuildInvalidationKind.ToolsetSelection);
                }

                var invalidated = false;
                foreach (var worker in workers.Values)
                {
                    var outcome = await worker.InvalidateAsync(changed, cancellationToken);
                    if (!CoreOutcomes.TrySuccess(outcome, out var result, out var failure))
                    {
                        return CoreOutcomes.Failure<MsBuildInvalidationKind>(failure!);
                    }

                    invalidated |= !result!.InvalidatedProjects.IsDefaultOrEmpty;
                }

                return CoreOutcomes.Success(
                    invalidated
                        ? MsBuildInvalidationKind.ProjectOrImport
                        : MsBuildInvalidationKind.None
                );
            }
        );

    public Task RefreshAsync() => ResetAsync(false);

    public ValueTask DisposeAsync() => new(ResetAsync(true));

    private async Task ResetAsync(bool close)
    {
        await gate.WaitAsync();
        try
        {
            if (closed)
            {
                return;
            }

            closed = close;
            bindings.Clear();
            var activeWorkers = workers.Values.ToArray();
            workers.Clear();
            foreach (var worker in activeWorkers)
            {
                await worker.DisposeAsync();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorkspaceOutcome<T>> RunLockedAsync<T>(
        CancellationToken cancellationToken,
        Func<Task<WorkspaceOutcome<T>>> operation
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
            return closed ? CoreOutcomes.WorkerClosed<T>() : await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsApplicableGlobalJsonChange(WorkspaceBinding binding, string changedPath)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(changedPath), "global.json"))
        {
            return false;
        }

        if (
            binding.Toolset.GlobalJsonPath is { } current
            && PathComparer.Equals(current.Value, changedPath)
        )
        {
            return true;
        }

        var workspaceDirectory = Directory.Exists(binding.WorkspacePath.Value)
            ? binding.WorkspacePath.Value
            : Path.GetDirectoryName(binding.WorkspacePath.Value)!;
        var changedDirectory = Path.GetDirectoryName(changedPath)!;
        if (!IsSameOrDescendant(changedDirectory, workspaceDirectory))
        {
            return false;
        }

        return binding.Toolset.GlobalJsonPath is null
            || IsSameOrDescendant(
                Path.GetDirectoryName(binding.Toolset.GlobalJsonPath.Value)!,
                changedDirectory
            );
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "."
            || (
                !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal
                )
                && !relative.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal
                )
            );
    }
}

internal sealed record WorkerLaunchSettings(
    string HostExecutable,
    string? HostAssembly,
    string DotnetExecutable
)
{
    internal static WorkerLaunchSettings ForCurrentProcess() =>
        new(
            Environment.ProcessPath
                ?? throw new InvalidOperationException("The host executable path is unavailable."),
            null,
            "dotnet"
        );

    internal ProcessStartInfo CreateStartInfo(ToolsetSelection selection)
    {
        var start = new ProcessStartInfo(HostAssembly is null ? HostExecutable : DotnetExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (HostAssembly is not null)
        {
            start.ArgumentList.Add(HostAssembly);
        }

        start.ArgumentList.Add("internal");
        start.ArgumentList.Add("msbuild-host");
        start.ArgumentList.Add("--toolset");
        start.ArgumentList.Add(selection.ToolsetPath.Value);
        return start;
    }
}
