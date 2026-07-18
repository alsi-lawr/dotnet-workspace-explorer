namespace Dotnet.CLI.Plus

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport

type internal ExportOperationState(sessionToken: CancellationToken) =
    let cancellation = CancellationTokenSource.CreateLinkedTokenSource sessionToken

    let cancellationResponseFlushed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable state = 0 // 0 running, 1 cancellation reserved, 2 completion reserved, 3 complete
    let mutable cancellationCommitted = 0

    let cancelAndRelease () =
        if Interlocked.CompareExchange(&cancellationCommitted, 1, 0) = 0 then
            try
                cancellation.Cancel()
            finally
                cancellationResponseFlushed.TrySetResult() |> ignore

    member _.Token = cancellation.Token
    member _.IsCancellationReserved = Volatile.Read(&state) = 1

    member _.TryReserveCancellation() =
        Interlocked.CompareExchange(&state, 1, 0) = 0

    member _.TryReserveCompletion() =
        Interlocked.CompareExchange(&state, 2, 0) = 0

    member _.WaitForCancellationResponseAsync() = cancellationResponseFlushed.Task
    member _.CommitCancellationAfterResponse() = cancelAndRelease ()

    member _.CancelForShutdown() =
        if Interlocked.CompareExchange(&state, 1, 0) = 0 || Volatile.Read(&state) = 1 then
            cancelAndRelease ()

    member _.Complete() =
        Volatile.Write(&state, 3)
        cancellation.Dispose()

