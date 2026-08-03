namespace Dotnet.WorkspaceExplorer.Rpc


open System
open System.Buffers
open System.Collections.Immutable
open System.IO
open System.Text
open MessagePack

[<RequireQualifiedAccess>]
type RpcFrameDecodeError =
    | Incomplete
    | Invalid of string
    | TooLarge of string

[<RequireQualifiedAccess>]
type RpcFrameDecodeResult =
    | Frame of RpcFrame
    | RecoverableError of messageId: uint32 * error: RpcError

type MessagePackRpcLimits =
    { MaximumValueBytes: int
      MaximumDepth: int }

[<RequireQualifiedAccess>]
module MessagePackRpcCodec =
    let secureLimits =
        { MaximumValueBytes = 16 * 1024 * 1024
          MaximumDepth = 64 }

    let private strictUtf8 = UTF8Encoding(false, true)

    let private security limits =
        MessagePackSecurity.UntrustedData
            .WithMaximumObjectGraphDepth(limits.MaximumDepth)
            .WithMaximumDecompressedSize
            limits.MaximumValueBytes

    let private invalid message =
        Error(RpcFrameDecodeError.Invalid message)

    let private safeMessage (error: exn) =
        match error with
        | :? DecoderFallbackException -> "MessagePack strings must contain valid UTF-8."
        | :? InsufficientExecutionStackException ->
            "MessagePack nesting exceeds the configured limit."
        | :? OverflowException -> "MessagePack numeric value is outside the supported range."
        | :? MessagePackSerializationException -> "The MessagePack value is malformed."
        | :? ArgumentException when not (String.IsNullOrWhiteSpace error.Message) -> error.Message
        | _ -> "The MessagePack value is malformed."

    let private readStrictString (reader: byref<MessagePackReader>) =
        let bytes = reader.ReadStringSequence()

        if not bytes.HasValue then
            invalidArg "value" "Expected a string."

        let sequence = bytes.Value
        let buffer = Array.zeroCreate<byte> (int sequence.Length)
        sequence.CopyTo buffer
        strictUtf8.GetString buffer

    let private readInteger (reader: byref<MessagePackReader>) =
        let code = reader.NextCode

        if code >= 0xccuy && code <= 0xcfuy then
            RpcValue.Unsigned(reader.ReadUInt64())
        elif code <= 0x7fuy then
            RpcValue.Unsigned(reader.ReadUInt64())
        else
            RpcValue.Integer(reader.ReadInt64())

    let rec private readValue
        (limits: MessagePackRpcLimits)
        (configuredSecurity: MessagePackSecurity)
        (reader: byref<MessagePackReader>)
        =
        reader.CancellationToken.ThrowIfCancellationRequested()

        match reader.NextMessagePackType with
        | MessagePackType.Nil ->
            reader.ReadNil() |> ignore
            RpcValue.Nil
        | MessagePackType.Boolean -> RpcValue.Boolean(reader.ReadBoolean())
        | MessagePackType.Integer -> readInteger &reader
        | MessagePackType.Float -> RpcValue.Float(reader.ReadDouble())
        | MessagePackType.String -> RpcValue.String(readStrictString &reader)
        | MessagePackType.Binary ->
            let bytes = reader.ReadBytes()

            if not bytes.HasValue then
                invalidArg "value" "Expected a binary value."

            let sequence = bytes.Value
            let buffer = Array.zeroCreate<byte> (int sequence.Length)
            sequence.CopyTo buffer
            RpcValue.Binary buffer
        | MessagePackType.Array ->
            configuredSecurity.DepthStep &reader

            try
                let count = reader.ReadArrayHeader()

                if count > min 1000000 limits.MaximumValueBytes then
                    invalidArg "value" "MessagePack arrays exceed the configured item limit."

                let values = ImmutableArray.CreateBuilder<RpcValue> count

                for _ in 1..count do
                    values.Add(readValue limits configuredSecurity &reader)

                RpcValue.Array(values.MoveToImmutable())
            finally
                reader.Depth <- reader.Depth - 1
        | MessagePackType.Map ->
            configuredSecurity.DepthStep &reader

            try
                let count = reader.ReadMapHeader()

                if count > min 500000 (limits.MaximumValueBytes / 2) then
                    invalidArg "value" "MessagePack maps exceed the configured item limit."

                let fields =
                    ImmutableDictionary.CreateBuilder<string, RpcValue>(
                        configuredSecurity.GetEqualityComparer<string>()
                    )

                for _ in 1..count do
                    if reader.NextMessagePackType <> MessagePackType.String then
                        invalidArg "value" "MessagePack map keys must be strings."

                    let key = readStrictString &reader

                    if String.IsNullOrEmpty key then
                        invalidArg "value" "MessagePack map keys must be non-empty strings."

                    if fields.ContainsKey key then
                        invalidArg "value" "MessagePack maps cannot contain duplicate keys."

                    fields.Add(key, readValue limits configuredSecurity &reader)

                RpcValue.Map(fields.ToImmutable())
            finally
                reader.Depth <- reader.Depth - 1
        | MessagePackType.Extension ->
            invalidArg "value" "MessagePack extension values are not allowed."
        | _ -> invalidArg "value" "Unsupported MessagePack value type."

    let tryReadValueLength limits (bytes: byte array) =
        try
            let mutable reader = MessagePackReader(ReadOnlyMemory<byte> bytes)

            reader.Skip()

            if reader.Consumed > int64 limits.MaximumValueBytes then
                Error(RpcFrameDecodeError.TooLarge "Inbound MessagePack value exceeds 16 MiB.")
            else
                Ok(int reader.Consumed)
        with
        | :? EndOfStreamException ->
            if bytes.Length > limits.MaximumValueBytes then
                Error(RpcFrameDecodeError.TooLarge "Inbound MessagePack value exceeds 16 MiB.")
            else
                Error RpcFrameDecodeError.Incomplete
        | error -> invalid (safeMessage error)

    let tryDecodeValue limits (bytes: byte array) =
        match tryReadValueLength limits bytes with
        | Error error -> Error error
        | Ok length ->
            try
                let mutable reader = MessagePackReader(ReadOnlyMemory<byte>(bytes, 0, length))
                let value = readValue limits (security limits) &reader

                if reader.Consumed <> int64 length then
                    invalid "Trailing bytes in a MessagePack value are not allowed."
                else
                    Ok(value, length)
            with
            | :? EndOfStreamException -> Error RpcFrameDecodeError.Incomplete
            | error -> invalid (safeMessage error)

    let private tryUnsigned32 value =
        match value with
        | RpcValue.Unsigned number when number <= uint64 UInt32.MaxValue -> Some(uint32 number)
        | RpcValue.Integer number when number >= 0L && number <= int64 UInt32.MaxValue ->
            Some(uint32 number)
        | _ -> None

    let private tryTag value =
        match value with
        | RpcValue.Unsigned number when number <= 2UL -> Some(int number)
        | RpcValue.Integer number when number >= 0L && number <= 2L -> Some(int number)
        | _ -> None

    let private tryError value =
        match value with
        | RpcValue.Nil -> Ok None
        | RpcValue.Map fields ->
            match fields.TryGetValue "code", fields.TryGetValue "message" with
            | (true, RpcValue.String code), (true, RpcValue.String message) ->
                Ok(
                    Some
                        { Code = code
                          Message = message
                          Data =
                            match fields.TryGetValue "data" with
                            | true, data -> Some data
                            | _ -> None }
                )
            | _ -> invalid "A response error map requires string code and message fields."
        | _ -> invalid "A response error must be nil or a string-key map."

    let private tryReadNext limits configuredSecurity (reader: byref<MessagePackReader>) =
        try
            Ok(readValue limits configuredSecurity &reader)
        with
        | :? EndOfStreamException -> Error RpcFrameDecodeError.Incomplete
        | error -> Error(RpcFrameDecodeError.Invalid(safeMessage error))

    let decodeFrame limits (bytes: byte array) =
        match tryReadValueLength limits bytes with
        | Error error -> Error error
        | Ok consumed when consumed <> bytes.Length ->
            invalid "Trailing bytes in a frame are not allowed."
        | Ok _ ->
            try
                let configuredSecurity = security limits
                let mutable reader = MessagePackReader(ReadOnlyMemory<byte> bytes)

                if reader.NextMessagePackType <> MessagePackType.Array then
                    invalid "A MessagePack-RPC frame must be an array."
                else
                    let count = reader.ReadArrayHeader()

                    if count = 0 then
                        invalid "A MessagePack-RPC frame cannot be an empty array."
                    else
                        match tryReadNext limits configuredSecurity &reader with
                        | Error error -> Error error
                        | Ok tagValue ->
                            match tryTag tagValue with
                            | Some 0 ->
                                if count < 2 then
                                    invalid
                                        "A request requires a non-negative uint32-compatible message ID."
                                else
                                    match tryReadNext limits configuredSecurity &reader with
                                    | Error error -> Error error
                                    | Ok idValue ->
                                        match tryUnsigned32 idValue with
                                        | None ->
                                            invalid
                                                "A request requires a non-negative uint32-compatible message ID."
                                        | Some id when count <> 4 ->
                                            Ok(
                                                RpcFrameDecodeResult.RecoverableError(
                                                    id,
                                                    { Code = "invalid_request"
                                                      Message =
                                                        "A request frame must contain exactly four values."
                                                      Data = None }
                                                )
                                            )
                                        | Some id ->
                                            let methodResult =
                                                tryReadNext limits configuredSecurity &reader

                                            let paramsResult =
                                                tryReadNext limits configuredSecurity &reader

                                            match methodResult, paramsResult with
                                            | Ok(RpcValue.String methodName),
                                              Ok(RpcValue.Map _ as parameters) when
                                                not (String.IsNullOrWhiteSpace methodName)
                                                ->
                                                Ok(
                                                    RpcFrameDecodeResult.Frame(
                                                        Request(id, methodName, parameters)
                                                    )
                                                )
                                            | Ok(RpcValue.String methodName), Ok _ when
                                                not (String.IsNullOrWhiteSpace methodName)
                                                ->
                                                Ok(
                                                    RpcFrameDecodeResult.RecoverableError(
                                                        id,
                                                        { Code = "invalid_params"
                                                          Message =
                                                            "Request params must be a string-key map."
                                                          Data = None }
                                                    )
                                                )
                                            | Ok(RpcValue.String methodName), Error decodeError when
                                                not (String.IsNullOrWhiteSpace methodName)
                                                ->
                                                Ok(
                                                    RpcFrameDecodeResult.RecoverableError(
                                                        id,
                                                        { Code = "invalid_params"
                                                          Message =
                                                            match decodeError with
                                                            | RpcFrameDecodeError.Invalid message ->
                                                                message
                                                            | RpcFrameDecodeError.TooLarge message ->
                                                                message
                                                            | RpcFrameDecodeError.Incomplete ->
                                                                "Request params are incomplete."
                                                          Data = None }
                                                    )
                                                )
                                            | _ ->
                                                Ok(
                                                    RpcFrameDecodeResult.RecoverableError(
                                                        id,
                                                        { Code = "invalid_request"
                                                          Message =
                                                            "A request method must be a non-empty UTF-8 string."
                                                          Data = None }
                                                    )
                                                )
                            | Some 1 when count = 4 ->
                                let id = tryReadNext limits configuredSecurity &reader
                                let error = tryReadNext limits configuredSecurity &reader
                                let result = tryReadNext limits configuredSecurity &reader

                                match id, error, result with
                                | Ok idValue, Ok errorValue, Ok resultValue ->
                                    match tryUnsigned32 idValue, tryError errorValue with
                                    | Some messageId, Ok rpcError ->
                                        Ok(
                                            RpcFrameDecodeResult.Frame(
                                                Response(
                                                    messageId,
                                                    match rpcError with
                                                    | Some error -> Error error
                                                    | None -> Ok resultValue
                                                )
                                            )
                                        )
                                    | None, _ ->
                                        invalid
                                            "A response requires a non-negative uint32-compatible message ID."
                                    | _, Error decodeError -> Error decodeError
                                | Error decodeError, _, _
                                | _, Error decodeError, _
                                | _, _, Error decodeError -> Error decodeError
                            | Some 1 -> invalid "A response frame must contain exactly four values."
                            | Some 2 when count = 3 ->
                                let methodName = tryReadNext limits configuredSecurity &reader
                                let parameters = tryReadNext limits configuredSecurity &reader

                                match methodName, parameters with
                                | Ok(RpcValue.String name),
                                  Ok(RpcValue.Map _ as notificationParameters) when
                                    not (String.IsNullOrWhiteSpace name)
                                    ->
                                    Ok(
                                        RpcFrameDecodeResult.Frame(
                                            Notification(name, notificationParameters)
                                        )
                                    )
                                | Error decodeError, _
                                | _, Error decodeError -> Error decodeError
                                | _ ->
                                    invalid
                                        "A notification requires a non-empty string method and string-key params map."
                            | Some 2 ->
                                invalid "A notification frame must contain exactly three values."
                            | _ -> invalid "The MessagePack-RPC frame tag must be 0, 1, or 2."
            with
            | :? EndOfStreamException -> Error RpcFrameDecodeError.Incomplete
            | error -> invalid (safeMessage error)

    let rec private writeValue (writer: byref<MessagePackWriter>) value =
        match value with
        | RpcValue.Nil -> writer.WriteNil()
        | RpcValue.Boolean boolean -> writer.Write boolean
        | RpcValue.Integer integer -> writer.Write integer
        | RpcValue.Unsigned integer -> writer.Write integer
        | RpcValue.Float number -> writer.Write number
        | RpcValue.String text -> writer.Write text
        | RpcValue.Binary bytes -> writer.Write bytes
        | RpcValue.Array values ->
            writer.WriteArrayHeader values.Length

            for item in values do
                writeValue &writer item
        | RpcValue.Map fields ->
            writer.WriteMapHeader fields.Count

            for field in fields |> Seq.sortBy _.Key do
                writer.Write field.Key
                writeValue &writer field.Value

    let encodeValue value =
        let buffer = ArrayBufferWriter<byte>()
        let mutable writer = MessagePackWriter buffer
        writeValue &writer value
        writer.Flush()
        buffer.WrittenSpan.ToArray()

    let encodeFrame frame =
        let errorValue (error: RpcError) =
            RpcValue.map
                [ "code", RpcValue.String error.Code
                  "message", RpcValue.String error.Message
                  "data", error.Data |> Option.defaultValue RpcValue.Nil ]

        match frame with
        | Request(id, methodName, parameters) ->
            RpcValue.array
                [ RpcValue.Unsigned 0UL
                  RpcValue.Unsigned(uint64 id)
                  RpcValue.String methodName
                  parameters ]
        | Response(id, outcome) ->
            let error, result =
                match outcome with
                | Ok result -> RpcValue.Nil, result
                | Error error -> errorValue error, RpcValue.Nil

            RpcValue.array [ RpcValue.Unsigned 1UL; RpcValue.Unsigned(uint64 id); error; result ]
        | Notification(methodName, parameters) ->
            RpcValue.array [ RpcValue.Unsigned 2UL; RpcValue.String methodName; parameters ]
        |> encodeValue
