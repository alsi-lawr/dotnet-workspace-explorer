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

    let responseFlushed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable state = 0 // 0 running, 1 cancelled, 2 completed

    member _.Token = cancellation.Token
    member _.IsCancellationReserved = Volatile.Read(&state) = 1

    member _.TryReserveCancellation() =
        Interlocked.CompareExchange(&state, 1, 0) = 0

    member _.TryReserveCompletion() =
        Interlocked.CompareExchange(&state, 2, 0) = 0

    member _.WaitForCancellationResponseAsync() = responseFlushed.Task

    member _.CommitCancellationAfterResponse() =
        if Volatile.Read(&state) = 1 then
            try
                cancellation.Cancel()
            finally
                responseFlushed.TrySetResult() |> ignore

    member this.CancelForShutdown() =
        if this.TryReserveCancellation() || this.IsCancellationReserved then
            this.CommitCancellationAfterResponse()

    member _.Complete() = cancellation.Dispose()

module internal PipeTestHooks =
    let canonicalSignature (groups: seq<seq<string>>) : byte array =
        groups
        |> Seq.collect (fun group ->
            seq {
                yield string (Seq.length group)
                yield! group
            })
        |> WorkspaceStateSupport.canonicalBytes

    let nextRevision (revision: int64) (before: byte array) (after: byte array) =
        if before.AsSpan().SequenceEqual after then
            revision
        else
            revision + 1L

