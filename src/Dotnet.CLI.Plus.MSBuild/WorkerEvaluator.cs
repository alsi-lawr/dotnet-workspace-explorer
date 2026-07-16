using System.Collections.Immutable;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;

namespace Dotnet.CLI.Plus.MSBuild;

internal sealed class WorkerEvaluator : IDisposable
{
    private const int CacheCapacity = 64;
    private readonly ProjectCollection collection = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> recency = new();

    public EvaluationOutcome Evaluate(string requestedProjectPath)
    {
        var projectPath = Path.GetFullPath(requestedProjectPath);
        if (!File.Exists(projectPath))
        {
            return new EvaluationOutcome.Failure(
                "msbuild.project_not_found",
                "The project file was not found."
            );
        }

        if (cache.TryGetValue(projectPath, out var cached))
        {
            Touch(cached);
            return new EvaluationOutcome.Success(cached.Snapshot);
        }

        try
        {
            var outer = Load(projectPath, EvaluationDimension.Outer);
            var targetFrameworks = TargetFrameworks(outer);
            var dimensions =
                targetFrameworks.Length == 0
                    ? ImmutableArray.Create(EvaluationDimension.Outer)
                    : targetFrameworks
                        .Select(static framework => new EvaluationDimension(framework))
                        .ToImmutableArray();
            var projects = dimensions
                .Select(dimension => Load(projectPath, dimension))
                .ToImmutableArray();
            var snapshot = Materialize(projectPath, outer, projects, targetFrameworks);
            Add(projectPath, snapshot, projects.Append(outer).Distinct().ToImmutableArray());
            return new EvaluationOutcome.Success(snapshot);
        }
        catch (InvalidProjectFileException)
        {
            return new EvaluationOutcome.Failure(
                "msbuild.evaluation_failed",
                "MSBuild could not evaluate the project."
            );
        }
        catch (IOException)
        {
            return new EvaluationOutcome.Failure(
                "msbuild.evaluation_failed",
                "MSBuild could not read the project."
            );
        }
        catch (UnauthorizedAccessException)
        {
            return new EvaluationOutcome.Failure(
                "msbuild.evaluation_failed",
                "MSBuild could not read the project."
            );
        }
        catch (ArgumentException)
        {
            return new EvaluationOutcome.Failure(
                "msbuild.evaluation_failed",
                "MSBuild could not evaluate the project."
            );
        }
    }

    public InvalidationResult Invalidate(IEnumerable<string> paths)
    {
        var changed = paths.Select(Path.GetFullPath).ToImmutableHashSet(StringComparer.Ordinal);
        var invalidated = cache
            .Values.Where(entry => entry.WatchInputs.Any(changed.Contains))
            .Select(entry => entry.ProjectPath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToImmutableArray();

        foreach (var projectPath in invalidated)
        {
            Remove(projectPath);
        }

        return new InvalidationResult(invalidated);
    }

    public void Dispose()
    {
        foreach (var entry in cache.Values)
        {
            foreach (var project in entry.Projects)
            {
                collection.UnloadProject(project);
            }
        }

        cache.Clear();
        recency.Clear();
        collection.Dispose();
    }

    private Project Load(string projectPath, EvaluationDimension dimension)
    {
        var properties =
            dimension == EvaluationDimension.Outer
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TargetFramework"] = dimension.TargetFramework,
                };
        return new Project(
            projectPath,
            properties,
            null,
            collection,
            ProjectLoadSettings.IgnoreMissingImports
        );
    }

