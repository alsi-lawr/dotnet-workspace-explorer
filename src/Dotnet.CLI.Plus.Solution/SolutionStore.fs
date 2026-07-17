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

    let throwIfCancellationRequested (cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()

    let comparer semantics : StringComparer =
        match semantics with
        | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase
        | _ -> StringComparer.Ordinal

    let pathIdentity semantics (path: string) =
        match semantics with
        | HostFileSystemCaseSemantics.Insensitive -> path.ToUpperInvariant()
        | _ -> path

    let includedProjects semantics paths =
        paths
        |> Option.map (fun values -> ImmutableHashSet.CreateRange<string>(comparer semantics, values))

    let orderBy cancellationToken key values =
        throwIfCancellationRequested cancellationToken

        let ordered =
            values
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(key left, key right))
            |> Seq.toArray

        throwIfCancellationRequested cancellationToken
        ordered

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

    let resolveCandidates cancellationToken predicate candidates =
        let matches = ResizeArray<string>()

        for candidate in candidates do
            throwIfCancellationRequested cancellationToken

            if predicate candidate then
                matches.Add(System.IO.Path.GetFullPath candidate)

        matches
        |> orderBy cancellationToken id
        |> Seq.truncate 2
        |> Seq.toArray
        |> function
            | [||] -> notFound "solution" "No solution or filter file was found."
            | [| path |] -> Success path
            | _ -> ambiguous "solution" "Multiple solution or filter files were found."

    let resolveTarget targetPath cancellationToken =
        throwIfCancellationRequested cancellationToken

        if String.IsNullOrWhiteSpace targetPath then
            invalidInput "targetPath" "A solution path is required."
        else
            try
                if Directory.Exists targetPath then
                    Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories)
                    |> resolveCandidates cancellationToken isCandidate
                else
                    let path = System.IO.Path.GetFullPath targetPath

                    if not (File.Exists path) then
                        notFound path "The solution or filter file was not found."
                    elif isCandidate path then
                        Success path
                    else
                        invalidInput "targetPath" "Expected a .sln, .slnx, or .slnf file."
            with
            | :? PathTooLongException -> invalidInput "targetPath" "The solution path is invalid."
            | :? IOException -> internalFailure "solution.resolve_failed" "Failed to resolve the solution path."
            | :? UnauthorizedAccessException ->
                internalFailure "solution.resolve_failed" "Failed to resolve the solution path."
            | :? ArgumentException
            | :? NotSupportedException -> invalidInput "targetPath" "The solution path is invalid."

    let resolveBackingSolution backingPath cancellationToken =
        throwIfCancellationRequested cancellationToken

        if Directory.Exists backingPath then
            Directory.EnumerateFiles(backingPath, "*", SearchOption.AllDirectories)
            |> resolveCandidates cancellationToken isSolution
        elif not (File.Exists backingPath) then
            notFound backingPath "The filter backing solution was not found."
        elif isSolution backingPath then
            Success(System.IO.Path.GetFullPath backingPath)
        else
            invalidInput "solution" "The filter backing solution must be a .sln or .slnx file."

    type FilterDefinition =
        { BackingSolutionPath: string
          IncludedProjectPaths: ImmutableArray<string> option }

    let readFilter filterPath cancellationToken =
        task {
            try
                throwIfCancellationRequested cancellationToken
                use stream = File.OpenRead filterPath
                use! document = JsonDocument.ParseAsync(stream, cancellationToken = cancellationToken)
                let root = document.RootElement
                let mutable solution = Unchecked.defaultof<JsonElement>
                let mutable path = Unchecked.defaultof<JsonElement>

                if
                    root.ValueKind <> JsonValueKind.Object
                    || not (root.TryGetProperty("solution", &solution))
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

                    match resolveBackingSolution backingPath cancellationToken with
                    | Failure failure -> return Failure failure
                    | Success resolvedBacking ->
                        let mutable projects = Unchecked.defaultof<JsonElement>

                        if not (solution.TryGetProperty("projects", &projects)) then
                            return
                                Success
                                    { BackingSolutionPath = resolvedBacking
                                      IncludedProjectPaths = Some(ImmutableArray<string>.Empty) }
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
                                        throwIfCancellationRequested cancellationToken

                                        project.GetString()
                                        |> Option.ofObj
                                        |> Option.map (fun value ->
                                            let backingDirectory =
                                                System.IO.Path.GetDirectoryName resolvedBacking
                                                |> Option.ofObj
                                                |> Option.defaultValue (Directory.GetCurrentDirectory())

                                            System.IO.Path.GetFullPath(value, backingDirectory)))
                                    |> Seq.toArray

                                return
                                    Success
                                        { BackingSolutionPath = resolvedBacking
                                          IncludedProjectPaths = Some(ImmutableArray.CreateRange includedPaths) }
            with
            | :? OperationCanceledException -> return cancelled ()
            | :? JsonException -> return invalidInput "filter" "The solution filter is malformed JSON."
            | :? PathTooLongException -> return invalidInput "filter" "The solution filter contains an invalid path."
            | :? IOException ->
                return internalFailure "solution.filter_read_failed" "Failed to read the solution filter."
            | :? UnauthorizedAccessException ->
                return internalFailure "solution.filter_read_failed" "Failed to read the solution filter."
            | :? ArgumentException
            | :? NotSupportedException -> return invalidInput "filter" "The solution filter contains an invalid path."
        }

    let isExternal relativePath =
        relativePath = ".."
        || relativePath.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relativePath.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)

    let parentFolderPath (folder: SolutionFolderModel) =
        folder.Parent
        |> Option.ofObj
        |> Option.map (fun parent -> text (box parent.Path))

    let folderNode descriptor caseSemantics (folder: SolutionFolderModel) =
        WorkspaceNode.Create(
            descriptor,
            WorkspaceNodeKind.SolutionFolder,
            NodeSemanticIdentity.Create $"folder:{pathIdentity caseSemantics folder.Path}",
            folder.ActualDisplayName,
            WorkspaceCapabilityProfile.Full
        )

    let projectNode descriptor caseSemantics relativePath displayName filteredOut =
        let kind, identity, profile, loadState =
            if filteredOut then
                WorkspaceNodeKind.Placeholder,
                $"filtered-out:{pathIdentity caseSemantics relativePath}",
                WorkspaceCapabilityProfile.ReadOnly,
                WorkspaceNodeLoadState.FilteredOut
            else
                WorkspaceNodeKind.Project,
                $"project:{pathIdentity caseSemantics relativePath}",
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

    let projectMappings cancellationToken (project: SolutionProjectModel) (model: SolutionModel) =
        seq {
            for buildType in model.BuildTypes do
                for platform in model.Platforms do
                    throwIfCancellationRequested cancellationToken

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

    let validateFilterProjects
        (filter: FilterDefinition)
        (caseSemantics: HostFileSystemCaseSemantics)
        (model: SolutionModel)
        (cancellationToken: CancellationToken)
        =
        match includedProjects caseSemantics filter.IncludedProjectPaths with
        | None -> Success()
        | Some included ->
            let solutionDirectory =
                System.IO.Path.GetDirectoryName filter.BackingSolutionPath
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let projects = HashSet<string>(comparer caseSemantics)

            for project in model.SolutionProjects do
                throwIfCancellationRequested cancellationToken

                projects.Add(System.IO.Path.GetFullPath(project.FilePath, solutionDirectory))
                |> ignore

            if included |> Seq.exists (fun path -> not (projects.Contains path)) then
                invalidInput "filter" "The solution filter includes a project that is not in the backing solution."
            else
                Success()

    let projectRoot
        (descriptor: WorkspaceDescriptor)
        (caseSemantics: HostFileSystemCaseSemantics)
        (filter: FilterDefinition)
        (model: SolutionModel)
        (cancellationToken: CancellationToken)
        =
        throwIfCancellationRequested cancellationToken

        let solutionDirectory =
            System.IO.Path.GetDirectoryName filter.BackingSolutionPath
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let filterProjects = includedProjects caseSemantics filter.IncludedProjectPaths

        let folders =
            model.SolutionFolders
            |> Seq.map (fun (folder: SolutionFolderModel) ->
                throwIfCancellationRequested cancellationToken

                { Node = folderNode descriptor caseSemantics folder
                  Path = text (box folder.Path)
                  ParentPath = parentFolderPath folder })
            |> orderBy cancellationToken (fun folder -> folder.Node.Identity.Value)
            |> ImmutableArray.CreateRange

        let items =
            model.SolutionFolders
            |> Seq.collect (fun (folder: SolutionFolderModel) ->
                throwIfCancellationRequested cancellationToken

                (folder.Files
                 |> Option.ofObj
                 |> Option.map (fun files -> files :> seq<string>)
                 |> Option.defaultValue Seq.empty)
                |> Seq.map (fun (file: string) ->
                    throwIfCancellationRequested cancellationToken

                    { Node =
                        WorkspaceNode.Create(
                            descriptor,
                            WorkspaceNodeKind.SolutionItem,
                            NodeSemanticIdentity.Create
                                $"solution-item:{pathIdentity caseSemantics folder.Path}/{pathIdentity caseSemantics file}",
                            System.IO.Path.GetFileName(file),
                            WorkspaceCapabilityProfile.Full
                        )
                      FolderPath = Some folder.Path
                      RelativePath = file }))
            |> orderBy cancellationToken (fun item -> item.Node.Identity.Value)
            |> ImmutableArray.CreateRange

        let projects =
            model.SolutionProjects
            |> Seq.map (fun (project: SolutionProjectModel) ->
                throwIfCancellationRequested cancellationToken

                let projectFilePath = project.FilePath
                let absolutePath = System.IO.Path.GetFullPath(projectFilePath, solutionDirectory)
                let relativePath = System.IO.Path.GetRelativePath(solutionDirectory, absolutePath)

                let filteredOut =
                    filterProjects
                    |> Option.exists (fun included -> not (included.Contains absolutePath))

                { Node = projectNode descriptor caseSemantics relativePath project.ActualDisplayName filteredOut
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
                  ConfigurationMappings = projectMappings cancellationToken project model })
            |> orderBy cancellationToken (fun project -> project.Node.Identity.Value)
            |> ImmutableArray.CreateRange

        let buildTypes =
            model.BuildTypes
            |> Seq.map (fun (value: string) ->
                throwIfCancellationRequested cancellationToken

                WorkspaceNode.Create(
                    descriptor,
                    WorkspaceNodeKind.Configuration,
                    NodeSemanticIdentity.Create $"configuration:{value}",
                    value,
                    WorkspaceCapabilityProfile.Full
                ))
            |> orderBy cancellationToken (fun node -> node.Identity.Value)
            |> ImmutableArray.CreateRange

        let platforms =
            model.Platforms
            |> Seq.map (fun (value: string) ->
                throwIfCancellationRequested cancellationToken

                WorkspaceNode.Create(
                    descriptor,
                    WorkspaceNodeKind.Platform,
                    NodeSemanticIdentity.Create $"platform:{value}",
                    value,
                    WorkspaceCapabilityProfile.Full
                ))
            |> orderBy cancellationToken (fun node -> node.Identity.Value)
            |> ImmutableArray.CreateRange

        let projectIds = Dictionary<string, NodeId>(comparer caseSemantics)

        for project in projects do
            throwIfCancellationRequested cancellationToken
            projectIds[project.Path.AbsolutePath.Value] <- project.Node.NodeId

        let dependencies =
            model.SolutionProjects
            |> Seq.collect (fun (project: SolutionProjectModel) ->
                throwIfCancellationRequested cancellationToken

                let projectFilePath = project.FilePath
                let projectPath = System.IO.Path.GetFullPath(projectFilePath, solutionDirectory)

                (project.Dependencies
                 |> Option.ofObj
                 |> Option.map (fun dependencies -> dependencies :> seq<SolutionProjectModel>)
                 |> Option.defaultValue Seq.empty)
                |> Seq.map (fun (dependency: SolutionProjectModel) ->
                    throwIfCancellationRequested cancellationToken

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
                                $"solution-dependency:{pathIdentity caseSemantics projectPath}:{pathIdentity caseSemantics dependencyPath}",
                            $"{project.ActualDisplayName} depends on {dependency.ActualDisplayName}",
                            WorkspaceCapabilityProfile.ReadOnly
                        )
                      ProjectId = projectId
                      DependsOnProjectId = dependsOnProjectId }
                    : SolutionDependencyProjection))
            |> orderBy cancellationToken (fun dependency -> dependency.Node.Identity.Value)
            |> ImmutableArray.CreateRange

        let nodes =
            Seq.concat
                [ folders |> Seq.map _.Node
                  items |> Seq.map _.Node
                  projects |> Seq.map _.Node
                  buildTypes
                  platforms
                  dependencies |> Seq.map _.Node ]
            |> orderBy cancellationToken (fun node -> node.Identity.Value)
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
            try
                throwIfCancellationRequested cancellationToken

                match resolveTarget targetPath cancellationToken with
                | Failure failure -> return Failure failure
                | Success resolvedTarget ->
                    throwIfCancellationRequested cancellationToken
                    let caseSemantics = HostFileSystemCaseDetector.DetectFromExistingPath resolvedTarget
                    let targetFormat = format resolvedTarget

                    let! filter =
                        if targetFormat = WorkspaceFormat.Slnf then
                            readFilter resolvedTarget cancellationToken
                        else
                            Task.FromResult(
                                Success
                                    { BackingSolutionPath = resolvedTarget
                                      IncludedProjectPaths = None }
                            )

                    match filter with
                    | Failure failure -> return Failure failure
                    | Success selectedFilter ->
                        throwIfCancellationRequested cancellationToken

                        let backingCaseSemantics =
                            HostFileSystemCaseDetector.DetectFromExistingPath selectedFilter.BackingSolutionPath

                        match
                            SolutionSerializers.GetSerializerByMoniker selectedFilter.BackingSolutionPath
                            |> Option.ofObj
                        with
                        | None -> return invalidInput "solution" "The backing solution must be a .sln or .slnx file."
                        | Some serializer ->
                            let! model = serializer.OpenAsync(selectedFilter.BackingSolutionPath, cancellationToken)
                            throwIfCancellationRequested cancellationToken

                            match
                                validateFilterProjects selectedFilter backingCaseSemantics model cancellationToken
                            with
                            | Failure failure -> return Failure failure
                            | Success() ->
                                let descriptor =
                                    WorkspaceDescriptor.Create(
                                        WorkspaceTargetPath.Create resolvedTarget,
                                        caseSemantics,
                                        targetFormat,
                                        WorkspaceRevision.Create 0L,
                                        WorkspaceAccess.ReadWrite
                                    )

                                let root =
                                    projectRoot descriptor backingCaseSemantics selectedFilter model cancellationToken

                                throwIfCancellationRequested cancellationToken

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
            | :? PathTooLongException -> return invalidInput "targetPath" "The solution path is invalid."
            | :? IOException -> return internalFailure "solution.open_failed" "Failed to read the solution."
            | :? UnauthorizedAccessException ->
                return internalFailure "solution.open_failed" "Failed to read the solution."
            | :? ArgumentException
            | :? NotSupportedException -> return invalidInput "targetPath" "The solution path is invalid."
        }

[<AbstractClass; Sealed>]
type SolutionStore private () =
    static member OpenAsync
        (targetPath: string, cancellationToken: CancellationToken)
        : Task<WorkspaceOutcome<SolutionWorkspace>> =
        SolutionStoreImplementation.openWorkspace targetPath cancellationToken

    static member OpenAsync(targetPath: string) : Task<WorkspaceOutcome<SolutionWorkspace>> =
        SolutionStoreImplementation.openWorkspace targetPath CancellationToken.None