type private WorkspaceWatcher(state: WorkspaceState) =
    let pending = ConcurrentDictionary<string, byte>(StringComparer.Ordinal)
    let mutable overflowed = 0
    let mutable started = 0
    let mutable watchers: FileSystemWatcher array = Array.empty

    let reset () =
        Interlocked.Exchange(&overflowed, 1) |> ignore

    let enqueue path =
        if not (String.IsNullOrWhiteSpace path) then
            pending[path] <- 0uy

    let disposeWatchers () =
        for watcher in watchers do
            watcher.Dispose()

        watchers <- Array.empty

    member _.Rebuild() =
        disposeWatchers ()

        watchers <-
            state.WatchDirectories()
            |> Array.map (fun directory ->
                let watcher = new FileSystemWatcher(directory, "*")
                watcher.IncludeSubdirectories <- true

                watcher.NotifyFilter <-
                    NotifyFilters.FileName
                    ||| NotifyFilters.LastWrite
                    ||| NotifyFilters.DirectoryName

                watcher.Changed.Add(fun args -> enqueue args.FullPath)
                watcher.Created.Add(fun args -> enqueue args.FullPath)
                watcher.Deleted.Add(fun args -> enqueue args.FullPath)
                watcher.Renamed.Add(fun args -> enqueue args.FullPath)
                watcher.Error.Add(fun _ -> reset ())
                watcher.EnableRaisingEvents <- true
                watcher)

    member this.StartAsync(sink: RpcNotificationSink, sessionToken: CancellationToken) =
        task {
            if Interlocked.CompareExchange(&started, 1, 0) = 0 then
                this.Rebuild()

                try
                    try
                        while not sessionToken.IsCancellationRequested do
                            do! Task.Delay(75, sessionToken)

                            if Interlocked.Exchange(&overflowed, 0) <> 0 then
                                let reset =
                                    { WorkspaceId = state.Descriptor.WorkspaceId
                                      Revision = WorkspaceRevision.Create state.Revision
                                      Diagnostics =
                                        [ WorkspaceDiagnostic.CreateSimple(
                                              WorkspaceDiagnosticSeverity.Warning,
                                              WorkspaceDiagnosticCode.Create "workspace.watch_overflow",
                                              "File watching overflowed; request a fresh workspace graph.",
                                              true,
                                              CorrelationId.New()
                                          ) ]
                                        |> System.Collections.Immutable.ImmutableArray.CreateRange }

                                do! sink.WriteAsync(PublicProtocol.workspaceReset reset)
                            elif not pending.IsEmpty then
                                let paths = ResizeArray<WorkspaceArtifactPath>()

                                for KeyValue(path, _) in pending do
                                    if pending.TryRemove path |> fst then
                                        paths.Add(WorkspaceArtifactPath.Create path)

                                let! outcome = state.InvalidateAsync(paths, sessionToken)

                                match outcome with
                                | WorkspaceWatchOutcome.Delta delta ->
                                    do! sink.WriteAsync(PublicProtocol.workspaceDelta delta)
                                | WorkspaceWatchOutcome.Reset reset ->
                                    do! sink.WriteAsync(PublicProtocol.workspaceReset reset)
                                | WorkspaceWatchOutcome.None -> ()
                    with :? OperationCanceledException ->
                        ()
                finally
                    disposeWatchers ()
        }

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

                if encodedSize [| node |] > maximumFrameBytes then
                    raise (RpcOutboundFrameTooLargeException(maximumFrameBytes, encodedSize [| node |]))

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
                let! state = WorkspaceState.CreateAsync(target, workspace, cancellationToken)
                let watcher = WorkspaceWatcher(state)
                let mutable watcherStarted = false
                let mutable maximumFrameBytes = RpcCodec.secureLimits.MaximumValueBytes
                let mutable maximumPageSize = 256

                let activeExports =
                    ConcurrentDictionary<string, ExportOperationState>(StringComparer.Ordinal)

                let startWatcher () =
                    if watcherStarted then
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
                                let! revision, nodes = state.RootAsync requestCancellationToken

                                return
                                    Ok
                                        { Result = PublicProtocol.rootResult state.Descriptor revision nodes
                                          Notifications = []
                                          BackgroundWork = startWatcher ()
                                          AfterResponse = None
                                          StopAfterResponse = false }
                            | PublicRequest.Children(parentId, pageSize, continuation) ->
                                let! result =
                                    state.ChildrenAsync(
                                        parentId,
                                        pageSize,
                                        maximumPageSize,
                                        continuation,
                                        requestCancellationToken
                                    )

                                match result with
                                | Error rpcError -> return Error rpcError
                                | Ok(revision, parent, nodes, next, delta) ->
                                    watcher.Rebuild()

                                    let notifications =
                                        delta
                                        |> Option.map (PublicProtocol.workspaceDelta >> List.singleton)
                                        |> Option.defaultValue []

                                    return
                                        Ok
                                            { Result =
                                                PublicProtocol.childrenResult
                                                    state.Descriptor
                                                    revision
                                                    parent
                                                    nodes
                                                    next
                                              Notifications = notifications
                                              BackgroundWork = startWatcher ()
                                              AfterResponse = None
                                              StopAfterResponse = false }
                            | PublicRequest.Refresh expectedRevision ->
                                let! refreshed = state.RefreshAsync(expectedRevision, requestCancellationToken)

                                match refreshed with
                                | Error rpcError -> return Error rpcError
                                | Ok(revision, changed, _) ->
                                    watcher.Rebuild()

                                    return
                                        Ok
                                            { Result = PublicProtocol.refreshResult revision changed
                                              Notifications = []
                                              BackgroundWork = startWatcher ()
                                              AfterResponse = None
                                              StopAfterResponse = false }
                            | PublicRequest.Export ->
                                let! exported = state.ExportAsync requestCancellationToken

                                match exported with
                                | Error rpcError -> return Error rpcError
                                | Ok(snapshotRevision, nodes) ->
                                    watcher.Rebuild()
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

                                                try
                                                    use linked =
                                                        CancellationTokenSource.CreateLinkedTokenSource(
                                                            operation.Token,
                                                            sessionToken
                                                        )

                                                    let chunks =
                                                        chunkNodes
                                                            maximumFrameBytes
                                                            state.Descriptor
                                                            operationId
                                                            snapshotRevision
                                                            (nodes |> Seq.toArray)

                                                    for index in 0 .. chunks.Length - 1 do
                                                        linked.Token.ThrowIfCancellationRequested()

                                                        do!
                                                            sink.WriteAsync(
                                                                PublicProtocol.exportChunk
                                                                    state.Descriptor
                                                                    operationId
                                                                    sequence
                                                                    snapshotRevision
                                                                    chunks[index]
                                                                    (index = chunks.Length - 1)
                                                            )

                                                        sequence <- sequence + 1
                                                        do! Task.Yield()
                                                        do! Task.Delay(1, linked.Token)

                                                    if not (operation.TryReserveCompletion()) then
                                                        outcome <- PublicOperationOutcome.Cancelled
                                                with
                                                | :? OperationCanceledException ->
                                                    outcome <- PublicOperationOutcome.Cancelled
                                                | :? RpcOutboundFrameTooLargeException ->
                                                    outcome <-
                                                        PublicOperationOutcome.Failed(
                                                            "response_too_large",
                                                            "The workspace export exceeded the negotiated outbound frame limit."
                                                        )

                                                do!
                                                    sink.WriteAsync(
                                                        PublicProtocol.operationCompleted
                                                            state.Descriptor
                                                            operationId
                                                            sequence
                                                            snapshotRevision
                                                            outcome
                                                    )

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
                                let accepted, after =
                                    match activeExports.TryGetValue operationId with
                                    | true, operation when operation.TryReserveCancellation() ->
                                        true, Some operation.CommitCancellationAfterResponse
                                    | _ -> false, None

                                return
                                    Ok
                                        { Result = PublicProtocol.cancelResult accepted
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = after
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

                let! result = RpcSession.runAsync configuration input output error cancellationToken
                do! state.DisposeAsync()
                return result
        }
