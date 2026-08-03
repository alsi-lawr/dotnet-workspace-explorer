namespace Dotnet.WorkspaceExplorer.Testing

open System.IO
open Dotnet.WorkspaceExplorer.Rpc

[<RequireQualifiedAccess>]
module WorkspaceRpcTransport =
    let request id methodName parameters =
        MessagePackRpcCodec.encodeFrame (Request(id, methodName, parameters))

    let send (stream: Stream) fragmented (bytes: byte array) =
        if fragmented then
            for value in bytes do
                stream.WriteByte value
                stream.Flush()
        else
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush()

    let readFrameWithSize (stream: Stream) =
        let pending = ResizeArray<byte>()
        let mutable result = None

        while result.IsNone do
            let next = stream.ReadByte()

            if next < 0 then
                result <- Some(Error "The stream ended before a complete frame was received.")
            else
                pending.Add(byte next)

                match
                    MessagePackRpcCodec.tryReadValueLength
                        MessagePackRpcCodec.secureLimits
                        (pending.ToArray())
                with
                | Error RpcFrameDecodeError.Incomplete -> ()
                | Error error -> result <- Some(Error $"The stream contained invalid data: {error}")
                | Ok length when length = pending.Count ->
                    match
                        MessagePackRpcCodec.decodeFrame
                            MessagePackRpcCodec.secureLimits
                            (pending.ToArray())
                    with
                    | Ok(RpcFrameDecodeResult.Frame frame) -> result <- Some(Ok(frame, length))
                    | Ok(RpcFrameDecodeResult.RecoverableError _) ->
                        result <- Some(Error "The stream contained a recoverable request error.")
                    | Error error ->
                        result <- Some(Error $"The stream contained an invalid frame: {error}")
                | Ok _ ->
                    result <- Some(Error "The frame reader consumed an unexpected byte count.")

        result.Value

    let readFrame stream =
        readFrameWithSize stream |> Result.map fst

    let response expectedId =
        function
        | Response(actualId, outcome) when actualId = expectedId -> Ok outcome
        | frame -> Error $"Expected response {expectedId}, got {frame}."
