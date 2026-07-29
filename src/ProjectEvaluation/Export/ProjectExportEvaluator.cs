using System.Collections.Immutable;
using System.Diagnostics;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal sealed class ProjectExportEvaluator : IAsyncDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly object sync = new();
    private readonly EvaluationWorkerLaunch launchSettings;
    private readonly DotnetSdkSelection selection;
    private readonly int capacity;
    private readonly SemaphoreSlim admission;
    private readonly List<ProjectExportWorkerLane> lanes = [];
    private bool closed;

    internal ProjectExportEvaluator(
        EvaluationWorkerLaunch launchSettings,
        DotnetSdkSelection selection,
        int capacity
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.launchSettings = launchSettings;
        this.selection = selection;
        this.capacity = capacity;
        admission = new SemaphoreSlim(capacity, capacity);
    }

    internal async Task<WorkspaceOutcome<ProjectEvaluationSnapshot>> EvaluateAsync(
        WorkspaceArtifactPath projectPath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await admission.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ProjectEvaluationOutcomes.Cancelled<ProjectEvaluationSnapshot>(
                "The MSBuild operation was cancelled."
            );
        }

        ProjectExportWorkerLane? lane = null;
        try
        {
            lock (sync)
            {
                if (closed)
                {
                    return ProjectEvaluationOutcomes.WorkerClosed<ProjectEvaluationSnapshot>();
                }

                lane = lanes.FirstOrDefault(candidate => !candidate.Leased);
                if (lane is null)
                {
                    if (lanes.Count >= capacity)
                    {
                        throw new InvalidOperationException(
                            "Export worker admission did not reserve an available lane."
                        );
                    }

                    lane = new ProjectExportWorkerLane(
                        new ProjectEvaluationWorker(launchSettings, selection)
                    );
                    lanes.Add(lane);
                }

                lane.Leased = true;
            }

            var canonicalProject = WorkspaceArtifactPath.Create(projectPath.Value);
            var evaluated = await lane.Worker.EvaluateAsync(canonicalProject, cancellationToken);
            if (!ProjectEvaluationOutcomes.TrySuccess(evaluated, out var snapshot, out _))
            {
                return evaluated;
            }

            var invalidated = await lane.Worker.InvalidateAsync(
                ImmutableArray.Create(canonicalProject),
                cancellationToken
            );
            if (!ProjectEvaluationOutcomes.TrySuccess(invalidated, out _, out var failure))
            {
                return ProjectEvaluationOutcomes.Failure<ProjectEvaluationSnapshot>(failure!);
            }

            var watchInputs = selection.GlobalJsonPath is { } globalJson
                ? snapshot!
                    .WatchInputs.Append(globalJson)
                    .DistinctBy(path => path.Value, PathComparer)
                    .OrderBy(path => path.Value, PathComparer)
                    .ToImmutableArray()
                : snapshot!.WatchInputs;

            return ProjectEvaluationOutcomes.Success(snapshot with { WatchInputs = watchInputs });
        }
        finally
        {
            if (lane is not null)
            {
                lock (sync)
                {
                    lane.Leased = false;
                }
            }

            admission.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ProjectExportWorkerLane[] activeLanes;
        lock (sync)
        {
            if (closed)
            {
                return;
            }

            closed = true;
            activeLanes = lanes.ToArray();
            lanes.Clear();
        }

        await Task.WhenAll(activeLanes.Select(lane => lane.Worker.DisposeAsync().AsTask()));

        admission.Dispose();
    }

    private sealed class ProjectExportWorkerLane(ProjectEvaluationWorker worker)
    {
        internal ProjectEvaluationWorker Worker { get; } = worker;
        internal bool Leased { get; set; }
    }
}
