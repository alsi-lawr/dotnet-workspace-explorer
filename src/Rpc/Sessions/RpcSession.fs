namespace Dotnet.WorkspaceExplorer.Rpc

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3511"

open System
open System.IO
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module RpcSession =
    let private protocolFailure = 65
    let private cancelled = 130

    type private ResponseWriteOutcome =
        | OriginalResponse
        | SizeErrorResponse

    type private SynchronizedWriter
        (
            output: Stream,
            getOutboundLimit: unit -> int,
            hardLimit: int,
            cancellationToken: CancellationToken
        ) =
        let gate = new SemaphoreSlim(1, 1)

        let outboundLimit () =
            getOutboundLimit () |> max 1 |> min hardLimit

        let writeBytes (bytes: byte array) =
            task {
                do! output.WriteAsync(bytes, cancellationToken)
                do! output.FlushAsync cancellationToken
            }

        member _.WriteResponseAsync(messageId: uint32, error: RpcError option, result: RpcValue) =
            task {
                do! gate.WaitAsync cancellationToken

                try
                    let limit = outboundLimit ()

                    let encoded =
                        MessagePackRpcCodec.encodeFrame (Response(messageId, error, result))

                    if encoded.Length <= limit then
                        do! writeBytes encoded
                        return OriginalResponse
                    else
                        let fallback =
                            MessagePackRpcCodec.encodeFrame (
                                Response(messageId, Some RpcErrors.responseTooLarge, RpcValue.Nil)
                            )

                        if fallback.Length > limit then
                            raise (RpcFrameLimitExceededException(limit, fallback.Length))

                        do! writeBytes fallback
                        return SizeErrorResponse
                finally
                    gate.Release() |> ignore
            }

        member _.WriteNotificationAsync(frame: RpcFrame) =
            task {
                do! gate.WaitAsync cancellationToken

                try
                    let limit = outboundLimit ()
                    let encoded = MessagePackRpcCodec.encodeFrame frame

                    if encoded.Length > limit then
                        raise (RpcFrameLimitExceededException(limit, encoded.Length))

                    do! writeBytes encoded
                finally
                    gate.Release() |> ignore
            }

        member _.WriteEncodedNotificationAsync(notification: EncodedRpcNotification) =
            task {
                do! gate.WaitAsync cancellationToken

                try
                    let limit = outboundLimit ()

                    if notification.Length > limit then
                        raise (RpcFrameLimitExceededException(limit, notification.Length))

                    do! writeBytes notification.Bytes
                finally
                    gate.Release() |> ignore
            }

        interface IDisposable with
            member _.Dispose() = gate.Dispose()

    let private append (pending: ResizeArray<byte>) (buffer: byte array) count =
        for index in 0 .. count - 1 do
            pending.Add buffer[index]

    let private nextFrame limits (pending: ResizeArray<byte>) =
        let bytes = pending.ToArray()

        match MessagePackRpcCodec.tryReadValueLength limits bytes with
        | Error RpcFrameDecodeError.Incomplete -> Ok None
        | Error error -> Error error
        | Ok consumed ->
            let current = bytes[0 .. consumed - 1]

            match MessagePackRpcCodec.decodeFrame limits current with
            | Ok frame ->
                pending.RemoveRange(0, consumed)
                Ok(Some frame)
            | Error error -> Error error

    let private safelyInvoke (operation: unit -> Task<Result<'value, RpcError>>) =
        task {
            try
                return! operation ()
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException())
            | _ -> return Error RpcErrors.internalError
        }

    let private isCallableMethod (profile: RpcProfile) methodName =
        match profile.Methods.TryGetValue methodName with
        | true, descriptor -> descriptor.Classification <> NotificationMethod
        | _ -> methodName = "initialize"

    let runAsync
        (configuration: RpcSessionOptions)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            use backgroundCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            use loopCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            use writer =
                new SynchronizedWriter(
                    output,
                    configuration.GetOutboundFrameLimit,
                    configuration.Limits.MaximumValueBytes,
                    cancellationToken
                )

            let sink =
                RpcNotificationSink(
                    writer.WriteNotificationAsync,
                    writer.WriteEncodedNotificationAsync
                )

            let backgroundTasks = ResizeArray<Task>()

            let backgroundFault =
                TaskCompletionSource<exn> TaskCreationOptions.RunContinuationsAsynchronously

            let pending = ResizeArray<byte>()
            let buffer = Array.zeroCreate<byte> (1024 * 1024)
            let mutable initialized = false
            let mutable stopping = false
            let mutable exitCode = 0
            let mutable fatalState = 0

            let isFatal () = Volatile.Read(&fatalState) <> 0

            let fatalDiagnostic (message: string) =
                task {
                    if Interlocked.CompareExchange(&fatalState, 1, 0) = 0 then
                        do!
                            error.WriteLineAsync
                                $"dotnet-workspace-explorer workspace RPC protocol failure: {message}"

                        do! error.FlushAsync()
                }

            let writeError id rpcError =
                writer.WriteResponseAsync(id, Some rpcError, RpcValue.Nil)

            let reportBackgroundFault exceptionValue =
                if backgroundFault.TrySetResult exceptionValue then
                    backgroundCancellation.Cancel()
                    loopCancellation.Cancel()

            let startBackground work =
                let running =
                    task {
                        try
                            do! work sink backgroundCancellation.Token
                        with
                        | :? OperationCanceledException when
                            backgroundCancellation.IsCancellationRequested
                            ->
                            ()
                        | exceptionValue -> reportBackgroundFault exceptionValue
                    }

                backgroundTasks.Add running

            let awaitBackground () =
                task {
                    if backgroundTasks.Count > 0 then
                        do! Task.WhenAll(backgroundTasks.ToArray())
                }

            try
                while not stopping
                      && not (isFatal ())
                      && not cancellationToken.IsCancellationRequested do
                    if backgroundFault.Task.IsCompleted then
                        raise (OperationCanceledException())

                    let! read = input.ReadAsync(buffer, loopCancellation.Token)

                    if read = 0 then
                        if pending.Count > 0 then
                            do!
                                fatalDiagnostic
                                    "The input ended with an incomplete MessagePack value."

                            exitCode <- protocolFailure
                        else
                            stopping <- true
                    else
                        append pending buffer read
                        let mutable parseMore = true

                        while parseMore
                              && not stopping
                              && not (isFatal ())
                              && not backgroundFault.Task.IsCompleted do
                            match nextFrame configuration.Limits pending with
                            | Error(RpcFrameDecodeError.TooLarge message)
                            | Error(RpcFrameDecodeError.Invalid message) ->
                                do! fatalDiagnostic message
                                exitCode <- protocolFailure
                                stopping <- true
                            | Error RpcFrameDecodeError.Incomplete -> parseMore <- false
                            | Ok None -> parseMore <- false
                            | Ok(Some(RpcFrameDecodeResult.RecoverableError(id, rpcError))) ->
                                let! _ = writeError id rpcError
                                ()
                            | Ok(Some(RpcFrameDecodeResult.Frame frame)) ->
                                match frame with
                                | Request(id, methodName, parameters) ->
                                    if methodName <> "initialize" && not initialized then
                                        let! _ = writeError id RpcErrors.preInitialize
                                        ()
                                    elif
                                        not (isCallableMethod configuration.Profile methodName)
                                    then
                                        let! _ = writeError id (RpcErrors.unknownMethod methodName)
                                        ()
                                    elif methodName = "initialize" then
                                        if initialized then
                                            let! _ =
                                                writeError
                                                    id
                                                    (RpcErrors.invalidRequest
                                                        "A session cannot be initialized more than once.")

                                            ()
                                        else
                                            let! initialization =
                                                safelyInvoke (fun () ->
                                                    configuration.Initialize
                                                        parameters
                                                        cancellationToken)

                                            match initialization with
                                            | Ok result ->
                                                let! outcome =
                                                    writer.WriteResponseAsync(id, None, result)

                                                if outcome = OriginalResponse then
                                                    initialized <- true
                                            | Error rpcError ->
                                                let! _ = writeError id rpcError
                                                ()
                                    else
                                        let context =
                                            { Profile = configuration.Profile
                                              IsInitialized = initialized
                                              Limits = configuration.Limits }

                                        let! dispatched =
                                            safelyInvoke (fun () ->
                                                configuration.Dispatch
                                                    context
                                                    methodName
                                                    parameters
                                                    cancellationToken)

                                        match dispatched with
                                        | Error rpcError ->
                                            let! _ = writeError id rpcError
                                            ()
                                        | Ok result when result.StopAfterResponse ->
                                            backgroundCancellation.Cancel()
                                            do! awaitBackground ()

                                            if backgroundFault.Task.IsCompleted then
                                                raise (OperationCanceledException())

                                            let! _ =
                                                writer.WriteResponseAsync(id, None, result.Result)

                                            ()
                                            stopping <- true
                                        | Ok result ->
                                            let! outcome =
                                                writer.WriteResponseAsync(id, None, result.Result)

                                            try
                                                if outcome = OriginalResponse then
                                                    for notification in result.Notifications do
                                                        do!
                                                            writer.WriteNotificationAsync
                                                                notification

                                                    result.BackgroundWork
                                                    |> Option.iter startBackground
                                            finally
                                                result.AfterResponse
                                                |> Option.iter (fun action ->
                                                    try
                                                        action ()
                                                    with exceptionValue ->
                                                        reportBackgroundFault exceptionValue)
                                | Notification _ -> ()
                                | Response _ ->
                                    do! fatalDiagnostic "Clients may not send response frames."
                                    exitCode <- protocolFailure
                                    stopping <- true
            with
            | :? OperationCanceledException when backgroundFault.Task.IsCompleted ->
                do! fatalDiagnostic "A background RPC operation failed."
                exitCode <- protocolFailure
                stopping <- true
            | :? OperationCanceledException ->
                exitCode <- cancelled
                stopping <- true
            | _ ->
                do!
                    fatalDiagnostic
                        "The RPC session failed while reading or writing protocol frames."

                exitCode <- protocolFailure
                stopping <- true

            backgroundCancellation.Cancel()

            try
                do! awaitBackground ()
            with _ ->
                do! fatalDiagnostic "A background RPC operation failed during session shutdown."
                exitCode <- protocolFailure

            if backgroundFault.Task.IsCompleted && exitCode = 0 then
                do! fatalDiagnostic "A background RPC operation failed."
                exitCode <- protocolFailure

            return
                if cancellationToken.IsCancellationRequested && not (isFatal ()) then
                    cancelled
                elif isFatal () then
                    protocolFailure
                else
                    exitCode
        }
