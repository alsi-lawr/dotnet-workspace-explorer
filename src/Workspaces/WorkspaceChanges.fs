namespace Dotnet.WorkspaceExplorer.Workspaces

open System.Collections.Immutable

type WorkspaceNodeReplacement =
    { OldId: WorkspaceNodeId
      NewId: WorkspaceNodeId }

type WorkspacePageToken private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> WorkspaceValue.nonEmpty (nameof value) |> WorkspacePageToken

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? WorkspacePageToken as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        System.StringComparer.Ordinal.GetHashCode value

type WorkspaceRoot =
    { Revision: WorkspaceRevision
      Nodes: ImmutableArray<WorkspaceNode> }

type WorkspaceNodePage =
    { Revision: WorkspaceRevision
      ParentWorkspaceNodeId: WorkspaceNodeId
      Nodes: ImmutableArray<WorkspaceNode>
      NextToken: WorkspacePageToken option }

type WorkspaceExport =
    { Revision: WorkspaceRevision
      Nodes: ImmutableArray<WorkspaceNode> }

type WorkspaceChange =
    | Added of node: WorkspaceNode * parentNodeId: WorkspaceNodeId option * index: int
    | Removed of nodeId: WorkspaceNodeId * parentNodeId: WorkspaceNodeId option * index: int
    | Updated of node: WorkspaceNode * parentNodeId: WorkspaceNodeId option * index: int
    | Moved of
        nodeId: WorkspaceNodeId *
        oldParentId: WorkspaceNodeId option *
        oldIndex: int *
        newParentId: WorkspaceNodeId option *
        newIndex: int
    | Replaced of
        oldWorkspaceNodeId: WorkspaceNodeId *
        newNode: WorkspaceNode *
        parentNodeId: WorkspaceNodeId option *
        index: int

type WorkspaceDelta =
    { WorkspaceId: WorkspaceId
      BaseRevision: WorkspaceRevision
      NewRevision: WorkspaceRevision
      Changes: ImmutableArray<WorkspaceChange>
      Diagnostics: ImmutableArray<WorkspaceDiagnostic> }

type WorkspaceReset =
    { WorkspaceId: WorkspaceId
      Revision: WorkspaceRevision
      Diagnostics: ImmutableArray<WorkspaceDiagnostic> }
