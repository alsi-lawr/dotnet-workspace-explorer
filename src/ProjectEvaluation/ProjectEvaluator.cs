using System.Collections.Immutable;
using System.Diagnostics;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

public sealed class ProjectEvaluator : IAsyncDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly EvaluationWorkerLaunch launchSettings;
    private readonly Dictionary<string, ProjectEvaluationBinding> bindings = new(PathComparer);
    private readonly Dictionary<string, ProjectEvaluationWorker> workers = new(PathComparer);
    private bool closed;

    public ProjectEvaluator()
        : this(EvaluationWorkerLaunch.ForCurrentProcess()) { }

    internal ProjectEvaluator(EvaluationWorkerLaunch launchSettings)
    {
        this.launchSettings = launchSettings;
    }

    internal Task<WorkspaceOutcome<ProjectEvaluationReadiness>> WarmAsync(
        WorkspaceArtifactPath workspacePath,
        CancellationToken cancellationToken = default
    ) =>
        RunLockedAsync(
            cancellationToken,
            async () =>
            {
                var prepared = await PrepareAsync(workspacePath, cancellationToken);
                if (
                    !ProjectEvaluationOutcomes.TrySuccess(
                        prepared,
                        out var session,
                        out var failure
                    )
                )
                {
                    return ProjectEvaluationOutcomes.Failure<ProjectEvaluationReadiness>(failure!);
                }

                return await session!.Worker.WarmAsync(cancellationToken);
            }
        );

    public Task<WorkspaceOutcome<ProjectEvaluationSnapshot>> EvaluateAsync(
        WorkspaceArtifactPath projectPath,
        WorkspaceArtifactPath workspacePath,
        CancellationToken cancellationToken = default
    ) =>
        RunLockedAsync(
            cancellationToken,
            async () =>
            {
                var prepared = await PrepareAsync(workspacePath, cancellationToken);
                if (
                    !ProjectEvaluationOutcomes.TrySuccess(
                        prepared,
                        out var session,
                        out var failure
                    )
                )
                {
                    return ProjectEvaluationOutcomes.Failure<ProjectEvaluationSnapshot>(failure!);
                }

                var outcome = await session!.Worker.EvaluateAsync(
                    WorkspaceArtifactPath.Create(projectPath.Value),
                    cancellationToken
                );

                if (!ProjectEvaluationOutcomes.TrySuccess(outcome, out var snapshot, out _))
                {
                    return outcome;
                }

                var watchInputs = session.Binding.SdkSelection.GlobalJsonPath is { } globalJson
                    ? snapshot!
                        .WatchInputs.Append(globalJson)
                        .DistinctBy(path => path.Value, PathComparer)
                        .OrderBy(path => path.Value, PathComparer)
                        .ToImmutableArray()
                    : snapshot!.WatchInputs;
                return ProjectEvaluationOutcomes.Success(
                    snapshot with
                    {
                        WatchInputs = watchInputs,
                    }
                );
            }
        );

    public Task<WorkspaceOutcome<ProjectEvaluationInvalidationKind>> InvalidateAsync(
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
                    return ProjectEvaluationOutcomes.Success(
                        ProjectEvaluationInvalidationKind.None
                    );
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
                        var sdkKey in affectedBindings
                            .Select(binding => binding.SdkSelection.SdkPath.Value)
                            .Distinct(PathComparer)
                    )
                    {
                        if (workers.Remove(sdkKey, out var worker))
                        {
                            await worker.DisposeAsync();
                        }
                    }

                    return ProjectEvaluationOutcomes.Success(
                        ProjectEvaluationInvalidationKind.DotnetSdkSelection
                    );
                }

                var invalidated = false;
                foreach (var worker in workers.Values)
                {
                    var outcome = await worker.InvalidateAsync(changed, cancellationToken);
                    if (
                        !ProjectEvaluationOutcomes.TrySuccess(
                            outcome,
                            out var result,
                            out var failure
                        )
                    )
                    {
                        return ProjectEvaluationOutcomes.Failure<ProjectEvaluationInvalidationKind>(
                            failure!
                        );
                    }

                    invalidated |= !result!.InvalidatedProjects.IsDefaultOrEmpty;
                }

                return ProjectEvaluationOutcomes.Success(
                    invalidated
                        ? ProjectEvaluationInvalidationKind.ProjectOrImport
                        : ProjectEvaluationInvalidationKind.None
                );
            }
        );

    internal Task<WorkspaceOutcome<ProjectExportEvaluator>> OpenExportSessionAsync(
        WorkspaceArtifactPath workspacePath,
        int capacity,
        CancellationToken cancellationToken
    ) =>
        RunLockedAsync(
            cancellationToken,
            async () =>
            {
                var canonicalWorkspace = WorkspaceArtifactPath.Create(workspacePath.Value);
                var discovery = await DotnetSdkResolver.DiscoverAsync(
                    canonicalWorkspace,
                    launchSettings.DotnetExecutable,
                    cancellationToken
                );
                if (
                    !ProjectEvaluationOutcomes.TrySuccess(
                        discovery,
                        out var selection,
                        out var failure
                    )
                )
                {
                    return ProjectEvaluationOutcomes.Failure<ProjectExportEvaluator>(failure!);
                }

                return ProjectEvaluationOutcomes.Success(
                    new ProjectExportEvaluator(launchSettings, selection!, capacity)
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
            return ProjectEvaluationOutcomes.Cancelled<T>("The MSBuild operation was cancelled.");
        }

        try
        {
            return closed ? ProjectEvaluationOutcomes.WorkerClosed<T>() : await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorkspaceOutcome<ProjectEvaluationSession>> PrepareAsync(
        WorkspaceArtifactPath workspacePath,
        CancellationToken cancellationToken
    )
    {
        var canonicalWorkspace = WorkspaceArtifactPath.Create(workspacePath.Value);
        if (!bindings.TryGetValue(canonicalWorkspace.Value, out var binding))
        {
            var discovery = await DotnetSdkResolver.DiscoverAsync(
                canonicalWorkspace,
                launchSettings.DotnetExecutable,
                cancellationToken
            );
            if (
                !ProjectEvaluationOutcomes.TrySuccess(discovery, out var selection, out var failure)
            )
            {
                return ProjectEvaluationOutcomes.Failure<ProjectEvaluationSession>(failure!);
            }

            binding = new ProjectEvaluationBinding(canonicalWorkspace, selection!);
            bindings.Add(canonicalWorkspace.Value, binding);
        }

        var sdkKey = binding.SdkSelection.SdkPath.Value;
        if (!workers.TryGetValue(sdkKey, out var worker))
        {
            worker = new ProjectEvaluationWorker(launchSettings, binding.SdkSelection);
            workers.Add(sdkKey, worker);
        }

        return ProjectEvaluationOutcomes.Success(new ProjectEvaluationSession(binding, worker));
    }

    private static bool IsApplicableGlobalJsonChange(
        ProjectEvaluationBinding binding,
        string changedPath
    )
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(changedPath), "global.json"))
        {
            return false;
        }

        if (
            binding.SdkSelection.GlobalJsonPath is { } current
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

        return binding.SdkSelection.GlobalJsonPath is null
            || IsSameOrDescendant(
                Path.GetDirectoryName(binding.SdkSelection.GlobalJsonPath.Value)!,
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

    private sealed record ProjectEvaluationSession(
        ProjectEvaluationBinding Binding,
        ProjectEvaluationWorker Worker
    );
}