    private static EvaluationSnapshot Materialize(
        string projectPath,
        Project outer,
        ImmutableArray<Project> projects,
        ImmutableArray<string> targetFrameworks
    )
    {
        var allProperties = projects
            .SelectMany(project =>
                project.AllEvaluatedProperties.Select(property => new EvaluatedProperty(
                    property.Name,
                    property.EvaluatedValue
                ))
            )
            .DistinctBy(static property => (property.Name, property.Value))
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ThenBy(static property => property.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var allItems = projects
            .SelectMany(
                (project, index) =>
                    project.AllEvaluatedItems.Select(item =>
                        Item(
                            projectPath,
                            item,
                            targetFrameworks.Length == 0
                                ? EvaluationDimension.Outer
                                : new EvaluationDimension(targetFrameworks[index])
                        )
                    )
            )
            .OrderBy(static item => item.Dimension.TargetFramework, StringComparer.Ordinal)
            .ThenBy(static item => item.ItemType, StringComparer.Ordinal)
            .ThenBy(static item => item.EvaluatedInclude, StringComparer.Ordinal)
            .ToImmutableArray();
        var projectReferences = References(projectPath, projects, "ProjectReference");
        var references = References(projectPath, projects, "Reference");
        var centralVersions = projects
            .SelectMany(project => project.GetItems("PackageVersion"))
            .GroupBy(static item => item.EvaluatedInclude, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => EmptyToNull(group.Last().GetMetadataValue("Version")),
                StringComparer.Ordinal
            );
        var packages = projects
            .SelectMany(project => project.GetItems("PackageReference"))
            .Select(item => new EvaluatedPackage(
                item.EvaluatedInclude,
                EmptyToNull(item.GetMetadataValue("Version"))
                    ?? (
                        centralVersions.TryGetValue(item.EvaluatedInclude, out var version)
                            ? version
                            : null
                    )
            ))
            .Distinct()
            .OrderBy(static package => package.Id, StringComparer.Ordinal)
            .ThenBy(static package => package.Version, StringComparer.Ordinal)
            .ToImmutableArray();
        var analyzers = projects
            .SelectMany(project => project.GetItems("Analyzer"))
            .Select(item => Resolve(projectPath, item.EvaluatedInclude))
            .Where(static path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToImmutableArray();
        var imports = projects
            .Append(outer)
            .SelectMany(project =>
                project.Imports.Select(import => import.ImportedProject.FullPath)
            )
            .Append(projectPath)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToImmutableArray();
        var watchInputs = imports
            .Concat(DirectoryInputs(projectPath))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToImmutableArray();
        var globRoots = new[] { Path.GetDirectoryName(projectPath)! }
            .Concat(
                allItems
                    .Select(item => item.ResolvedPath)
                    .Where(static path => path is not null)
                    .Select(path => Path.GetDirectoryName(path!)!)
            )
            .Where(static path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToImmutableArray();
        var profile = IsManagedSdkProject(outer)
            ? MsBuildCapabilityProfile.Full
            : MsBuildCapabilityProfile.UnknownProjectSystem;
        var capabilities =
            profile == MsBuildCapabilityProfile.Full
                ? ImmutableArray.Create("workspace.read", "workspace.write")
                : ImmutableArray.Create("workspace.read");
        var diagnostics = File.Exists(
            Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json")
        )
            ? ImmutableArray<MsBuildDiagnostic>.Empty
            : ImmutableArray.Create(
                new MsBuildDiagnostic(
                    "msbuild.assets_missing",
                    "Restore assets are missing; evaluation did not run restore.",
                    true
                )
            );

        return new EvaluationSnapshot(
            projectPath,
            allProperties,
            allItems,
            projectReferences,
            references,
            packages,
            targetFrameworks,
            analyzers,
            imports,
            watchInputs,
            globRoots,
            profile,
            capabilities,
            diagnostics
        );
    }

    private static EvaluatedItem Item(
        string projectPath,
        ProjectItem item,
        EvaluationDimension dimension
    ) =>
        new(
            item.ItemType,
            item.EvaluatedInclude,
            Resolve(projectPath, item.EvaluatedInclude),
            item.Metadata.Select(metadata => new EvaluatedMetadata(
                    metadata.Name,
                    metadata.EvaluatedValue
                ))
                .OrderBy(static metadata => metadata.Name, StringComparer.Ordinal)
                .ToImmutableArray(),
            dimension
        );

    private static ImmutableArray<EvaluatedReference> References(
        string projectPath,
        IEnumerable<Project> projects,
        string itemType
    ) =>
        projects
            .SelectMany(project => project.GetItems(itemType))
            .Select(item => new EvaluatedReference(
                item.EvaluatedInclude,
                Resolve(projectPath, item.EvaluatedInclude)
            ))
            .Distinct()
            .OrderBy(static reference => reference.Include, StringComparer.Ordinal)
            .ToImmutableArray();

    private static ImmutableArray<string> TargetFrameworks(Project project) =>
        project
            .GetPropertyValue("TargetFrameworks")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(
                project
                    .GetPropertyValue("TargetFramework")
                    .Split(
                        ';',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
            )
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static framework => framework, StringComparer.Ordinal)
            .ToImmutableArray();

    private static IEnumerable<string> DirectoryInputs(string projectPath)
    {
        for (
            var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
            directory is not null;
            directory = directory.Parent
        )
        {
            foreach (
                var name in new[]
                {
                    "Directory.Build.props",
                    "Directory.Build.targets",
                    "Directory.Packages.props",
                }
            )
            {
                var candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate))
                {
                    yield return Path.GetFullPath(candidate);
                }
            }
        }
    }

    private static bool IsManagedSdkProject(Project project) =>
        string.Equals(
            project.GetPropertyValue("UsingMicrosoftNETSdk"),
            "true",
            StringComparison.OrdinalIgnoreCase
        ) && Path.GetExtension(project.FullPath) is ".csproj" or ".fsproj" or ".vbproj";

    private static string? Resolve(string projectPath, string include)
    {
        if (string.IsNullOrWhiteSpace(include) || include.Contains("$", StringComparison.Ordinal))
        {
            return null;
        }

        var candidate = Path.IsPathRooted(include)
            ? include
            : Path.Combine(Path.GetDirectoryName(projectPath)!, include);
        return File.Exists(candidate) || Directory.Exists(candidate)
            ? Path.GetFullPath(candidate)
            : null;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void Add(
        string projectPath,
        EvaluationSnapshot snapshot,
        ImmutableArray<Project> projects
    )
    {
        if (cache.Count == CacheCapacity)
        {
            Remove(recency.Last!.Value);
        }

        var node = recency.AddFirst(projectPath);
        cache.Add(
            projectPath,
            new CacheEntry(projectPath, snapshot, snapshot.WatchInputs, projects, node)
        );
    }

    private void Touch(CacheEntry entry)
    {
        recency.Remove(entry.RecencyNode);
        recency.AddFirst(entry.RecencyNode);
    }

    private void Remove(string projectPath)
    {
        if (!cache.Remove(projectPath, out var entry))
        {
            return;
        }

        recency.Remove(entry.RecencyNode);
        foreach (var project in entry.Projects)
        {
            collection.UnloadProject(project);
        }
    }

    private sealed record CacheEntry(
        string ProjectPath,
        EvaluationSnapshot Snapshot,
        ImmutableArray<string> WatchInputs,
        ImmutableArray<Project> Projects,
        LinkedListNode<string> RecencyNode
    );
}
