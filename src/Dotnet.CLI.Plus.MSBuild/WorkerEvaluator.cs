using System.Collections.Immutable;
using Dotnet.CLI.Plus.Core;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;

namespace Dotnet.CLI.Plus.MSBuild;

internal sealed class WorkerEvaluator : IDisposable
{
    private const int DefaultCacheCapacity = 64;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly ProjectCollection collection = new();
    private readonly Dictionary<string, CacheEntry> cache;
    private readonly LinkedList<string> recency = new();

    internal WorkerEvaluator()
    {
        cache = new Dictionary<string, CacheEntry>(PathComparer);
    }

    internal WorkspaceOutcome<EvaluationSnapshot> Evaluate(
        WorkspaceArtifactPath requestedProjectPath
    )
    {
        var projectPath = WorkspaceArtifactPath.Create(requestedProjectPath.Value);
        if (!File.Exists(projectPath.Value))
        {
            return CoreOutcomes.NotFound<EvaluationSnapshot>(
                projectPath,
                MsBuildDiagnosticCodes.ProjectNotFound,
                "The project file was not found."
            );
        }

        if (cache.TryGetValue(projectPath.Value, out var cached))
        {
            Touch(cached);
            return CoreOutcomes.Success(cached.Snapshot);
        }

        var existingProjects = collection.LoadedProjects.ToHashSet(
            ReferenceEqualityComparer.Instance
        );
        var cacheOwnsLoadedProjects = false;

        try
        {
            var outer = Load(projectPath.Value, null);
            var targetFrameworks = GetTargetFrameworks(outer);
            var loaded = ImmutableArray.CreateBuilder<Project>(targetFrameworks.Length + 1);
            loaded.Add(outer);
            loaded.AddRange(
                targetFrameworks.Select(framework => Load(projectPath.Value, framework))
            );
            var projects = loaded.MoveToImmutable();
            var snapshot = Materialize(projectPath, projects, targetFrameworks);
            Add(projectPath.Value, snapshot, projects);
            cacheOwnsLoadedProjects = true;
            return CoreOutcomes.Success(snapshot);
        }
        catch (InvalidProjectFileException)
        {
            return CoreOutcomes.InvalidInput<EvaluationSnapshot>(
                "projectPath",
                MsBuildDiagnosticCodes.ProjectMalformed,
                "MSBuild could not evaluate the project because the project or selected SDK is incompatible.",
                projectPath
            );
        }
        catch (IOException)
        {
            return CoreOutcomes.Internal<EvaluationSnapshot>(
                MsBuildDiagnosticCodes.EvaluationFailed,
                "MSBuild could not read the project."
            );
        }
        catch (UnauthorizedAccessException)
        {
            return CoreOutcomes.Internal<EvaluationSnapshot>(
                MsBuildDiagnosticCodes.EvaluationFailed,
                "MSBuild could not read the project."
            );
        }
        finally
        {
            if (!cacheOwnsLoadedProjects)
            {
                foreach (
                    var project in collection
                        .LoadedProjects.Where(project => !existingProjects.Contains(project))
                        .ToArray()
                )
                {
                    collection.UnloadProject(project);
                }
            }
        }
    }

    internal InvalidationResult Invalidate(ImmutableArray<WorkspaceArtifactPath> changedPaths)
    {
        var invalidated = cache
            .Values.Where(entry => changedPaths.Any(path => Affects(entry, path.Value)))
            .Select(entry => entry.Snapshot.ProjectPath)
            .OrderBy(path => path.Value, PathComparer)
            .ToImmutableArray();

        foreach (var projectPath in invalidated)
        {
            Remove(projectPath.Value);
        }

        if (!invalidated.IsDefaultOrEmpty)
        {
            collection.UnloadAllProjects();
            foreach (var projectPath in cache.Keys.ToArray())
            {
                cache[projectPath] = cache[projectPath] with { Projects = [] };
            }
        }

        return new InvalidationResult(invalidated);
    }

    public void Dispose()
    {
        foreach (var entry in cache.Values)
        {
            Unload(entry);
        }

        cache.Clear();
        recency.Clear();
        collection.Dispose();
    }

