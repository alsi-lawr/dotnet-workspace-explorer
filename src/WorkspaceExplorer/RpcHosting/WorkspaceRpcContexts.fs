namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3511"

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

type internal WorkspaceRpcContext =
    { State: WorkspaceIndex
      GitStatus: WorkspaceGitStatus
      GitStatusNegotiated: unit -> bool
      Watcher: WorkspaceIndexWatcher
      ActiveOperations: ConcurrentDictionary<string, WorkspaceExportOperation>
      MaximumFrameBytes: unit -> int
      MaximumPageSize: unit -> int
      StartWatcher: bool -> (RpcNotificationSink -> CancellationToken -> Task<unit>) option }

type internal WorkspaceCommandContext =
    { State: WorkspaceIndex
      Watcher: WorkspaceIndexWatcher
      Coordinator: WorkspaceEditTransaction
      PublicationGate: SemaphoreSlim
      ActiveOperations: ConcurrentDictionary<string, WorkspaceExportOperation>
      WorkspaceRoot: string
      MaximumFrameBytes: unit -> int
      AddExistingNegotiated: unit -> bool
      AddExistingSelector: AddExistingSelector
      RebuildWatcher: CancellationToken -> Task<RpcFrame list>
      MutationNotifications: WorkspaceProjectInvalidationResult -> RpcFrame list }

type internal DotnetCommandOperationContext =
    { Workspace: SolutionWorkspace
      State: WorkspaceIndex
      Watcher: WorkspaceIndexWatcher
      Coordinator: WorkspaceEditTransaction
      PublicationGate: SemaphoreSlim
      ActiveOperations: ConcurrentDictionary<string, WorkspaceExportOperation>
      WorkspaceRoot: string
      MaximumFrameBytes: unit -> int
      RebuildWatcher: CancellationToken -> Task<RpcFrame list>
      MutationNotifications: WorkspaceProjectInvalidationResult -> RpcFrame list }

[<RequireQualifiedAccess>]
module internal WorkspaceCommandContext =
    let operation workspace (context: WorkspaceCommandContext) =
        { Workspace = workspace
          State = context.State
          Watcher = context.Watcher
          Coordinator = context.Coordinator
          PublicationGate = context.PublicationGate
          ActiveOperations = context.ActiveOperations
          WorkspaceRoot = context.WorkspaceRoot
          MaximumFrameBytes = context.MaximumFrameBytes
          RebuildWatcher = context.RebuildWatcher
          MutationNotifications = context.MutationNotifications }
