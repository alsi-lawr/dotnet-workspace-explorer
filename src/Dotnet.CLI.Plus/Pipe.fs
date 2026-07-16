namespace Dotnet.CLI.Plus

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.IO
open System.Text
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

module internal PipeTestHooks =
    let canonicalSignature (groups: seq<seq<string>>) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        let values = groups |> Seq.map Seq.toArray |> Seq.toArray
        writer.Write values.Length

        for group in values do
            writer.Write group.Length

            for value in group do
                let bytes = Encoding.UTF8.GetBytes value
                writer.Write bytes.Length
                writer.Write bytes

        writer.Flush()
        stream.ToArray()

    let nextRevision (revision: int64) (before: byte array) (after: byte array) =
        if before.AsSpan().SequenceEqual after then
            revision
        else
            revision + 1L

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
        let current = ResizeArray<WorkspaceNode>()

        let encodedSize candidate =
            PublicProtocol.exportChunk descriptor operationId chunks.Count revision candidate false
            |> RpcCodec.encodeFrame
            |> _.Length

        let flush () =
            if current.Count > 0 then
                chunks.Add(current.ToArray())
                current.Clear()

        for node in nodes do
            let candidate = Array.append (current.ToArray()) [| node |]

            if encodedSize candidate <= maximumFrameBytes then
                current.Add node
            else
                flush ()
                let actual = encodedSize [| node |]

                if actual > maximumFrameBytes then
                    raise (RpcOutboundFrameTooLargeException(maximumFrameBytes, actual))

                current.Add node

        flush ()

        if chunks.Count = 0 then
            chunks.Add Array.empty

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
            let! opened = openWorkspace target cancellationToken

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
                let watcher = WorkspaceWatcher(state, 128, fun () -> maximumFrameBytes)

                let activeExports =
                    ConcurrentDictionary<string, ExportOperationState>(StringComparer.Ordinal)

                let startWatcher resume =
                    if resume then
                        watcher.Resume()

                    if watcherStarted then
                        None
                    elif not resume then
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

                let dispatch (_: RpcSessionContext) methodName parameters requestCancellationToken =
                    task {
                        match PublicProtocol.parseRequest methodName parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            match request with
                            | PublicRequest.Root ->
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
                                    let notifications =
                                        result.Delta
                                        |> Option.map (PublicProtocol.workspaceDelta >> List.singleton)
                                        |> Option.defaultValue []

                                    if watcherStarted then
                                        do! watcher.RequestRebuildAsync()

                                    return
                                        Ok
                                            { Result =
                                                PublicProtocol.childrenResult
                                                    state.Descriptor
                                                    result.Revision
                                                    result.ParentId
                                                    result.Nodes
                                                    result.NextToken
                                              Notifications = notifications
                                              BackgroundWork = startWatcher true
                                              AfterResponse = None
                                              StopAfterResponse = false }
                            | PublicRequest.Refresh expectedRevision ->
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

                                    let notifications =
                                        [ effective.Delta |> Option.map PublicProtocol.workspaceDelta
                                          effective.ResetEvent |> Option.map PublicProtocol.workspaceReset ]
                                        |> List.choose id

                                    if effective.Reset then
                                        watcher.Pause()
                                    elif watcherStarted then
                                        watcher.Resume()
                                        do! watcher.RequestRebuildAsync()

                                    return
                                        Ok
                                            { Result = PublicProtocol.refreshResult effective.Revision effective.Reset
                                              Notifications = notifications
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
