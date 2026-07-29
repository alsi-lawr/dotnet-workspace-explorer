namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3511"

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

type internal WorkspaceRpcContext =
    { State: WorkspaceIndex
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
      RebuildWatcher: CancellationToken -> Task<RpcFrame list>
      MutationNotifications: WorkspaceProjectInvalidationResult -> RpcFrame list }