    private Project Load(string projectPath, TargetFramework? targetFramework)
    {
        var properties = targetFramework is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TargetFramework"] = targetFramework.Value.Value,
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
        WorkspaceArtifactPath projectPath,
        ImmutableArray<Project> projects,
        ImmutableArray<TargetFramework> targetFrameworks
    )
    {
        var dimensions = ImmutableArray.CreateBuilder<EvaluationDimensionSnapshot>(projects.Length);
        dimensions.Add(MaterializeDimension(projectPath, projects[0], null));

        for (var index = 0; index < targetFrameworks.Length; index++)
        {
            dimensions.Add(
                MaterializeDimension(projectPath, projects[index + 1], targetFrameworks[index])
            );
        }

        var imports = projects
            .SelectMany(project =>
                project.Imports.Select(import => import.ImportedProject.FullPath)
            )
            .Append(projectPath.Value)
            .Select(WorkspaceArtifactPath.Create)
            .DistinctBy(path => path.Value, PathComparer)
            .OrderBy(path => path.Value, PathComparer)
            .ToImmutableArray();
        var watchInputs = imports
            .Concat(DirectoryInputs(projectPath.Value))
            .DistinctBy(path => path.Value, PathComparer)
            .OrderBy(path => path.Value, PathComparer)
            .ToImmutableArray();
        var globRoots = projects
            .SelectMany(project => GlobRoots(projectPath.Value, project))
            .DistinctBy(path => path.Value, PathComparer)
            .OrderBy(path => path.Value, PathComparer)
            .ToImmutableArray();
        var profile = IsManagedSdkProject(projects[0])
            ? WorkspaceCapabilityProfile.Full
            : WorkspaceCapabilityProfile.UnknownProjectSystem;
        var capabilities =
            profile == WorkspaceCapabilityProfile.Full
                ? ImmutableArray.Create(WorkspaceCapabilityId.Read, WorkspaceCapabilityId.Write)
                : ImmutableArray.Create(WorkspaceCapabilityId.Read);
        var diagnostics = File.Exists(
            Path.Combine(Path.GetDirectoryName(projectPath.Value)!, "obj", "project.assets.json")
        )
            ? ImmutableArray<WorkspaceDiagnostic>.Empty
            : ImmutableArray.Create(
                CoreOutcomes.Diagnostic(
                    MsBuildDiagnosticCodes.AssetsMissing,
                    "Restore assets are missing; evaluation did not run restore.",
                    projectPath,
                    true,
                    WorkspaceDiagnosticSeverity.Warning
                )
            );

        return new EvaluationSnapshot(
            projectPath,
            dimensions.MoveToImmutable(),
            imports,
            watchInputs,
            globRoots,
            profile,
            capabilities,
            diagnostics
        );
    }

