namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.IO
open System.Threading

module internal WorkspaceRpcServer =
    let private openWorkspace target cancellationToken =
        task {
            let! outcome = SolutionWorkspaceReader.OpenAsync(target, cancellationToken)

            return
                match outcome with
                | Success workspace -> Ok workspace
                | Failure failure -> Error(WorkspaceRpcResponses.failureError failure)
        }

    let runAsync
        (target: string)
        (exportCapacity: int)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            let! opened = openWorkspace target cancellationToken

            match opened with
            | Error rpcError ->
                do!
                    error.WriteLineAsync
                        $"dotnet-workspace-explorer workspace RPC startup failure: {rpcError.Message}"

                do! error.FlushAsync()
                return 64
            | Ok workspace ->
                let state = WorkspaceIndex.CreateProduction(target, workspace, exportCapacity)
                let mutable watcherStarted = false
                let mutable maximumFrameBytes = MessagePackRpcCodec.secureLimits.MaximumValueBytes
                let mutable maximumPageSize = 256
                use publicationGate = new SemaphoreSlim(1, 1)

                let watcher =
                    WorkspaceIndexWatcher(
                        state,
                        128,
                        (fun () -> maximumFrameBytes),
                        publicationGate
                    )

                let coordinator =
                    let root =
                        Path.GetDirectoryName workspace.SolutionPath.Value
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    WorkspaceEditTransaction.CreateProduction(
                        WorkspaceArtifactPath.Create root,
                        fun () -> WorkspaceRevision.Create state.Revision
                    )

                let workspaceRoot =
                    Path.GetDirectoryName workspace.SolutionPath.Value
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory())

                let activeOperations =
                    ConcurrentDictionary<string, WorkspaceExportOperation> StringComparer.Ordinal

                let startWatcher active =
                    if watcherStarted then
                        None
                    elif not active then
                        None
                    else
                        watcherStarted <- true
                        Some(fun sink token -> watcher.StartAsync(sink, token))

                let workspaceRequestContext =
                    { State = state
                      Watcher = watcher
                      ActiveOperations = activeOperations
                      MaximumFrameBytes = fun () -> maximumFrameBytes
                      MaximumPageSize = fun () -> maximumPageSize
                      StartWatcher = startWatcher }

                let commandRequestContext =
                    { State = state
                      Watcher = watcher
                      Coordinator = coordinator
                      PublicationGate = publicationGate
                      ActiveOperations = activeOperations
                      WorkspaceRoot = workspaceRoot
                      MaximumFrameBytes = fun () -> maximumFrameBytes
                      RebuildWatcher =
                        WorkspaceNavigationRequests.rebuildWatcher workspaceRequestContext
                      MutationNotifications = WorkspaceNavigationRequests.mutationNotifications }

                let initialize parameters _ =
                    task {
                        match WorkspaceRpc.parseInitialize parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            maximumFrameBytes <- request.MaximumFrameBytes
                            maximumPageSize <- request.MaximumPageSize

                            return
                                Ok(
                                    WorkspaceRpcResponses.initializeResult
                                        state.Descriptor
                                        state.Revision
                                        request
                                )
                    }

                let dispatchCore
                    (_: RpcSessionContext)
                    methodName
                    parameters
                    requestCancellationToken
                    =
                    task {
                        match WorkspaceRpc.parseRequest methodName parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            let! workspaceResult =
                                WorkspaceNavigationRequests.tryDispatch
                                    workspaceRequestContext
                                    request
                                    requestCancellationToken

                            match workspaceResult with
                            | Some result -> return result
                            | None ->
                                let! exportResult =
                                    WorkspaceExportRequests.tryDispatch
                                        workspaceRequestContext
                                        request
                                        requestCancellationToken

                                match exportResult with
                                | Some result -> return result
                                | None ->
                                    match request with
                                    | WorkspaceRpcRequest.CommandList _
                                    | WorkspaceRpcRequest.CommandDescribe _
                                    | WorkspaceRpcRequest.CommandPreview _
                                    | WorkspaceRpcRequest.CommandExecute _ ->
                                        return!
                                            WorkspaceCommandRequests.dispatch
                                                commandRequestContext
                                                request
                                                requestCancellationToken
                                    | _ -> return Error RpcErrors.internalError
                    }

                let dispatch
                    (context: RpcSessionContext)
                    (methodName: string)
                    (parameters: RpcValue)
                    (requestCancellationToken: CancellationToken)
                    =
                    task {
                        let serialized =
                            match WorkspaceRpc.parseRequest methodName parameters with
                            | Ok WorkspaceRpcRequest.Root
                            | Ok(WorkspaceRpcRequest.Children _)
                            | Ok(WorkspaceRpcRequest.Refresh _)
                            | Ok(WorkspaceRpcRequest.CommandList _)
                            | Ok(WorkspaceRpcRequest.CommandDescribe _)
                            | Ok(WorkspaceRpcRequest.CommandPreview _)
                            | Ok(WorkspaceRpcRequest.CommandExecute _) -> true
                            | _ -> false

                        let mutable releaseOnExit = false

                        try
                            if serialized then
                                do! publicationGate.WaitAsync requestCancellationToken
                                releaseOnExit <- true

                            let! result =
                                dispatchCore context methodName parameters requestCancellationToken

                            return
                                match result with
                                | Ok value when serialized ->
                                    let release () = publicationGate.Release() |> ignore
                                    releaseOnExit <- false

                                    Ok
                                        { value with
                                            AfterResponse = Some release }
                                | _ -> result
                        finally
                            if releaseOnExit then
                                publicationGate.Release() |> ignore
                    }

                let configuration =
                    { Profile = WorkspaceRpcProfile.current
                      Limits = MessagePackRpcCodec.secureLimits
                      GetOutboundFrameLimit = fun () -> maximumFrameBytes
                      Initialize = initialize
                      Dispatch = dispatch }

                try
                    let! result =
                        RpcSession.runAsync configuration input output error cancellationToken

                    do! state.DisposeAsync()
                    return result
                with exceptionValue ->
                    do! state.DisposeAsync()
                    return raise exceptionValue
        }
