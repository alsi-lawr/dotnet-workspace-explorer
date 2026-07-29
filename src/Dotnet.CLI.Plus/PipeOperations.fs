namespace Dotnet.CLI.Plus

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Transport

type internal ExportOperationState(sessionToken: CancellationToken) =
    let cancellation = CancellationTokenSource.CreateLinkedTokenSource sessionToken

    let cancellationResponseFlushed =
        TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

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

type internal OperationNotificationWriter(publish: string -> unit) =
    inherit TextWriter()

    override _.Encoding = Encoding.UTF8

    override _.Write(value: string) =
        if not (String.IsNullOrEmpty value) then
            publish value

module internal PipeOperations =
    let completedOutcome
        (operation: ExportOperationState)
        (completionReserved: bool)
        (outcome: PublicOperationOutcome)
        =
        task {
            if completionReserved || operation.TryReserveCompletion() then
                return outcome
            else
                do! operation.WaitForCancellationResponseAsync()

                return
                    match outcome with
                    | PublicOperationOutcome.Failed(code, _) when code = "partial_recovery_required" ->
                        outcome
                    | _ -> PublicOperationOutcome.Cancelled
        }

    let writeExportBatch
        maximumFrameBytes
        descriptor
        operationId
        revision
        sequence
        (nodes: WorkspaceNode array)
        isFinalBatch
        ensureActive
        reserveFinal
        (sink: RpcNotificationSink)
        =
        task {
            let mutable nextSequence = sequence
            let mutable offset = 0
            let mutable emptyPending = nodes.Length = 0

            let encode count last =
                ensureActive ()

                let candidate =
                    ArraySegment<WorkspaceNode>(nodes, offset, count) :> seq<WorkspaceNode>

                PublicProtocol.exportChunk
                    descriptor
                    operationId
                    nextSequence
                    revision
                    candidate
                    last
                |> RpcEncodedNotification.Create

            while emptyPending || offset < nodes.Length do
                let remaining = nodes.Length - offset

                let count, encoded =
                    if emptyPending then
                        let value = encode 0 isFinalBatch
                        0, value
                    else
                        let whole = encode remaining isFinalBatch

                        if whole.Length <= maximumFrameBytes then
                            remaining, whole
                        else
                            let first = encode 1 false

                            if first.Length > maximumFrameBytes then
                                raise (
                                    RpcOutboundFrameTooLargeException(
                                        maximumFrameBytes,
                                        first.Length
                                    )
                                )

                            let mutable accepted = 1
                            let mutable selected = first
                            let mutable probe = 2

                            while probe < remaining do
                                let candidate = encode probe false

                                if candidate.Length <= maximumFrameBytes then
                                    accepted <- probe
                                    selected <- candidate
                                    probe <- probe * 2
                                else
                                    probe <- remaining

                            let mutable low = accepted + 1
                            let mutable high = min (remaining - 1) (probe - 1)

                            while low <= high do
                                let middle = low + (high - low) / 2
                                let candidate = encode middle false

                                if candidate.Length <= maximumFrameBytes then
                                    accepted <- middle
                                    selected <- candidate
                                    low <- middle + 1
                                else
                                    high <- middle - 1

                            accepted, selected

                if encoded.Length > maximumFrameBytes then
                    raise (RpcOutboundFrameTooLargeException(maximumFrameBytes, encoded.Length))

                let isFinalChunk = isFinalBatch && (emptyPending || offset + count = nodes.Length)

                if isFinalChunk then
                    let! reserved = reserveFinal ()

                    if not reserved then
                        raise (OperationCanceledException())

                ensureActive ()
                do! sink.WriteEncodedAsync encoded
                nextSequence <- nextSequence + 1
                emptyPending <- false
                offset <- offset + count

            return nextSequence
        }

    let createOutputPublisher
        maximumFrameBytes
        descriptor
        operationId
        revision
        (sink: RpcNotificationSink)
        (nextSequence: unit -> int)
        =
        let outputGate = obj ()

        let frame sequence stream value =
            PublicProtocol.operationOutput descriptor operationId sequence revision stream value

        let encodedSize sequence stream value =
            frame sequence stream value |> RpcCodec.encodeFrame |> _.Length

        let boundedSize stream value = encodedSize Int32.MaxValue stream value

        let scalarCountAt (value: string) offset =
            if
                Char.IsHighSurrogate value[offset]
                && offset + 1 < value.Length
                && Char.IsLowSurrogate value[offset + 1]
            then
                2
            else
                1

        let boundaryCount (value: string) offset count =
            if
                offset + count < value.Length
                && Char.IsHighSurrogate value[offset + count - 1]
                && Char.IsLowSurrogate value[offset + count]
            then
                count - 1
            else
                count

        let publish stream (value: string) =
            lock outputGate (fun () ->
                let mutable offset = 0

                while offset < value.Length do
                    let remaining = value.Length - offset

                    let accepted =
                        if
                            boundedSize stream (value.Substring(offset, remaining))
                            <= maximumFrameBytes
                        then
                            remaining
                        else
                            let mutable low = 1
                            let mutable high = remaining
                            let mutable best = 0

                            while low <= high do
                                let middle = low + (high - low) / 2
                                let count = boundaryCount value offset middle

                                if count = 0 then
                                    low <- middle + 1
                                elif
                                    boundedSize stream (value.Substring(offset, count))
                                    <= maximumFrameBytes
                                then
                                    best <- max best count
                                    low <- middle + 1
                                else
                                    high <- middle - 1

                            best

                    if accepted = 0 then
                        let count = scalarCountAt value offset
                        let size = boundedSize stream (value.Substring(offset, count))
                        raise (RpcOutboundFrameTooLargeException(maximumFrameBytes, size))

                    sink
                        .WriteAsync(
                            frame (nextSequence ()) stream (value.Substring(offset, accepted))
                        )
                        .GetAwaiter()
                        .GetResult()

                    offset <- offset + accepted)

        publish