    private static EvaluationDimensionSnapshot MaterializeDimension(
        WorkspaceArtifactPath projectPath,
        Project project,
        TargetFramework? targetFramework
    )
    {
        var properties = project
            .AllEvaluatedProperties.Select(property => new EvaluatedProperty(
                property.Name,
                property.EvaluatedValue
            ))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ThenBy(property => property.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var items = project
            .AllEvaluatedItems.Select(item => MaterializeItem(projectPath.Value, item))
            .OrderBy(item => item.ItemType, StringComparer.Ordinal)
            .ThenBy(item => item.EvaluatedInclude, StringComparer.Ordinal)
            .ToImmutableArray();
        var centralVersions = project
            .GetItems("PackageVersion")
            .GroupBy(item => item.EvaluatedInclude, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => EmptyToNull(group.Last().GetMetadataValue("Version")),
                StringComparer.Ordinal
            );
        var packages = project
            .GetItems("PackageReference")
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
            .OrderBy(package => package.Id, StringComparer.Ordinal)
            .ThenBy(package => package.Version, StringComparer.Ordinal)
            .ToImmutableArray();
        var analyzers = project
            .GetItems("Analyzer")
            .Select(item => Resolve(projectPath.Value, item.EvaluatedInclude))
            .Where(path => path is not null)
            .Cast<WorkspaceArtifactPath>()
            .DistinctBy(path => path.Value, PathComparer)
            .OrderBy(path => path.Value, PathComparer)
            .ToImmutableArray();

        return new EvaluationDimensionSnapshot(
            targetFramework,
            properties,
            items,
            References(projectPath.Value, project, "ProjectReference"),
            References(projectPath.Value, project, "Reference"),
            packages,
            analyzers
        );
    }

    private static EvaluatedItem MaterializeItem(string projectPath, ProjectItem item) =>
        new(
            item.ItemType,
            ItemInclude(item),
            Resolve(projectPath, ItemInclude(item)),
            item.Metadata.Select(metadata => new EvaluatedMetadata(
                    metadata.Name,
                    MetadataValue(metadata)
                ))
                .OrderBy(metadata => metadata.Name, StringComparer.Ordinal)
                .ToImmutableArray()
        );

    private static string ItemInclude(ProjectItem item)
    {
        try
        {
            return item.EvaluatedInclude;
        }
        catch (InvalidProjectFileException)
        {
            return item.UnevaluatedInclude;
        }
    }

    private static string MetadataValue(ProjectMetadata metadata)
    {
        try
        {
            return metadata.EvaluatedValue;
        }
        catch (InvalidProjectFileException)
        {
            return metadata.UnevaluatedValue;
        }
    }

    private static ImmutableArray<EvaluatedReference> References(
        string projectPath,
        Project project,
        string itemType
    ) =>
        project
            .GetItems(itemType)
            .Select(item => new EvaluatedReference(
                item.EvaluatedInclude,
                Resolve(
                    projectPath,
                    EmptyToNull(item.GetMetadataValue("HintPath")) ?? item.EvaluatedInclude
                )
            ))
            .Distinct()
            .OrderBy(reference => reference.Include, StringComparer.Ordinal)
            .ToImmutableArray();

    private static ImmutableArray<TargetFramework> GetTargetFrameworks(Project project) =>
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
            .OrderBy(framework => framework, StringComparer.Ordinal)
            .Select(framework => new TargetFramework(framework))
            .ToImmutableArray();

    private static IEnumerable<WorkspaceArtifactPath> DirectoryInputs(string projectPath)
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
                    yield return WorkspaceArtifactPath.Create(candidate);
                }
            }
        }
    }

    private static IEnumerable<WorkspaceArtifactPath> GlobRoots(string projectPath, Project project)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var roots = project
            .Imports.Select(import => import.ImportedProject)
            .Append(project.Xml)
            .SelectMany(root => root.Items)
            .SelectMany(item => ExpandedIncludes(project, item))
            .Where(include => include.IndexOfAny(['*', '?']) >= 0)
            .Select(include => GlobRoot(projectDirectory, include));

        foreach (var root in roots)
        {
            yield return WorkspaceArtifactPath.Create(root);
        }
    }

    private static IEnumerable<string> ExpandedIncludes(Project project, ProjectItemElement item)
    {
        if (
            item.Include.IndexOfAny(['*', '?']) < 0
            && !item.Include.Contains("$(", StringComparison.Ordinal)
        )
        {
            return [];
        }

        try
        {
            var expanded = project.ExpandString(item.Include);
            return expanded.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
        }
        catch (InvalidProjectFileException)
        {
            return item.Include.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
        }
    }

    private static string GlobRoot(string projectDirectory, string include)
    {
        var wildcard = include.IndexOfAny(['*', '?']);
        var separator = include.LastIndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            wildcard
        );
        var prefix = separator < 0 ? string.Empty : include[..separator];
        return Path.GetFullPath(prefix, projectDirectory);
    }

    private static bool IsManagedSdkProject(Project project) =>
        string.Equals(
            project.GetPropertyValue("UsingMicrosoftNETSdk"),
            "true",
            StringComparison.OrdinalIgnoreCase
        ) && Path.GetExtension(project.FullPath) is ".csproj" or ".fsproj" or ".vbproj";

    private static WorkspaceArtifactPath? Resolve(string projectPath, string include)
    {
        if (string.IsNullOrWhiteSpace(include) || include.Contains('$'))
        {
            return null;
        }

        var candidate = Path.IsPathRooted(include)
            ? include
            : Path.Combine(Path.GetDirectoryName(projectPath)!, include);
        return File.Exists(candidate) || Directory.Exists(candidate)
            ? WorkspaceArtifactPath.Create(candidate)
            : null;
    }

    private static bool Affects(CacheEntry entry, string changedPath)
    {
        if (entry.Snapshot.WatchInputs.Any(path => PathComparer.Equals(path.Value, changedPath)))
        {
            return true;
        }

        return entry.Snapshot.GlobRoots.Any(root => IsDescendant(root.Value, changedPath));
    }

    private static bool IsDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal
            );
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void Add(
        string projectPath,
        EvaluationSnapshot snapshot,
        ImmutableArray<Project> projects
    )
    {
        if (cache.Count == DefaultCacheCapacity)
        {
            Remove(recency.Last!.Value);
        }

        var node = recency.AddFirst(projectPath);
        cache.Add(projectPath, new CacheEntry(snapshot, projects, node));
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
        Unload(entry);
    }

    private void Unload(CacheEntry entry)
    {
        foreach (var project in entry.Projects)
        {
            collection.UnloadProject(project);
        }
    }

    private sealed record CacheEntry(
        EvaluationSnapshot Snapshot,
        ImmutableArray<Project> Projects,
        LinkedListNode<string> RecencyNode
    );
}
