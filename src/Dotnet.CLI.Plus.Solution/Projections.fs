namespace Dotnet.CLI.Plus.Solution

open System.Collections.Immutable
open System.Collections.Generic
open Dotnet.CLI.Plus.Core

type SolutionProjectPath =
    { AbsolutePath: WorkspaceArtifactPath
      SolutionRelativePath: string
      IsExternal: bool }

type SolutionFolderProjection =
    { Node: WorkspaceNode
      Path: string
      ParentPath: string option }

type SolutionItemProjection =
    { Node: WorkspaceNode
      FolderPath: string option
      RelativePath: string }

type ProjectConfigurationRuleProjection =
    { SolutionBuildType: string
      SolutionPlatform: string
      Dimension: string
      ProjectValue: string }

type ProjectConfigurationMappingProjection =
    { SolutionBuildType: string
      SolutionPlatform: string
      ProjectBuildType: string
      ProjectPlatform: string
      Builds: bool
      Deploys: bool }

type SolutionDependencyProjection =
    { Node: WorkspaceNode
      ProjectId: NodeId
      DependsOnProjectId: NodeId }

type SolutionProjectProjection =
    { Node: WorkspaceNode
      Path: SolutionProjectPath
      ParentFolderPath: string option
      IsFilteredOut: bool
      ConfigurationRules: ImmutableArray<ProjectConfigurationRuleProjection>
      ConfigurationMappings: ImmutableArray<ProjectConfigurationMappingProjection> }

type SolutionRootProjection =
    { Workspace: WorkspaceDescriptor
      Root: WorkspaceRoot
      Nodes: ImmutableArray<WorkspaceNode>
      Folders: ImmutableArray<SolutionFolderProjection>
      Items: ImmutableArray<SolutionItemProjection>
      Projects: ImmutableArray<SolutionProjectProjection>
      BuildTypes: ImmutableArray<WorkspaceNode>
      Platforms: ImmutableArray<WorkspaceNode>
      Dependencies: ImmutableArray<SolutionDependencyProjection> }

type SolutionWorkspace =
    private
        { Descriptor: WorkspaceDescriptor
          BackingSolutionPath: WorkspaceArtifactPath
          Root: SolutionRootProjection }

    member this.WorkspaceDescriptor = this.Descriptor
    member this.BackingPath = this.BackingSolutionPath
    member this.RootProjection = this.Root

    static member internal Create
        (descriptor: WorkspaceDescriptor, backingSolutionPath: WorkspaceArtifactPath, root: SolutionRootProjection)
        =
        { Descriptor = descriptor
          BackingSolutionPath = backingSolutionPath
          Root = root }

type ProjectCapabilityEnrichment =
    { ProjectId: NodeId
      CapabilityProfile: WorkspaceCapabilityProfile }

[<AbstractClass; Sealed>]
type SolutionProjection private () =
    static member EnrichProjectCapabilities
        (workspace: SolutionWorkspace, enrichments: seq<ProjectCapabilityEnrichment>)
        =
        if isNull (box workspace) then
            nullArg (nameof workspace)

        if isNull (box enrichments) then
            nullArg (nameof enrichments)

        let profiles = Dictionary<NodeId, WorkspaceCapabilityProfile>()

        for enrichment in enrichments do
            profiles[enrichment.ProjectId] <- enrichment.CapabilityProfile

        let replaceNode (node: WorkspaceNode) =
            match profiles.TryGetValue node.NodeId with
            | true, profile ->
                WorkspaceNode.CreateWithLoadState(
                    workspace.WorkspaceDescriptor,
                    node.NodeKind,
                    node.Identity,
                    node.Name,
                    profile,
                    node.NodeLoadState
                )
            | false, _ -> node

        let projects =
            workspace.RootProjection.Projects
            |> Seq.map (fun project ->
                { project with
                    Node = replaceNode project.Node })
            |> ImmutableArray.CreateRange

        let nodes =
            workspace.RootProjection.Nodes
            |> Seq.map replaceNode
            |> ImmutableArray.CreateRange

        let root =
            { workspace.RootProjection.Root with
                Nodes = nodes }

        let projection =
            { workspace.RootProjection with
                Root = root
                Nodes = nodes
                Projects = projects }

        SolutionWorkspace.Create(workspace.WorkspaceDescriptor, workspace.BackingPath, projection)
