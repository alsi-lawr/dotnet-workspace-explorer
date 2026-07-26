namespace Dotnet.CLI.Plus

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Transport

type internal WorkspaceRequestContext =
    { State: WorkspaceState
      Watcher: WorkspaceWatcher
      ActiveOperations: ConcurrentDictionary<string, ExportOperationState>
      MaximumFrameBytes: unit -> int
      MaximumPageSize: unit -> int
      StartWatcher: bool -> (RpcNotificationSink -> CancellationToken -> Task<unit>) option }

module internal PipeWorkspaceRequests =
    let mutationNotifications =
        function
        | WorkspaceInvalidationResult.Delta delta -> [ PublicProtocol.workspaceDelta delta ]
        | WorkspaceInvalidationResult.Reset reset -> [ PublicProtocol.workspaceReset reset ]
        | WorkspaceInvalidationResult.None -> []

    let prepareWatcher (context: WorkspaceRequestContext) cancellationToken =
        task {
            context.Watcher.Resume()
            let! handoff = context.Watcher.ActivateAsync cancellationToken

            match handoff with
            | WatcherHandoff.Complete -> return true
            | WatcherHandoff.Revalidate _
            | WatcherHandoff.RevalidateWorkspace ->
                context.Watcher.QueueActivationHandoff handoff
                return true
            | WatcherHandoff.Uncertain -> return false
        }

    let rebuildWatcher (context: WorkspaceRequestContext) cancellationToken =
        task {
            let notifications = ResizeArray<RpcFrame>()

            let publish handoff =
                match handoff with
                | WorkspaceInvalidationResult.Delta delta ->
                    notifications.Add(PublicProtocol.workspaceDelta delta)
                | WorkspaceInvalidationResult.Reset reset ->
                    notifications.Add(PublicProtocol.workspaceReset reset)
                | WorkspaceInvalidationResult.None -> ()

                Task.FromResult(())

            let! _ = context.Watcher.RebuildAndRevalidateAsync(publish, cancellationToken)
            return notifications |> Seq.toList
        }

    let resetForFramePressure (state: WorkspaceState) cancellationToken =
        task {
            let diagnostic =
                WorkspaceDiagnostic.CreateSimple(
                    WorkspaceDiagnosticSeverity.Warning,
                    WorkspaceDiagnosticCode.Create "workspace.delta_pressure",
                    "Verified delta exceeded capacity; request a fresh workspace graph.",
                    true,
                    CorrelationId.New()
                )

            return! state.ResetAsync(diagnostic, cancellationToken)
        }

    let private dispatchRoot context cancellationToken =
        task {
            let! active = prepareWatcher context cancellationToken

            if not active then
                return Error RpcErrors.internalError
            else
                let! rooted = context.State.RootAsync cancellationToken

                match rooted with
                | Error rpcError -> return Error rpcError
                | Ok(revision, nodes) ->
                    return
                        Ok
                            { Result =
                                PublicProtocol.rootResult context.State.Descriptor revision nodes
                              Notifications = []
                              BackgroundWork = context.StartWatcher true
                              AfterResponse = None
                              StopAfterResponse = false }
        }

    let private dispatchChildren context parentId pageSize continuation cancellationToken =
        task {
            let! active = prepareWatcher context cancellationToken

            if not active then
                return Error RpcErrors.internalError
            else
                let! page =
                    context.State.ChildrenAsync(
                        parentId,
                        pageSize,
                        context.MaximumPageSize(),
                        continuation,
                        cancellationToken
                    )

                match page with
                | Error rpcError -> return Error rpcError
                | Ok result ->
                    let! stateNotifications, reset =
                        match result.Delta |> Option.map PublicProtocol.workspaceDelta with
                        | Some notification when
                            (RpcCodec.encodeFrame notification).Length > context.MaximumFrameBytes()
                            ->
                            task {
                                let! reset = resetForFramePressure context.State cancellationToken
                                return [ PublicProtocol.workspaceReset reset ], true
                            }
                        | Some notification -> Task.FromResult([ notification ], false)
                        | None -> Task.FromResult([], false)

                    if reset then
                        context.Watcher.Pause()

                    let! handoffNotifications =
                        if reset then
                            Task.FromResult []
                        else
                            rebuildWatcher context cancellationToken

                    return
                        Ok
                            { Result =
                                PublicProtocol.childrenResult
                                    context.State.Descriptor
                                    result.Revision
                                    result.ParentId
                                    result.Nodes
                                    result.NextToken
                              Notifications = stateNotifications @ handoffNotifications
                              BackgroundWork = if reset then None else context.StartWatcher true
                              AfterResponse = None
                              StopAfterResponse = false }
        }

    let private dispatchRefresh context expectedRevision cancellationToken =
        task {
            let! active = prepareWatcher context cancellationToken

            if not active then
                return Error RpcErrors.internalError
            else
                let! refreshed = context.State.RefreshAsync(expectedRevision, cancellationToken)

                match refreshed with
                | Error rpcError -> return Error rpcError
                | Ok result ->
                    let! effective =
                        match result.Delta |> Option.map PublicProtocol.workspaceDelta with
                        | Some notification when
                            (RpcCodec.encodeFrame notification).Length > context.MaximumFrameBytes()
                            ->
                            task {
                                let! reset = resetForFramePressure context.State cancellationToken

                                return
                                    { Revision = reset.Revision.Value
                                      Reset = true
                                      Delta = None
                                      ResetEvent = Some reset
                                      Diagnostics = reset.Diagnostics }
                            }
                        | _ -> Task.FromResult result

                    let stateNotifications =
                        [ effective.Delta |> Option.map PublicProtocol.workspaceDelta
                          effective.ResetEvent |> Option.map PublicProtocol.workspaceReset ]
                        |> List.choose id

                    if effective.Reset then
                        context.Watcher.Pause()

                    let! handoffNotifications =
                        if effective.Reset then
                            Task.FromResult []
                        else
                            rebuildWatcher context cancellationToken

                    return
                        Ok
                            { Result =
                                PublicProtocol.refreshResult effective.Revision effective.Reset
                              Notifications = stateNotifications @ handoffNotifications
                              BackgroundWork = None
                              AfterResponse = None
                              StopAfterResponse = false }
        }

    let private dispatchExport (context: WorkspaceRequestContext) cancellationToken =
        task {
            let snapshotRevision = context.State.Revision
            let descriptor = context.State.Descriptor
            let operationId = Guid.NewGuid().ToString "N"
            let operation = ExportOperationState cancellationToken

            if not (context.ActiveOperations.TryAdd(operationId, operation)) then
                operation.Complete()
                return Error RpcErrors.internalError
            else
                let background (sink: RpcNotificationSink) sessionToken =
                    task {
                        let mutable sequence = 0
                        let mutable outcome = PublicOperationOutcome.Succeeded

                        let reserveFailure failure =
                            task {
                                if operation.TryReserveCompletion() then
                                    outcome <- failure
                                else
                                    do! operation.WaitForCancellationResponseAsync()
                                    outcome <- PublicOperationOutcome.Cancelled
                            }

                        try
                            try
                                use linked =
                                    CancellationTokenSource.CreateLinkedTokenSource(
                                        operation.Token,
                                        sessionToken
                                    )

                                let! exported =
                                    context.State.ExportAsync(snapshotRevision, linked.Token)

                                match exported with
                                | Error rpcError when rpcError.Code = "cancelled" ->
                                    raise (OperationCanceledException())
                                | Error rpcError ->
                                    do!
                                        reserveFailure (
                                            PublicOperationOutcome.Failed(
                                                rpcError.Code,
                                                rpcError.Message
                                            )
                                        )
                                | Ok snapshot ->
                                    let chunks =
                                        PipeOperations.chunkExportNodes
                                            (context.MaximumFrameBytes())
                                            snapshot.Descriptor
                                            operationId
                                            snapshot.Revision
                                            (snapshot.Nodes |> Seq.toArray)

                                    for index in 0 .. chunks.Length - 1 do
                                        if operation.IsCancellationReserved then
                                            raise (OperationCanceledException())

                                        linked.Token.ThrowIfCancellationRequested()

                                        do!
                                            sink.WriteAsync(
                                                PublicProtocol.exportChunk
                                                    snapshot.Descriptor
                                                    operationId
                                                    sequence
                                                    snapshot.Revision
                                                    chunks[index]
                                                    (index = chunks.Length - 1)
                                            )

                                        sequence <- sequence + 1

                                    if operation.TryReserveCompletion() then
                                        outcome <- PublicOperationOutcome.Succeeded
                                    else
                                        do! operation.WaitForCancellationResponseAsync()
                                        outcome <- PublicOperationOutcome.Cancelled
                            with
                            | :? OperationCanceledException ->
                                if operation.IsCancellationReserved then
                                    do! operation.WaitForCancellationResponseAsync()

                                outcome <- PublicOperationOutcome.Cancelled
                            | :? RpcOutboundFrameTooLargeException ->
                                do!
                                    reserveFailure (
                                        PublicOperationOutcome.Failed(
                                            "response_too_large",
                                            "Workspace export exceeded the outbound frame limit."
                                        )
                                    )
                            | :? InvalidOperationException
                            | :? ArgumentException
                            | :? FormatException ->
                                do!
                                    reserveFailure (
                                        PublicOperationOutcome.Failed(
                                            "export_failed",
                                            "The workspace export could not be completed safely."
                                        )
                                    )

                            do!
                                sink.WriteAsync(
                                    PublicProtocol.operationCompleted
                                        descriptor
                                        operationId
                                        sequence
                                        snapshotRevision
                                        outcome
                                )
                        finally
                            context.ActiveOperations.TryRemove operationId |> ignore
                            operation.Complete()
                    }

                return
                    Ok
                        { Result = PublicProtocol.exportResult operationId snapshotRevision
                          Notifications = []
                          BackgroundWork = Some background
                          AfterResponse = None
                          StopAfterResponse = false }
        }

    let private dispatchCancel (context: WorkspaceRequestContext) operationId =
        let accepted, afterResponse =
            match context.ActiveOperations.TryGetValue operationId with
            | true, operation when operation.TryReserveCancellation() ->
                true, Some operation.CommitCancellationAfterResponse
            | _ -> false, None

        { Result = PublicProtocol.cancelResult accepted
          Notifications = []
          BackgroundWork = None
          AfterResponse = afterResponse
          StopAfterResponse = false }
        |> Ok
        |> Task.FromResult

    let private dispatchShutdown (context: WorkspaceRequestContext) =
        for operation in context.ActiveOperations.Values do
            operation.CancelForShutdown()

        { Result = PublicProtocol.shutdownResult
          Notifications = []
          BackgroundWork = None
          AfterResponse = None
          StopAfterResponse = true }
        |> Ok
        |> Task.FromResult

    let private someAsync operation =
        task {
            let! result = operation
            return Some result
        }

    let tryDispatch (context: WorkspaceRequestContext) request cancellationToken =
        match request with
        | PublicRequest.Root -> dispatchRoot context cancellationToken |> someAsync
        | PublicRequest.Children(parentId, pageSize, continuation) ->
            dispatchChildren context parentId pageSize continuation cancellationToken
            |> someAsync
        | PublicRequest.Refresh expectedRevision ->
            dispatchRefresh context expectedRevision cancellationToken |> someAsync
        | PublicRequest.Export -> dispatchExport context cancellationToken |> someAsync
        | PublicRequest.Cancel operationId -> dispatchCancel context operationId |> someAsync
        | PublicRequest.Shutdown -> dispatchShutdown context |> someAsync
        | PublicRequest.CommandList _
        | PublicRequest.CommandDescribe _
        | PublicRequest.CommandPreview _
        | PublicRequest.CommandExecute _ -> Task.FromResult None
