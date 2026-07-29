namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System.Collections.Immutable
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation

type internal WorkspaceIndexServices =
    { OpenAsync: string -> CancellationToken -> Task<WorkspaceOutcome<SolutionWorkspace>>
      EvaluateAsync:
          WorkspaceArtifactPath
              -> WorkspaceArtifactPath
              -> CancellationToken
              -> Task<WorkspaceOutcome<ProjectEvaluationSnapshot>>
      InvalidateAsync:
          ImmutableArray<WorkspaceArtifactPath>
              -> CancellationToken
              -> Task<WorkspaceOutcome<ProjectEvaluationInvalidationKind>>
      OpenExportSessionAsync:
          WorkspaceArtifactPath
              -> int
              -> CancellationToken
              -> Task<WorkspaceOutcome<WorkspaceIndexExportSession>>
      RefreshAsync: unit -> Task
      DisposeAsync: unit -> Task }

and internal WorkspaceIndexExportSession =
    { EvaluateAsync:
        WorkspaceArtifactPath
            -> CancellationToken
            -> Task<WorkspaceOutcome<ProjectEvaluationSnapshot>>
      DisposeAsync: unit -> Task }

type internal WorkspaceIndexOptions =
    { HydrationLimit: int
      ExportCapacity: int
      TokenSecret: byte array }

type internal WorkspacePageResult =
    { Revision: int64
      ParentWorkspaceNodeId: WorkspaceNodeId
      Nodes: ImmutableArray<WorkspaceNode>
      NextToken: WorkspacePageToken option
      Delta: WorkspaceDelta option }

type internal WorkspaceRefreshResult =
    { Revision: int64
      Reset: bool
      Delta: WorkspaceDelta option
      ResetEvent: WorkspaceReset option
      Diagnostics: ImmutableArray<WorkspaceDiagnostic> }

type internal WorkspaceExportBatch =
    { Nodes: WorkspaceNode array
      IsFinal: bool }

[<RequireQualifiedAccess>]
type internal WorkspaceProjectInvalidationResult =
    | None
    | Delta of WorkspaceDelta
    | Reset of WorkspaceReset
