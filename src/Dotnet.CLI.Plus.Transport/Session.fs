namespace Dotnet.CLI.Plus.Transport

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

type RpcDispatchResult =
    { Result: RpcValue
      Notifications: RpcFrame list
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

[<RequireQualifiedAccess>]
module RpcSession =
    let private protocolFailure = 65

    let private writeFrame (output: Stream) frame cancellationToken =
        task {
            let bytes = RpcCodec.encodeFrame frame
            do! output.WriteAsync(bytes, cancellationToken)
            do! output.FlushAsync(cancellationToken)
        }

    let private append (pending: ResizeArray<byte>) (buffer: byte array) count =
        for index in 0 .. count - 1 do
            pending.Add buffer[index]

    let private tryFrame limits (pending: ResizeArray<byte>) =
        let bytes = pending.ToArray()

        match RpcCodec.tryDecodeValue limits bytes with
        | Error RpcDecodeError.Incomplete -> Ok None
        | Error error -> Error error
        | Ok(_, consumed) ->
            let current = bytes[0 .. consumed - 1]

            match RpcCodec.decodeFrame limits current with
            | Ok frame ->
                pending.RemoveRange(0, consumed)
                Ok(Some frame)
            | Error error -> Error error

    let runAsync
        (configuration: RpcSessionConfiguration)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            let pending = ResizeArray<byte>()
            let buffer = Array.zeroCreate<byte> 4096
            let mutable initialized = false
            let mutable stopping = false
            let mutable fatal = false

            let fatalDiagnostic (message: string) =
                task {
                    if not fatal then
                        fatal <- true
                        do! error.WriteLineAsync($"dotnet-plus pipe protocol failure: {message}")
                        do! error.FlushAsync()
                }


            while not stopping && not fatal && not cancellationToken.IsCancellationRequested do
                let! read = input.ReadAsync(buffer, cancellationToken)

                if read = 0 then
                    stopping <- true
                else
                    append pending buffer read
                    let mutable parseMore = true

                    while parseMore && not stopping && not fatal do
                        match tryFrame configuration.Limits pending with
                        | Error(RpcDecodeError.TooLarge message)
                        | Error(RpcDecodeError.Invalid message) ->
                            do! fatalDiagnostic message
                            stopping <- true
                        | Error RpcDecodeError.Incomplete -> parseMore <- false
                        | Ok None ->
                            if pending.Count > configuration.Limits.MaximumValueBytes then
                                do! fatalDiagnostic "Inbound MessagePack value exceeds 16 MiB."
                                stopping <- true
                            else
                                parseMore <- false
                        | Ok(Some frame) ->
                            match frame with
                            | Request(id, methodName, parameters) ->
                                if methodName <> "initialize" && not initialized then
                                    do!
                                        writeFrame
                                            output
                                            (Response(id, Some RpcErrors.preInitialize, RpcValue.Nil))
                                            cancellationToken
                                elif
                                    methodName <> "initialize"
                                    && not (configuration.Profile.Methods.ContainsKey methodName)
                                then
                                    do!
                                        writeFrame
                                            output
                                            (Response(id, Some(RpcErrors.unknownMethod methodName), RpcValue.Nil))
                                            cancellationToken
                                elif methodName = "initialize" then
                                    if initialized then
                                        do!
                                            writeFrame
                                                output
                                                (Response(
                                                    id,
                                                    Some(
                                                        RpcErrors.invalidRequest
                                                            "A session cannot be initialized more than once."
                                                    ),
                                                    RpcValue.Nil
                                                ))
                                                cancellationToken
                                    else
                                        let! initialization = configuration.Initialize parameters cancellationToken

                                        match initialization with
                                        | Ok result ->
                                            initialized <- true
                                            do! writeFrame output (Response(id, None, result)) cancellationToken
                                        | Error rpcError ->
                                            do!
                                                writeFrame
                                                    output
                                                    (Response(id, Some rpcError, RpcValue.Nil))
                                                    cancellationToken
                                elif not initialized then
                                    do!
                                        writeFrame
                                            output
                                            (Response(id, Some RpcErrors.preInitialize, RpcValue.Nil))
                                            cancellationToken
                                else
                                    let context =
                                        { Profile = configuration.Profile
                                          IsInitialized = initialized
                                          Limits = configuration.Limits }

                                    let! dispatched =
                                        configuration.Dispatch context methodName parameters cancellationToken

                                    match dispatched with
                                    | Error rpcError ->
                                        do!
                                            writeFrame
                                                output
                                                (Response(id, Some rpcError, RpcValue.Nil))
                                                cancellationToken
                                    | Ok result ->
                                        do! writeFrame output (Response(id, None, result.Result)) cancellationToken

                                        for notification in result.Notifications do
                                            do! writeFrame output notification cancellationToken

                                        if result.StopAfterResponse then
                                            stopping <- true
                            | Notification _ -> ()
                            | Response _ ->
                                do! fatalDiagnostic "Clients may not send response frames."
                                stopping <- true

            return
                if fatal then protocolFailure
                elif cancellationToken.IsCancellationRequested then 130
                else 0
        }
