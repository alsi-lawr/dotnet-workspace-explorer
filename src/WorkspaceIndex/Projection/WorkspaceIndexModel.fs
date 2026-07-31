namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

[<RequireQualifiedAccess>]
type internal WorkspaceWatchKind =
    | ExactFile
    | RecursiveGlob

type internal WorkspaceWatch =
    { Directory: string
      Filters: ImmutableArray<string>
      IncludeSubdirectories: bool
      Kind: WorkspaceWatchKind }

type internal IndexedNodeKey = IndexedNodeKey of string list

type internal IndexedNode =
    { Key: IndexedNodeKey
      Node: WorkspaceNode
      ParentWorkspaceNodeId: WorkspaceNodeId option
      PhysicalRelativePath: string option
      Index: int }

type internal IndexedWorkspace =
    { Workspace: SolutionWorkspace
      Hydrated: Map<string, EvaluatedWorkspaceProject>
      Recency: string list
      Revision: int64
      NeedsRebase: bool }

type internal ProjectNodePlacement =
    { PlacementKey: IndexedNodeKey
      PlacementNode: WorkspaceNode
      ParentNodeId: WorkspaceNodeId
      PhysicalRelativePath: string option
      SiblingOrder: string list }
