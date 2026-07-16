namespace Dotnet.CLI.Plus.Transport

#nowarn "3511"

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

type RpcNotificationSink internal (write: RpcFrame -> Task<unit>) =
    member _.WriteAsync(frame: RpcFrame) = write frame

type RpcDispatchResult =
    { Result: RpcValue
      Notifications: RpcFrame list
      BackgroundWork: (RpcNotificationSink -> CancellationToken -> Task<unit>) option
      AfterResponse: (unit -> unit) option
      StopAfterResponse: bool }

type RpcSessionContext =
    { Profile: RpcProfile
      IsInitialized: bool
      Limits: RpcCodecLimits }

type RpcSessionConfiguration =
    { Profile: RpcProfile
      Limits: RpcCodecLimits
      Initialize: RpcValue -> CancellationToken -> Task<Result<RpcValue, RpcError>>
      Dispatch:
          RpcSessionContext -> string -> RpcValue -> CancellationToken -> Task<Result<RpcDispatchResult, RpcError>> }

[<RequireQualifiedAccess>]
module RpcErrors =
    let create code message data =
        { Code = code
          Message = message
          Data = data }

    let invalidParams message = create "invalid_params" message None
    let invalidRequest message = create "invalid_request" message None

    let preInitialize =
        create "not_initialized" "initialize must be called before other methods." None

    let unknownMethod name =
        create "unknown_method" $"The method '{name}' is not available in this protocol profile." None

    let unsupported message =
        create "unsupported_capability" message None

    let internalError =
        create "internal_error" "The request could not be completed safely." None

[<RequireQualifiedAccess>]
module RpcSession =
    let private protocolFailure = 65
    let private cancelled = 130

    type private SynchronizedWriter(output: Stream, cancellationToken: CancellationToken) =
        let gate = new SemaphoreSlim(1, 1)

        member _.WriteAsync(frame: RpcFrame) =
            task {
                do! gate.WaitAsync cancellationToken

                try
                    let bytes = RpcCodec.encodeFrame frame
                    do! output.WriteAsync(bytes, cancellationToken)
                    do! output.FlushAsync cancellationToken
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

        match RpcCodec.tryReadValueLength limits bytes with
        | Error RpcDecodeError.Incomplete -> Ok None
        | Error error -> Error error
        | Ok consumed ->
            let current = bytes[0 .. consumed - 1]

            match RpcCodec.decodeFrame limits current with
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

    let runAsync
        (configuration: RpcSessionConfiguration)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            use backgroundCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            use writer = new SynchronizedWriter(output, cancellationToken)
            let sink = RpcNotificationSink(writer.WriteAsync)
            let backgroundTasks = ResizeArray<Task>()
            let pending = ResizeArray<byte>()
            let buffer = Array.zeroCreate<byte> (1024 * 1024)
            let mutable initialized = false
            let mutable stopping = false
            let mutable fatal = false
            let mutable exitCode = 0

            let fatalDiagnostic (message: string) =
                task {
                    if not fatal then
                        fatal <- true
                        do! error.WriteLineAsync($"dotnet-plus pipe protocol failure: {message}")
                        do! error.FlushAsync()
                }

            let writeError id rpcError =
                writer.WriteAsync(Response(id, Some rpcError, RpcValue.Nil))

            let startBackground work =
                let running =
                    task {
                        try
                            do! work sink backgroundCancellation.Token
                        with
                        | :? OperationCanceledException -> ()
                        | _ -> ()
                    }

                backgroundTasks.Add running

            let awaitBackground () =
                task {
                    if backgroundTasks.Count > 0 then
                        try
                            do! Task.WhenAll(backgroundTasks.ToArray())
                        with
                        | :? OperationCanceledException -> ()
                        | _ -> ()
                }

            try
                while not stopping && not fatal && not cancellationToken.IsCancellationRequested do
                    let! read = input.ReadAsync(buffer, cancellationToken)

                    if read = 0 then
                        if pending.Count > 0 then
                            do! fatalDiagnostic "The input ended with an incomplete MessagePack value."
                            exitCode <- protocolFailure
                        else
                            stopping <- true
                    else
                        append pending buffer read
                        let mutable parseMore = true

                        while parseMore && not stopping && not fatal do
                            match nextFrame configuration.Limits pending with
                            | Error(RpcDecodeError.TooLarge message)
                            | Error(RpcDecodeError.Invalid message) ->
                                do! fatalDiagnostic message
                                exitCode <- protocolFailure
                                stopping <- true
                            | Error RpcDecodeError.Incomplete -> parseMore <- false
                            | Ok None -> parseMore <- false
                            | Ok(Some(RpcFrameDecodeResult.RecoverableError(id, rpcError))) ->
                                do! writeError id rpcError
                            | Ok(Some(RpcFrameDecodeResult.Frame frame)) ->
                                match frame with
                                | Request(id, methodName, parameters) ->
                                    if methodName <> "initialize" && not initialized then
                                        do! writeError id RpcErrors.preInitialize
                                    elif
                                        methodName <> "initialize"
                                        && not (configuration.Profile.Methods.ContainsKey methodName)
                                    then
                                        do! writeError id (RpcErrors.unknownMethod methodName)
                                    elif methodName = "initialize" then
                                        if initialized then
                                            do!
                                                writeError
                                                    id
                                                    (RpcErrors.invalidRequest
                                                        "A session cannot be initialized more than once.")
                                        else
                                            let! initialization =
                                                safelyInvoke (fun () ->
                                                    configuration.Initialize parameters cancellationToken)

                                            match initialization with
                                            | Ok result ->
                                                initialized <- true
                                                do! writer.WriteAsync(Response(id, None, result))
                                            | Error rpcError -> do! writeError id rpcError
                                    else
                                        let context =
                                            { Profile = configuration.Profile
                                              IsInitialized = initialized
                                              Limits = configuration.Limits }

                                        let! dispatched =
                                            safelyInvoke (fun () ->
                                                configuration.Dispatch context methodName parameters cancellationToken)

                                        match dispatched with
                                        | Error rpcError -> do! writeError id rpcError
                                        | Ok result when result.StopAfterResponse ->
                                            backgroundCancellation.Cancel()
                                            do! awaitBackground ()
                                            do! writer.WriteAsync(Response(id, None, result.Result))
                                            stopping <- true
                                        | Ok result ->
                                            do! writer.WriteAsync(Response(id, None, result.Result))

                                            result.AfterResponse
                                            |> Option.iter (fun action ->
                                                try
                                                    action ()
                                                with _ ->
                                                    ())

                                            for notification in result.Notifications do
                                                do! writer.WriteAsync notification

                                            result.BackgroundWork |> Option.iter startBackground
                                | Notification _ -> ()
                                | Response _ ->
                                    do! fatalDiagnostic "Clients may not send response frames."
                                    exitCode <- protocolFailure
                                    stopping <- true
            with :? OperationCanceledException ->
                exitCode <- cancelled
                stopping <- true

            backgroundCancellation.Cancel()
            do! awaitBackground ()

            return
                if cancellationToken.IsCancellationRequested then cancelled
                elif fatal then protocolFailure
                else exitCode
        }
