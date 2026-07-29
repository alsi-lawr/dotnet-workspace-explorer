namespace Dotnet.CLI.Plus

#nowarn "3511"

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Transport

type internal CommandRequestContext =
    { State: WorkspaceState
      Watcher: WorkspaceWatcher
      Coordinator: MutationCoordinator
      PublicationGate: SemaphoreSlim
      ActiveOperations: ConcurrentDictionary<string, ExportOperationState>
      WorkspaceRoot: string
      MaximumFrameBytes: unit -> int
      RebuildWatcher: CancellationToken -> Task<RpcFrame list>
      MutationNotifications: WorkspaceInvalidationResult -> RpcFrame list }
