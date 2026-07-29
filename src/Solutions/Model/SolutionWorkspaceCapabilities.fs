namespace Dotnet.WorkspaceExplorer.Solutions

open Dotnet.WorkspaceExplorer.Workspaces

open System.Collections.Immutable
open System.Collections.Generic

type ProjectCapabilityUpdate =
    { ProjectId: WorkspaceNodeId
      CapabilityProfile: WorkspaceCapabilityProfile }

[<AbstractClass; Sealed>]
type SolutionWorkspaceCapabilities private () =
    static member EnrichProjectCapabilities
        (workspace: SolutionWorkspace, enrichments: seq<ProjectCapabilityUpdate>)
        =
        if isNull (box workspace) then
            nullArg (nameof workspace)

        if isNull (box enrichments) then
            nullArg (nameof enrichments)

        let profiles = Dictionary<WorkspaceNodeId, WorkspaceCapabilityProfile>()

        for enrichment in enrichments do
            profiles[enrichment.ProjectId] <- enrichment.CapabilityProfile

        let replaceNode (node: WorkspaceNode) =
            match profiles.TryGetValue node.Id with
            | true, profile ->
                WorkspaceNode.CreateWithLoadState(
                    workspace.Descriptor,
                    node.Kind,
                    node.Identity,
                    node.Name,
                    profile,
                    node.LoadState
                )
            | false, _ -> node

        let projects =
            workspace.Contents.Projects
            |> Seq.map (fun project ->
                { project with
                    Node = replaceNode project.Node })
            |> ImmutableArray.CreateRange

        let nodes =
            workspace.Contents.Nodes |> Seq.map replaceNode |> ImmutableArray.CreateRange

        let root =
            { workspace.Contents.Root with
                Nodes = nodes }

        let projection =
            { workspace.Contents with
                Root = root
                Nodes = nodes
                Projects = projects }

        SolutionWorkspace.Create(workspace.Descriptor, workspace.SolutionPath, projection)
