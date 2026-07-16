namespace Dotnet.CLI.Plus.Solution

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Microsoft.VisualStudio.SolutionPersistence
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer


module private SolutionStoreImplementation =
    let diagnostic code message retryable =
        WorkspaceDiagnostic.Create(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            None,
            None,
            retryable,
            CorrelationId.New()
        )

    let invalidInput input message =
        Failure(InvalidInput(input, diagnostic "solution.invalid_input" message false))

    let notFound target message =
        Failure(NotFound(target, diagnostic "solution.not_found" message false))

    let ambiguous target message =
        Failure(AmbiguousTarget(target, diagnostic "solution.ambiguous" message false))

    let cancelled () =
        Failure(Cancelled(OperationId.New(), diagnostic "solution.cancelled" "Solution operation was cancelled." true))

    let internalFailure code message =
        Failure(Internal(diagnostic code message true))

    let text (value: obj) =
        match value with
        | null -> String.Empty
        | :? string as result -> result
        | _ -> invalidArg (nameof value) "Expected a string value."

    let mapFailure outcome =
        match outcome with
        | Failure failure -> Failure failure
        | Success _ -> invalidOp "A successful outcome cannot be converted to a failure."

    let comparer semantics : StringComparer =
        match semantics with
        | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase
        | _ -> StringComparer.Ordinal

    let isExtension extension (path: string) =
        String.Equals(System.IO.Path.GetExtension path, extension, StringComparison.OrdinalIgnoreCase)

    let isSolution path =
        isExtension ".sln" path || isExtension ".slnx" path

    let isCandidate path =
        isSolution path || isExtension ".slnf" path

    let format path =
        if isExtension ".sln" path then
            WorkspaceFormat.Sln
        elif isExtension ".slnx" path then
            WorkspaceFormat.Slnx
        elif isExtension ".slnf" path then
            WorkspaceFormat.Slnf
        else
            invalidArg (nameof path) "Expected a supported solution path."

    let resolveCandidates predicate candidates =
        candidates
        |> Seq.filter predicate
        |> Seq.map System.IO.Path.GetFullPath
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        |> Seq.truncate 2
        |> Seq.toArray
        |> function
            | [||] -> notFound "solution" "No solution or filter file was found."
            | [| path |] -> Success path
            | _ -> ambiguous "solution" "Multiple solution or filter files were found."

    let resolveTarget targetPath =
        if String.IsNullOrWhiteSpace targetPath then
            invalidInput "targetPath" "A solution path is required."
        else
            try
                if Directory.Exists targetPath then
                    Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories)
                    |> resolveCandidates isCandidate
                else
                    let path = System.IO.Path.GetFullPath targetPath

                    if not (File.Exists path) then
                        notFound path "The solution or filter file was not found."
                    elif isCandidate path then
                        Success path
                    else
                        invalidInput "targetPath" "Expected a .sln, .slnx, or .slnf file."
            with
            | :? IOException -> internalFailure "solution.resolve_failed" "Failed to resolve the solution path."
            | :? UnauthorizedAccessException ->
                internalFailure "solution.resolve_failed" "Failed to resolve the solution path."

    let resolveBackingSolution backingPath =
        if Directory.Exists backingPath then
            Directory.EnumerateFiles(backingPath, "*", SearchOption.AllDirectories)
            |> resolveCandidates isSolution
        elif not (File.Exists backingPath) then
            notFound backingPath "The filter backing solution was not found."
        elif isSolution backingPath then
            Success(System.IO.Path.GetFullPath backingPath)
        else
            invalidInput "solution" "The filter backing solution must be a .sln or .slnx file."

    type FilterDefinition =
        { BackingSolutionPath: string
          IncludedProjects: ImmutableHashSet<string> option }

    let readFilter filterPath caseSemantics cancellationToken =
        task {
            try
                use stream = File.OpenRead filterPath
                use! document = JsonDocument.ParseAsync(stream, cancellationToken = cancellationToken)
                let root = document.RootElement
                let mutable solution = Unchecked.defaultof<JsonElement>
                let mutable path = Unchecked.defaultof<JsonElement>

                if
                    not (root.TryGetProperty("solution", &solution))
                    || solution.ValueKind <> JsonValueKind.Object
                    || not (solution.TryGetProperty("path", &path))
                    || path.ValueKind <> JsonValueKind.String
                    || String.IsNullOrWhiteSpace(path.GetString())
                then
                    return invalidInput "filter" "The solution filter must declare solution.path."
                else
                    let filterDirectory =
                        System.IO.Path.GetDirectoryName filterPath
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    let backingPath =
                        path.GetString()
                        |> Option.ofObj
                        |> Option.map (fun value -> System.IO.Path.GetFullPath(value, filterDirectory))
                        |> Option.defaultWith (fun () -> invalidArg "filter" "A filter solution path is required.")

                    match resolveBackingSolution backingPath with
                    | Failure failure -> return Failure failure
                    | Success resolvedBacking ->
                        let mutable projects = Unchecked.defaultof<JsonElement>

                        if not (solution.TryGetProperty("projects", &projects)) then
                            return
                                Success
                                    { BackingSolutionPath = resolvedBacking
                                      IncludedProjects = Some(ImmutableHashSet.Create<string>(comparer caseSemantics)) }
                        elif projects.ValueKind <> JsonValueKind.Array then
                            return invalidInput "filter" "The solution filter projects value must be an array of paths."
                        else
                            let values = projects.EnumerateArray() |> Seq.toArray

                            if
                                values
                                |> Array.exists (fun project -> project.ValueKind <> JsonValueKind.String)
                            then
                                return
                                    invalidInput
                                        "filter"
                                        "The solution filter projects value must be an array of paths."
                            else
                                let includedPaths =
                                    values
                                    |> Seq.choose (fun project ->
                                        project.GetString()
                                        |> Option.ofObj
                                        |> Option.map (fun value -> System.IO.Path.GetFullPath(value, filterDirectory)))
                                    |> Seq.toArray

                                let included =
                                    ImmutableHashSet.CreateRange<string>(comparer caseSemantics, includedPaths)

                                return
                                    Success
                                        { BackingSolutionPath = resolvedBacking
                                          IncludedProjects = Some included }
            with
            | :? OperationCanceledException -> return cancelled ()
            | :? JsonException -> return invalidInput "filter" "The solution filter is malformed JSON."
            | :? IOException ->
                return internalFailure "solution.filter_read_failed" "Failed to read the solution filter."
            | :? UnauthorizedAccessException ->
                return internalFailure "solution.filter_read_failed" "Failed to read the solution filter."
        }

    let isExternal relativePath =
        relativePath = ".."
        || relativePath.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relativePath.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)

    let parentFolderPath (folder: SolutionFolderModel) =
        folder.Parent
        |> Option.ofObj
        |> Option.map (fun parent -> text (box parent.Path))

    let folderNode descriptor (folder: SolutionFolderModel) =
        WorkspaceNode.Create(
            descriptor,
            WorkspaceNodeKind.SolutionFolder,
            NodeSemanticIdentity.Create $"folder:{folder.Path}",
            folder.ActualDisplayName,
            WorkspaceCapabilityProfile.Full
        )

    let projectNode descriptor relativePath displayName filteredOut =
        let kind, identity, profile, loadState =
            if filteredOut then
                WorkspaceNodeKind.Placeholder,
                $"filtered-out:{relativePath}",
                WorkspaceCapabilityProfile.ReadOnly,
                WorkspaceNodeLoadState.FilteredOut
            else
                WorkspaceNodeKind.Project,
                $"project:{relativePath}",
                WorkspaceCapabilityProfile.UnknownProjectSystem,
                WorkspaceNodeLoadState.Unhydrated

        WorkspaceNode.CreateWithLoadState(
            descriptor,
            kind,
            NodeSemanticIdentity.Create identity,
            (if filteredOut then
                 $"{displayName} (filtered out)"
             else
                 displayName),
            profile,
            loadState
        )

    let ruleProjection (rule: ConfigurationRule) =
        { SolutionBuildType = rule.SolutionBuildType
          SolutionPlatform = rule.SolutionPlatform
          Dimension = rule.Dimension.ToString()
          ProjectValue = rule.ProjectValue }

    let projectMappings (project: SolutionProjectModel) (model: SolutionModel) =
        seq {
            for buildType in model.BuildTypes do
                for platform in model.Platforms do
                    let struct (buildTypeValue, platformValue, builds, deploys) =
                        project.GetProjectConfiguration(buildType, platform)

                    yield
                        { SolutionBuildType = buildType
                          SolutionPlatform = platform
                          ProjectBuildType = buildTypeValue
                          ProjectPlatform = platformValue
                          Builds = builds
                          Deploys = deploys }
        }
        |> ImmutableArray.CreateRange

    let projectRoot (descriptor: WorkspaceDescriptor) (filter: FilterDefinition) (model: SolutionModel) =
        let solutionDirectory =
            System.IO.Path.GetDirectoryName filter.BackingSolutionPath
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let folders =
            model.SolutionFolders
            |> Seq.map (fun (folder: SolutionFolderModel) ->
                { Node = folderNode descriptor folder
                  Path = text (box folder.Path)
                  ParentPath = parentFolderPath folder })
            |> Seq.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left.Node.Identity.Value, right.Node.Identity.Value))
            |> ImmutableArray.CreateRange

        let items =
            model.SolutionFolders
            |> Seq.collect (fun (folder: SolutionFolderModel) ->
                (folder.Files
                 |> Option.ofObj
                 |> Option.map (fun files -> files :> seq<string>)
                 |> Option.defaultValue Seq.empty)
                |> Seq.map (fun (file: string) ->
                    { Node =
                        WorkspaceNode.Create(
                            descriptor,
                            WorkspaceNodeKind.SolutionItem,
                            NodeSemanticIdentity.Create $"solution-item:{folder.Path}/{file}",
                            System.IO.Path.GetFileName(file),
                            WorkspaceCapabilityProfile.Full
                        )
                      FolderPath = Some folder.Path
                      RelativePath = file }))
            |> Seq.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left.Node.Identity.Value, right.Node.Identity.Value))
            |> ImmutableArray.CreateRange

        let projects =
            model.SolutionProjects
            |> Seq.map (fun (project: SolutionProjectModel) ->
                let projectFilePath = project.FilePath
                let absolutePath = System.IO.Path.GetFullPath(projectFilePath, solutionDirectory)
                let relativePath = System.IO.Path.GetRelativePath(solutionDirectory, absolutePath)

                let filteredOut =
                    filter.IncludedProjects
                    |> Option.exists (fun included -> not (included.Contains absolutePath))

                { Node = projectNode descriptor relativePath project.ActualDisplayName filteredOut
                  Path =
                    { AbsolutePath = WorkspaceArtifactPath.Create absolutePath
                      SolutionRelativePath = relativePath
                      IsExternal = isExternal relativePath }
                  ParentFolderPath =
                    project.Parent
                    |> Option.ofObj
                    |> Option.map (fun parent -> text (box parent.Path))
                  IsFilteredOut = filteredOut
                  ConfigurationRules =
                    (project.ProjectConfigurationRules
                     |> Option.ofObj
                     |> Option.map (fun rules -> rules :> seq<ConfigurationRule>)
                     |> Option.defaultValue Seq.empty)
                    |> Seq.map ruleProjection
                    |> ImmutableArray.CreateRange
                  ConfigurationMappings = projectMappings project model })
            |> Seq.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left.Node.Identity.Value, right.Node.Identity.Value))
            |> ImmutableArray.CreateRange

        let buildTypes =
            model.BuildTypes
            |> Seq.map (fun (value: string) ->
                WorkspaceNode.Create(
                    descriptor,
                    WorkspaceNodeKind.Configuration,
                    NodeSemanticIdentity.Create $"configuration:{value}",
                    value,
                    WorkspaceCapabilityProfile.Full
                ))
            |> Seq.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left.Identity.Value, right.Identity.Value))
            |> ImmutableArray.CreateRange

        let platforms =
            model.Platforms
            |> Seq.map (fun (value: string) ->
                WorkspaceNode.Create(
                    descriptor,
                    WorkspaceNodeKind.Platform,
                    NodeSemanticIdentity.Create $"platform:{value}",
                    value,
                    WorkspaceCapabilityProfile.Full
                ))
            |> Seq.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left.Identity.Value, right.Identity.Value))
            |> ImmutableArray.CreateRange

        let projectIds = Dictionary<string, NodeId>(StringComparer.Ordinal)

        for project in projects do
            projectIds[project.Path.AbsolutePath.Value] <- project.Node.NodeId

        let dependencies =
            model.SolutionProjects
            |> Seq.collect (fun (project: SolutionProjectModel) ->
                let projectFilePath = project.FilePath
                let projectPath = System.IO.Path.GetFullPath(projectFilePath, solutionDirectory)

                (project.Dependencies
                 |> Option.ofObj
                 |> Option.map (fun dependencies -> dependencies :> seq<SolutionProjectModel>)
                 |> Option.defaultValue Seq.empty)
                |> Seq.map (fun (dependency: SolutionProjectModel) ->
                    let dependencyFilePath = dependency.FilePath

                    let dependencyPath =
                        System.IO.Path.GetFullPath(dependencyFilePath, solutionDirectory)

                    let projectId = projectIds[projectPath]
                    let dependsOnProjectId = projectIds[dependencyPath]

                    { Node =
                        WorkspaceNode.Create(
                            descriptor,
                            WorkspaceNodeKind.Placeholder,
                            NodeSemanticIdentity.Create
                                $"solution-dependency:{projectId.Value}:{dependsOnProjectId.Value}",
                            $"{project.ActualDisplayName} depends on {dependency.ActualDisplayName}",
                            WorkspaceCapabilityProfile.ReadOnly
                        )
                      ProjectId = projectId
                      DependsOnProjectId = dependsOnProjectId }
                    : SolutionDependencyProjection))
            |> Seq.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left.Node.Identity.Value, right.Node.Identity.Value))
            |> ImmutableArray.CreateRange

        let nodes =
            Seq.concat
                [ folders |> Seq.map _.Node
                  items |> Seq.map _.Node
                  projects |> Seq.map _.Node
                  buildTypes
                  platforms
                  dependencies |> Seq.map _.Node ]
            |> Seq.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left.Identity.Value, right.Identity.Value))
            |> ImmutableArray.CreateRange

        { Workspace = descriptor
          Root =
            { Revision = descriptor.WorkspaceRevision
              Nodes = nodes }
          Nodes = nodes
          Folders = folders
          Items = items
          Projects = projects
          BuildTypes = buildTypes
          Platforms = platforms
          Dependencies = dependencies }

    let openWorkspace targetPath cancellationToken =
        task {
            match resolveTarget targetPath with
            | Failure failure -> return Failure failure
            | Success resolvedTarget ->
                let caseSemantics = HostFileSystemCaseDetector.DetectFromExistingPath resolvedTarget
                let targetFormat = format resolvedTarget

                let! filter =
                    if targetFormat = WorkspaceFormat.Slnf then
                        readFilter resolvedTarget caseSemantics cancellationToken
                    else
                        Task.FromResult(
                            Success
                                { BackingSolutionPath = resolvedTarget
                                  IncludedProjects = None }
                        )

                match filter with
                | Failure failure -> return Failure failure
                | Success selectedFilter ->
                    match
                        SolutionSerializers.GetSerializerByMoniker selectedFilter.BackingSolutionPath
                        |> Option.ofObj
                    with
                    | None -> return invalidInput "solution" "The backing solution must be a .sln or .slnx file."
                    | Some serializer ->
                        try
                            let! model = serializer.OpenAsync(selectedFilter.BackingSolutionPath, cancellationToken)

                            let descriptor =
                                WorkspaceDescriptor.Create(
                                    WorkspaceTargetPath.Create resolvedTarget,
                                    caseSemantics,
                                    targetFormat,
                                    WorkspaceRevision.Create 0L,
                                    WorkspaceAccess.ReadWrite
                                )

                            let root = projectRoot descriptor selectedFilter model

                            return
                                Success(
                                    SolutionWorkspace.Create(
                                        descriptor,
                                        WorkspaceArtifactPath.Create selectedFilter.BackingSolutionPath,
                                        root
                                    )
                                )
                        with
                        | :? OperationCanceledException -> return cancelled ()
                        | :? SolutionException -> return invalidInput "solution" "The solution file is malformed."
                        | :? IOException -> return internalFailure "solution.open_failed" "Failed to read the solution."
                        | :? UnauthorizedAccessException ->
                            return internalFailure "solution.open_failed" "Failed to read the solution."
        }

[<AbstractClass; Sealed>]
type SolutionStore private () =
    static member OpenAsync
        (targetPath: string, cancellationToken: CancellationToken)
        : Task<WorkspaceOutcome<SolutionWorkspace>> =
        SolutionStoreImplementation.openWorkspace targetPath cancellationToken

    static member OpenAsync(targetPath: string) : Task<WorkspaceOutcome<SolutionWorkspace>> =
        SolutionStoreImplementation.openWorkspace targetPath CancellationToken.None
