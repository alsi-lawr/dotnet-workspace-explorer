namespace Dotnet.WorkspaceExplorer.Solutions

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces
open Microsoft.VisualStudio.SolutionPersistence.Model
open SolutionTargetResolution
open SolutionFilterReader

module internal SolutionDocumentProjection =
    let isExternal relativePath =
        relativePath = ".."
        || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)

    let parentFolderPath (folder: SolutionFolderModel) =
        folder.Parent
        |> Option.ofObj
        |> Option.map (fun parent -> text (box parent.Path))

    let folderNode descriptor caseSemantics (folder: SolutionFolderModel) =
        WorkspaceNode.Create(
            descriptor,
            WorkspaceNodeKind.SolutionFolder,
            WorkspaceNodeIdentity.Create $"folder:{pathIdentity caseSemantics folder.Path}",
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
            WorkspaceNodeIdentity.Create identity,
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
        (filter: SolutionFilterDefinition)
        (caseSemantics: FileSystemCaseSensitivity)
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
                invalidInput
                    "filter"
                    "The solution filter includes a project that is not in the backing solution."
            else
                Success()

    let projectRoot
        (descriptor: WorkspaceDescriptor)
        (caseSemantics: FileSystemCaseSensitivity)
        (filter: SolutionFilterDefinition)
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

                folder.Files
                |> Option.ofObj
                |> Option.map (fun files -> files :> seq<string>)
                |> Option.defaultValue Seq.empty
                |> Seq.map (fun (file: string) ->
                    throwIfCancellationRequested cancellationToken

                    { Node =
                        WorkspaceNode.Create(
                            descriptor,
                            WorkspaceNodeKind.SolutionItem,
                            WorkspaceNodeIdentity.Create
                                $"solution-item:{pathIdentity caseSemantics folder.Path}/{pathIdentity caseSemantics file}",
                            System.IO.Path.GetFileName file,
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
                let relativePath = Path.GetRelativePath(solutionDirectory, absolutePath)

                let filteredOut =
                    filterProjects
                    |> Option.exists (fun included -> not (included.Contains absolutePath))

                { Node =
                    projectNode
                        descriptor
                        caseSemantics
                        relativePath
                        project.ActualDisplayName
                        filteredOut
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
                    project.ProjectConfigurationRules
                    |> Option.ofObj
                    |> Option.map (fun rules -> rules :> seq<ConfigurationRule>)
                    |> Option.defaultValue Seq.empty
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
                    WorkspaceNodeIdentity.Create $"configuration:{value}",
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
                    WorkspaceNodeIdentity.Create $"platform:{value}",
                    value,
                    WorkspaceCapabilityProfile.Full
                ))
            |> orderBy cancellationToken (fun node -> node.Identity.Value)
            |> ImmutableArray.CreateRange

        let projectIds = Dictionary<string, WorkspaceNodeId>(comparer caseSemantics)

        for project in projects do
            throwIfCancellationRequested cancellationToken
            projectIds[project.Path.AbsolutePath.Value] <- project.Node.Id

        let dependencies =
            model.SolutionProjects
            |> Seq.collect (fun (project: SolutionProjectModel) ->
                throwIfCancellationRequested cancellationToken

                let projectFilePath = project.FilePath
                let projectPath = System.IO.Path.GetFullPath(projectFilePath, solutionDirectory)

                project.Dependencies
                |> Option.ofObj
                |> Option.map (fun dependencies -> dependencies :> seq<SolutionProjectModel>)
                |> Option.defaultValue Seq.empty
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
                            WorkspaceNodeIdentity.Create
                                $"solution-dependency:{pathIdentity caseSemantics projectPath}:{pathIdentity caseSemantics dependencyPath}",
                            $"{project.ActualDisplayName} depends on {dependency.ActualDisplayName}",
                            WorkspaceCapabilityProfile.ReadOnly
                        )
                      ProjectId = projectId
                      DependsOnProjectId = dependsOnProjectId }
                    : SolutionProjectDependency))
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
            { Revision = descriptor.Revision
              Nodes = nodes }
          Nodes = nodes
          Folders = folders
          Items = items
          Projects = projects
          BuildTypes = buildTypes
          Platforms = platforms
          Dependencies = dependencies }
