namespace Dotnet.CLI.Plus.Transport

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.Collections.Immutable
open System.Text
open System.IO

[<RequireQualifiedAccess>]
type RpcDecodeError =
    | Incomplete
    | Invalid of string
    | TooLarge of string

type RpcCodecLimits =
    { MaximumValueBytes: int
      MaximumDepth: int }

[<RequireQualifiedAccess>]
module RpcCodec =
    let secureLimits =
        { MaximumValueBytes = 16 * 1024 * 1024
          MaximumDepth = 64 }

    let private invalid message = Error(RpcDecodeError.Invalid message)

    let private require count index length =
        if index + count > length then
            raise (EndOfStreamException())

    let private u16 source index =
        BinaryPrimitives.ReadUInt16BigEndian(ReadOnlySpan(source, index, 2)) |> int

    let private u32 source index =
        BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(source, index, 4)) |> int64

    let private u64 source index =
        BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan(source, index, 8))

    let private parse (limits: RpcCodecLimits) (source: byte array) start length =
        let rec value depth index =
            if depth > limits.MaximumDepth then
                raise (ArgumentException "MessagePack nesting exceeds the configured limit.")

            require 1 index length
            let marker = source[index]
            let next = index + 1

            let text count offset =
                require count offset length
                Encoding.UTF8.GetString(source, offset, count), offset + count

            let binary count offset =
                require count offset length
                source[offset .. offset + count - 1], offset + count

            let array count offset =
                let mutable cursor = offset
                let values = ResizeArray<RpcValue>()

                for _ in 1..count do
                    let parsed, after = value (depth + 1) cursor
                    values.Add parsed
                    cursor <- after

                RpcValue.Array(ImmutableArray.CreateRange values), cursor

            let map count offset =
                let fields =
                    ImmutableDictionary.CreateBuilder<string, RpcValue>(StringComparer.Ordinal)

                let mutable cursor = offset

                for _ in 1..count do
                    let key, afterKey = value (depth + 1) cursor

                    let name =
                        match key with
                        | RpcValue.String text when not (String.IsNullOrEmpty text) -> text
                        | _ -> raise (ArgumentException "MessagePack map keys must be strings.")

                    let parsed, afterValue = value (depth + 1) afterKey

                    if not (fields.TryAdd(name, parsed)) then
                        raise (ArgumentException "MessagePack maps cannot contain duplicate keys.")

                    cursor <- afterValue

                RpcValue.Map(fields.ToImmutable()), cursor

            match marker with
            | value when value <= 0x7fuy -> RpcValue.Unsigned(uint64 value), next
            | value when value >= 0xe0uy -> RpcValue.Integer(int64 (sbyte value)), next
            | value when value >= 0xa0uy && value <= 0xbfuy ->
                text (int (value &&& 0x1fuy)) next |> fun (v, i) -> RpcValue.String v, i
            | value when value >= 0x90uy && value <= 0x9fuy -> array (int (value &&& 0x0fuy)) next
            | value when value >= 0x80uy && value <= 0x8fuy -> map (int (value &&& 0x0fuy)) next
            | 0xc0uy -> RpcValue.Nil, next
            | 0xc2uy -> RpcValue.Boolean false, next
            | 0xc3uy -> RpcValue.Boolean true, next
            | 0xc4uy ->
                require 1 next length
                binary (int source[next]) (next + 1) |> fun (v, i) -> RpcValue.Binary v, i
            | 0xc5uy ->
                require 2 next length
                binary (u16 source next) (next + 2) |> fun (v, i) -> RpcValue.Binary v, i
            | 0xc6uy ->
                require 4 next length
                let count = u32 source next in

                if count > int64 Int32.MaxValue then
                    raise (ArgumentException "Binary value is too large.")
                else
                    binary (int count) (next + 4) |> fun (v, i) -> RpcValue.Binary v, i
            | 0xc7uy
            | 0xc8uy
            | 0xc9uy
            | 0xd4uy
            | 0xd5uy
            | 0xd6uy
            | 0xd7uy
            | 0xd8uy -> raise (ArgumentException "MessagePack extension values are not allowed.")
            | 0xcauy ->
                require 4 next length

                RpcValue.Float(
                    float (
                        BitConverter.Int32BitsToSingle(
                            BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan(source, next, 4))
                        )
                    )
                ),
                next + 4
            | 0xcbuy ->
                require 8 next length

                RpcValue.Float(
                    BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(source, next, 8)))
                ),
                next + 8
            | 0xccuy ->
                require 1 next length
                RpcValue.Unsigned(uint64 source[next]), next + 1
            | 0xcduy ->
                require 2 next length
                RpcValue.Unsigned(uint64 (u16 source next)), next + 2
            | 0xceuy ->
                require 4 next length

                RpcValue.Unsigned(uint64 (BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(source, next, 4)))),
                next + 4
            | 0xcfuy ->
                require 8 next length
                RpcValue.Unsigned(u64 source next), next + 8
            | 0xd0uy ->
                require 1 next length
                RpcValue.Integer(int64 (sbyte source[next])), next + 1
            | 0xd1uy ->
                require 2 next length
                RpcValue.Integer(int64 (BinaryPrimitives.ReadInt16BigEndian(ReadOnlySpan(source, next, 2)))), next + 2
            | 0xd2uy ->
                require 4 next length
                RpcValue.Integer(int64 (BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan(source, next, 4)))), next + 4
            | 0xd3uy ->
                require 8 next length
                RpcValue.Integer(BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(source, next, 8))), next + 8
            | 0xd9uy ->
                require 1 next length
                text (int source[next]) (next + 1) |> fun (v, i) -> RpcValue.String v, i
            | 0xdauy ->
                require 2 next length
                text (u16 source next) (next + 2) |> fun (v, i) -> RpcValue.String v, i
            | 0xdbuy ->
                require 4 next length
                let count = u32 source next in

                if count > int64 Int32.MaxValue then
                    raise (ArgumentException "String value is too large.")
                else
                    text (int count) (next + 4) |> fun (v, i) -> RpcValue.String v, i
            | 0xdcuy ->
                require 2 next length
                array (u16 source next) (next + 2)
            | 0xdduy ->
                require 4 next length
                let count = u32 source next in

                if count > int64 Int32.MaxValue then
                    raise (ArgumentException "Array is too large.")
                else
                    array (int count) (next + 4)
            | 0xdeuy ->
                require 2 next length
                map (u16 source next) (next + 2)
            | 0xdfuy ->
                require 4 next length
                let count = u32 source next in

                if count > int64 Int32.MaxValue then
                    raise (ArgumentException "Map is too large.")
                else
                    map (int count) (next + 4)
            | _ -> raise (ArgumentException "Unsupported MessagePack marker.")

        value 0 start

    let tryDecodeValue limits (bytes: byte array) =
        if bytes.Length > limits.MaximumValueBytes then
            Error(RpcDecodeError.TooLarge "Inbound MessagePack value exceeds 16 MiB.")
        else
            try
                let parsed, consumed = parse limits bytes 0 bytes.Length
                Ok(parsed, consumed)
            with
            | :? EndOfStreamException -> Error RpcDecodeError.Incomplete
            | :? ArgumentException as error -> invalid error.Message

    let private integer =
        function
        | RpcValue.Integer value -> value
        | RpcValue.Unsigned value when value <= uint64 Int64.MaxValue -> int64 value
        | _ -> invalidArg "frame" "Expected an integer frame tag."

    let decodeFrame limits bytes =
        match tryDecodeValue limits bytes with
        | Error error -> Error error
        | Ok(value, consumed) when consumed <> bytes.Length -> invalid "Trailing bytes in a frame are not allowed."
        | Ok(RpcValue.Array values, _) ->
            try
                match values.Length, integer values[0] with
                | 4, 0L ->
                    let id = RpcValue.requireUnsigned32 "msgid" values[1]
                    let methodName = RpcValue.requireString "method" values[2]
                    RpcValue.requireMap "params" values[3] |> ignore
                    Ok(Request(id, methodName, values[3]))
                | 4, 1L ->
                    let id = RpcValue.requireUnsigned32 "msgid" values[1]

                    let error =
                        match values[2] with
                        | RpcValue.Nil -> None
                        | RpcValue.Map fields ->
                            let code = fields |> fun map -> map["code"] |> RpcValue.requireString "error.code"

                            let message =
                                fields |> fun map -> map["message"] |> RpcValue.requireString "error.message"

                            Some
                                { Code = code
                                  Message = message
                                  Data =
                                    fields.TryGetValue "data"
                                    |> function
                                        | true, data -> Some data
                                        | _ -> None }
                        | _ -> invalidArg "error" "Expected an error map or nil."

                    Ok(Response(id, error, values[3]))
                | 3, 2L ->
                    let methodName = RpcValue.requireString "method" values[1]
                    RpcValue.requireMap "params" values[2] |> ignore
                    Ok(Notification(methodName, values[2]))
                | _ -> invalid "Invalid MessagePack-RPC frame arity or tag."
            with :? ArgumentException as error ->
                invalid error.Message
        | Ok _ -> invalid "A MessagePack-RPC frame must be an array."

    let private appendUnsigned (buffer: ResizeArray<byte>) (value: uint64) =
        if value <= 0x7fUL then
            buffer.Add(byte value)
        elif value <= 0xffUL then
            buffer.Add 0xccuy
            buffer.Add(byte value)
        elif value <= 0xffffUL then
            buffer.Add 0xcduy
            buffer.Add(byte (value >>> 8))
            buffer.Add(byte value)
        elif value <= 0xffffffffUL then
            buffer.Add 0xceuy

            for shift in [ 24; 16; 8; 0 ] do
                buffer.Add(byte (value >>> shift))
        else
            buffer.Add 0xcfuy

            for shift in [ 56; 48; 40; 32; 24; 16; 8; 0 ] do
                buffer.Add(byte (value >>> shift))

    let private appendLength marker8 marker16 marker32 (buffer: ResizeArray<byte>) length =
        if length <= 0xff then
            buffer.Add marker8
            buffer.Add(byte length)
        elif length <= 0xffff then
            buffer.Add marker16
            buffer.Add(byte (length >>> 8))
            buffer.Add(byte length)
        else
            buffer.Add marker32

            for shift in [ 24; 16; 8; 0 ] do
                buffer.Add(byte (uint32 length >>> shift))

    let encodeValue value =
        let buffer = ResizeArray<byte>()

        let rec append value =
            match value with
            | RpcValue.Nil -> buffer.Add 0xc0uy
            | RpcValue.Boolean false -> buffer.Add 0xc2uy
            | RpcValue.Boolean true -> buffer.Add 0xc3uy
            | RpcValue.Unsigned number -> appendUnsigned buffer number
            | RpcValue.Integer number when number >= 0L -> appendUnsigned buffer (uint64 number)
            | RpcValue.Integer number when number >= -32L -> buffer.Add(byte (sbyte number))
            | RpcValue.Integer number when number >= int64 Int16.MinValue ->
                buffer.Add 0xd1uy
                let raw = int16 number in
                buffer.Add(byte (raw >>> 8))
                buffer.Add(byte raw)
            | RpcValue.Integer number when number >= int64 Int32.MinValue ->
                buffer.Add 0xd2uy
                let raw = int32 number in

                for shift in [ 24; 16; 8; 0 ] do
                    buffer.Add(byte (raw >>> shift))
            | RpcValue.Integer number ->
                buffer.Add 0xd3uy

                for shift in [ 56; 48; 40; 32; 24; 16; 8; 0 ] do
                    buffer.Add(byte (number >>> shift))
            | RpcValue.Float number ->
                buffer.Add 0xcbuy
                let raw = BitConverter.DoubleToInt64Bits number in

                for shift in [ 56; 48; 40; 32; 24; 16; 8; 0 ] do
                    buffer.Add(byte (raw >>> shift))
            | RpcValue.String text ->
                let bytes = Encoding.UTF8.GetBytes text

                if bytes.Length <= 31 then
                    buffer.Add(byte (0xa0 + bytes.Length))
                else
                    appendLength 0xd9uy 0xdauy 0xdbuy buffer bytes.Length

                buffer.AddRange bytes
            | RpcValue.Binary bytes ->
                appendLength 0xc4uy 0xc5uy 0xc6uy buffer bytes.Length
                buffer.AddRange bytes
            | RpcValue.Array values ->
                if values.Length <= 15 then
                    buffer.Add(byte (0x90 + values.Length))
                elif values.Length <= 0xffff then
                    buffer.Add 0xdcuy
                    buffer.Add(byte (values.Length >>> 8))
                    buffer.Add(byte values.Length)
                else
                    buffer.Add 0xdduy

                    for shift in [ 24; 16; 8; 0 ] do
                        buffer.Add(byte (uint32 values.Length >>> shift))

                for item in values do
                    append item
            | RpcValue.Map fields ->
                if fields.Count <= 15 then
                    buffer.Add(byte (0x80 + fields.Count))
                elif fields.Count <= 0xffff then
                    buffer.Add 0xdeuy
                    buffer.Add(byte (fields.Count >>> 8))
                    buffer.Add(byte fields.Count)
                else
                    buffer.Add 0xdfuy

                    for shift in [ 24; 16; 8; 0 ] do
                        buffer.Add(byte (uint32 fields.Count >>> shift))

                for field in fields |> Seq.sortBy _.Key do
                    append (RpcValue.String field.Key)
                    append field.Value

        append value
        buffer.ToArray()

    let encodeFrame frame =
        let errorValue error =
            match error with
            | None -> RpcValue.Nil
            | Some value ->
                RpcValue.map [ "code", RpcValue.String value.Code; "message", RpcValue.String value.Message ]
                |> fun baseMap ->
                    match value.Data with
                    | None -> baseMap
                    | Some data ->
                        match baseMap with
                        | RpcValue.Map fields -> RpcValue.Map(fields.Add("data", data))
                        | _ -> failwith "Impossible."

        match frame with
        | Request(id, methodName, parameters) ->
            RpcValue.array
                [ RpcValue.Unsigned 0UL
                  RpcValue.Unsigned(uint64 id)
                  RpcValue.String methodName
                  parameters ]
        | Response(id, error, result) ->
            RpcValue.array
                [ RpcValue.Unsigned 1UL
                  RpcValue.Unsigned(uint64 id)
                  errorValue error
                  result ]
        | Notification(methodName, parameters) ->
            RpcValue.array [ RpcValue.Unsigned 2UL; RpcValue.String methodName; parameters ]
        |> encodeValue
