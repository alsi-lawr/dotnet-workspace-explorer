namespace Dotnet.WorkspaceExplorer.Solutions

open Dotnet.WorkspaceExplorer.Workspaces

open System.Collections.Immutable

type SolutionProjectPath =
    { AbsolutePath: WorkspaceArtifactPath
      SolutionRelativePath: string
      IsExternal: bool }

type SolutionFolder =
    { Node: WorkspaceNode
      Path: string
      ParentPath: string option }

type SolutionItem =
    { Node: WorkspaceNode
      FolderPath: string option
      RelativePath: string }

type SolutionProjectConfigurationRule =
    { SolutionBuildType: string
      SolutionPlatform: string
      Dimension: string
      ProjectValue: string }

type SolutionProjectConfiguration =
    { SolutionBuildType: string
      SolutionPlatform: string
      ProjectBuildType: string
      ProjectPlatform: string
      Builds: bool
      Deploys: bool }

type SolutionProjectDependency =
    { Node: WorkspaceNode
      ProjectId: WorkspaceNodeId
      DependsOnProjectId: WorkspaceNodeId }

type SolutionProject =
    { Node: WorkspaceNode
      Path: SolutionProjectPath
      ParentFolderPath: string option
      IsFilteredOut: bool
      ConfigurationRules: ImmutableArray<SolutionProjectConfigurationRule>
      ConfigurationMappings: ImmutableArray<SolutionProjectConfiguration> }

type SolutionContents =
    { Workspace: WorkspaceDescriptor
      Root: WorkspaceRoot
      Nodes: ImmutableArray<WorkspaceNode>
      Folders: ImmutableArray<SolutionFolder>
      Items: ImmutableArray<SolutionItem>
      Projects: ImmutableArray<SolutionProject>
      BuildTypes: ImmutableArray<WorkspaceNode>
      Platforms: ImmutableArray<WorkspaceNode>
      Dependencies: ImmutableArray<SolutionProjectDependency> }
