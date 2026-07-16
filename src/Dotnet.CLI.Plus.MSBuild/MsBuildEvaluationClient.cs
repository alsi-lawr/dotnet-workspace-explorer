using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Dotnet.CLI.Plus.Core;

namespace Dotnet.CLI.Plus.MSBuild;

public sealed class MsBuildEvaluationClient : IAsyncDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly WorkerLaunchSettings launchSettings;
    private readonly ConcurrentDictionary<string, WorkspaceBinding> bindings = new(PathComparer);
    private readonly ConcurrentDictionary<string, WorkerClient> workers = new(PathComparer);

    public MsBuildEvaluationClient()
        : this(WorkerLaunchSettings.ForCurrentProcess()) { }

    internal MsBuildEvaluationClient(WorkerLaunchSettings launchSettings)
    {
        this.launchSettings = launchSettings;
    }

    public async Task<WorkspaceOutcome<EvaluationSnapshot>> EvaluateAsync(
        WorkspaceArtifactPath projectPath,
        WorkspaceArtifactPath workspacePath,
        CancellationToken cancellationToken = default
    )
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
            bindings[canonicalWorkspace.Value] = binding;
        }

        var toolsetKey = binding.Toolset.ToolsetPath.Value;
        var worker = workers.GetOrAdd(
            toolsetKey,
            _ => new WorkerClient(launchSettings, binding.Toolset)
        );
        return await worker.EvaluateAsync(
            WorkspaceArtifactPath.Create(projectPath.Value),
            cancellationToken
        );
    }

    public async Task<WorkspaceOutcome<MsBuildInvalidationKind>> InvalidateAsync(
        IEnumerable<WorkspaceArtifactPath> paths,
        CancellationToken cancellationToken = default
    )
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
                bindings.TryRemove(binding.WorkspacePath.Value, out _);
            }

            foreach (
                var toolsetKey in affectedBindings
                    .Select(binding => binding.Toolset.ToolsetPath.Value)
                    .Distinct(PathComparer)
            )
            {
                if (workers.TryRemove(toolsetKey, out var worker))
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
            invalidated ? MsBuildInvalidationKind.ProjectOrImport : MsBuildInvalidationKind.None
        );
    }

    public async Task RefreshAsync()
    {
        bindings.Clear();
        var active = workers.ToArray();
        workers.Clear();
        foreach (var worker in active)
        {
            await worker.Value.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await RefreshAsync();

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
    string DotnetExecutable,
    Action<Process>? ProcessStarted = null
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