module internal Pipe =
    let private openWorkspace target cancellationToken =
        task {
            let! outcome = SolutionStore.OpenAsync(target, cancellationToken)

            return
                match outcome with
                | WorkspaceOutcome.Success workspace -> Ok workspace
                | WorkspaceOutcome.Failure failure -> Error(PublicProtocol.failureError failure)
        }

    let private chunkNodes maximumFrameBytes descriptor operationId revision (nodes: WorkspaceNode array) =
        let chunks = ResizeArray<WorkspaceNode array>()

        let encodedSize offset count =
            let candidate =
                ArraySegment<WorkspaceNode>(nodes, offset, count) :> seq<WorkspaceNode>

            PublicProtocol.exportChunk descriptor operationId chunks.Count revision candidate false
            |> RpcCodec.encodeFrame
            |> _.Length

        if nodes.Length = 0 then
            chunks.Add Array.empty
        else
            let mutable offset = 0

            while offset < nodes.Length do
                let remaining = nodes.Length - offset
                let firstSize = encodedSize offset 1

                if firstSize > maximumFrameBytes then
                    raise (RpcOutboundFrameTooLargeException(maximumFrameBytes, firstSize))

                let mutable accepted = 1
                let mutable probe = 2

                while probe <= remaining && encodedSize offset probe <= maximumFrameBytes do
                    accepted <- probe
                    probe <- probe * 2

                let mutable low = accepted + 1
                let mutable high = min remaining (probe - 1)

                while low <= high do
                    let middle = low + (high - low) / 2

                    if encodedSize offset middle <= maximumFrameBytes then
                        accepted <- middle
                        low <- middle + 1
                    else
                        high <- middle - 1

                chunks.Add(nodes[offset .. offset + accepted - 1])
                offset <- offset + accepted

        chunks.ToArray()

    let isPipeInvocation (arguments: string array) =
        match arguments with
        | [| ("solution" | "sln"); target; "--pipe" |] -> Some target
        | _ -> None

    let runAsync
        (target: string)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            let! opened =
                task {
                    let! workspace = openWorkspace target cancellationToken

                    match workspace with
                    | Ok value when
                        not value.WorkspaceDescriptor.IsReadOnly
                        && MutationCoordinator.RecoverStartup() = MutationRecoveryDisposition.PartialRecoveryRequired
                        ->
                        return
                            Error
                                { Code = "partial_recovery_required"
                                  Message =
                                    "partial_recovery_required: transaction recovery requires manual intervention."
                                  Data = None }
                    | _ -> return workspace
                }

            match opened with
            | Error rpcError ->
                do! error.WriteLineAsync($"dotnet-plus pipe startup failure: {rpcError.Message}")
                do! error.FlushAsync()
                return 64
            | Ok workspace ->
                let state = WorkspaceState.CreateProduction(target, workspace)
                let mutable watcherStarted = false
                let mutable maximumFrameBytes = RpcCodec.secureLimits.MaximumValueBytes
                let mutable maximumPageSize = 256
                use publicationGate = new SemaphoreSlim(1, 1)

                let watcher =
                    WorkspaceWatcher(state, 128, (fun () -> maximumFrameBytes), publicationGate)

                let prepareWatcher requestCancellationToken =
                    task {
                        watcher.Resume()
                        let! handoff = watcher.ActivateAsync requestCancellationToken

                        match handoff with
                        | WatcherHandoff.Complete -> return true
                        | WatcherHandoff.Revalidate _
                        | WatcherHandoff.RevalidateWorkspace ->
                            watcher.QueueActivationHandoff handoff
                            return true
                        | WatcherHandoff.Uncertain -> return false
                    }

                let rebuildWatcher requestCancellationToken =
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

                        let! _ = watcher.RebuildAndRevalidateAsync(publish, requestCancellationToken)
                        return notifications |> Seq.toList
                    }

                let activeExports =
                    ConcurrentDictionary<string, ExportOperationState>(StringComparer.Ordinal)

                let startWatcher active =
                    if watcherStarted then
                        None
                    elif not active then
                        None
                    else
                        watcherStarted <- true
                        Some(fun sink token -> watcher.StartAsync(sink, token))

                let initialize parameters _ =
                    task {
                        match PublicProtocol.parseInitialize parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            maximumFrameBytes <- request.MaximumFrameBytes
                            maximumPageSize <- request.MaximumPageSize
                            return Ok(PublicProtocol.initializeResult state.Descriptor state.Revision request)
                    }

                let dispatchCore (_: RpcSessionContext) methodName parameters requestCancellationToken =
                    task {
                        match PublicProtocol.parseRequest methodName parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            match request with
                            | PublicRequest.Root ->
                                let! active = prepareWatcher requestCancellationToken

                                if not active then
                                    return Error RpcErrors.internalError
                                else
                                    let! rooted = state.RootAsync requestCancellationToken

                                    match rooted with
                                    | Error rpcError -> return Error rpcError
                                    | Ok(revision, nodes) ->
                                        return
                                            Ok
                                                { Result = PublicProtocol.rootResult state.Descriptor revision nodes
                                                  Notifications = []
                                                  BackgroundWork = startWatcher true
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                            | PublicRequest.Children(parentId, pageSize, continuation) ->
                                let! active = prepareWatcher requestCancellationToken

                                if not active then
                                    return Error RpcErrors.internalError
                                else
                                    let! page =
                                        state.ChildrenAsync(
                                            parentId,
                                            pageSize,
                                            maximumPageSize,
                                            continuation,
                                            requestCancellationToken
                                        )

                                    match page with
                                    | Error rpcError -> return Error rpcError
                                    | Ok result ->
                                        let! stateNotifications, reset =
                                            match result.Delta |> Option.map PublicProtocol.workspaceDelta with
                                            | Some notification when
                                                (RpcCodec.encodeFrame notification).Length > maximumFrameBytes
                                                ->
                                                task {
                                                    let diagnostic =
                                                        WorkspaceDiagnostic.CreateSimple(
                                                            WorkspaceDiagnosticSeverity.Warning,
                                                            WorkspaceDiagnosticCode.Create "workspace.delta_pressure",
                                                            "The verified delta exceeded delivery capacity; request a fresh workspace graph.",
                                                            true,
                                                            CorrelationId.New()
                                                        )

                                                    let! reset = state.ResetAsync(diagnostic, requestCancellationToken)
                                                    return [ PublicProtocol.workspaceReset reset ], true
                                                }
                                            | Some notification -> Task.FromResult([ notification ], false)
                                            | None -> Task.FromResult([], false)

                                        if reset then
                                            watcher.Pause()

                                        let! handoffNotifications =
                                            if reset then
                                                Task.FromResult []
                                            else
                                                rebuildWatcher requestCancellationToken

                                        return
                                            Ok
                                                { Result =
                                                    PublicProtocol.childrenResult
                                                        state.Descriptor
                                                        result.Revision
                                                        result.ParentId
                                                        result.Nodes
                                                        result.NextToken
                                                  Notifications = stateNotifications @ handoffNotifications
                                                  BackgroundWork = if reset then None else startWatcher true
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                            | PublicRequest.Refresh expectedRevision ->
                                let! active = prepareWatcher requestCancellationToken

                                if not active then
                                    return Error RpcErrors.internalError
                                else
                                    let! refreshed = state.RefreshAsync(expectedRevision, requestCancellationToken)

                                    match refreshed with
                                    | Error rpcError -> return Error rpcError
                                    | Ok result ->
                                        let! effective =
                                            match result.Delta |> Option.map PublicProtocol.workspaceDelta with
                                            | Some notification when
                                                (RpcCodec.encodeFrame notification).Length > maximumFrameBytes
                                                ->
                                                task {
                                                    let diagnostic =
                                                        WorkspaceDiagnostic.CreateSimple(
                                                            WorkspaceDiagnosticSeverity.Warning,
                                                            WorkspaceDiagnosticCode.Create "workspace.delta_pressure",
                                                            "The verified delta exceeded delivery capacity; request a fresh workspace graph.",
                                                            true,
                                                            CorrelationId.New()
                                                        )

                                                    let! reset = state.ResetAsync(diagnostic, requestCancellationToken)

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
                                            watcher.Pause()

                                        let! handoffNotifications =
                                            if effective.Reset then
                                                Task.FromResult []
                                            else
                                                rebuildWatcher requestCancellationToken

                                        return
                                            Ok
                                                { Result =
                                                    PublicProtocol.refreshResult effective.Revision effective.Reset
                                                  Notifications = stateNotifications @ handoffNotifications
                                                  BackgroundWork = None
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                            | PublicRequest.Export ->
                                let snapshotRevision = state.Revision
                                let descriptor = state.Descriptor
                                let operationId = Guid.NewGuid().ToString("N")
                                let operation = ExportOperationState(requestCancellationToken)

                                if not (activeExports.TryAdd(operationId, operation)) then
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

                                                    let! exported = state.ExportAsync(snapshotRevision, linked.Token)

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
                                                            chunkNodes
                                                                maximumFrameBytes
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
                                                                "The workspace export exceeded the negotiated outbound frame limit."
                                                            )
                                                        )
                                                | :? InvalidOperationException ->
                                                    do!
                                                        reserveFailure (
                                                            PublicOperationOutcome.Failed(
                                                                "export_failed",
                                                                "The workspace export could not be completed safely."
                                                            )
                                                        )
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
                                                activeExports.TryRemove operationId |> ignore
                                                operation.Complete()
                                        }

                                    return
                                        Ok
                                            { Result = PublicProtocol.exportResult operationId snapshotRevision
                                              Notifications = []
                                              BackgroundWork = Some background
                                              AfterResponse = None
                                              StopAfterResponse = false }
                            | PublicRequest.Cancel operationId ->
                                let accepted, afterResponse =
                                    match activeExports.TryGetValue operationId with
                                    | true, operation when operation.TryReserveCancellation() ->
                                        true, Some operation.CommitCancellationAfterResponse
                                    | _ -> false, None

                                return
                                    Ok
                                        { Result = PublicProtocol.cancelResult accepted
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = afterResponse
                                          StopAfterResponse = false }
                            | PublicRequest.Shutdown ->
                                for operation in activeExports.Values do
                                    operation.CancelForShutdown()

                                return
                                    Ok
                                        { Result = PublicProtocol.shutdownResult
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = None
                                          StopAfterResponse = true }
                            | PublicRequest.CommandList _
                            | PublicRequest.CommandDescribe _ ->
                                return Error(RpcErrors.unsupported "Command discovery is not implemented until T-007.")
                            | PublicRequest.CommandPreview _
                            | PublicRequest.CommandExecute _ ->
                                if state.Descriptor.IsReadOnly then
                                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                                else
                                    return
                                        Error(
                                            RpcErrors.unsupported "Workspace mutations are not implemented until T-007."
                                        )
                    }

                let dispatch
                    (context: RpcSessionContext)
                    (methodName: string)
                    (parameters: RpcValue)
                    (requestCancellationToken: CancellationToken)
                    =
                    task {
                        let serialized =
                            match PublicProtocol.parseRequest methodName parameters with
                            | Ok PublicRequest.Root
                            | Ok(PublicRequest.Children _)
                            | Ok(PublicRequest.Refresh _) -> true
                            | _ -> false

                        if serialized then
                            do! publicationGate.WaitAsync requestCancellationToken

                        try
                            let! result = dispatchCore context methodName parameters requestCancellationToken

                            return
                                match result with
                                | Ok value when serialized ->
                                    Ok
                                        { value with
                                            AfterResponse = Some(fun () -> publicationGate.Release() |> ignore) }
                                | _ ->
                                    if serialized then
                                        publicationGate.Release() |> ignore

                                    result
                        with exceptionValue ->
                            if serialized then
                                publicationGate.Release() |> ignore

                            return raise exceptionValue
                    }

                let configuration =
                    { Profile = RpcProfile.publicProfile
                      Limits = RpcCodec.secureLimits
                      GetOutboundFrameLimit = fun () -> maximumFrameBytes
                      Initialize = initialize
                      Dispatch = dispatch }

                try
                    let! result = RpcSession.runAsync configuration input output error cancellationToken
                    do! state.DisposeAsync()
                    return result
                with exceptionValue ->
                    do! state.DisposeAsync()
                    return raise exceptionValue
        }
