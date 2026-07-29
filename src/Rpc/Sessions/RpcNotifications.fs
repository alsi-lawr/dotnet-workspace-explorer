namespace Dotnet.WorkspaceExplorer.Rpc


#nowarn "3511"

open System
open System.Threading.Tasks

type RpcFrameLimitExceededException(limit: int, actual: int) =
    inherit
        InvalidOperationException
            $"The encoded RPC frame is {actual} bytes and exceeds the negotiated {limit}-byte limit."


    member _.Limit = limit
    member _.Actual = actual

/// An encoded notification whose bytes can only be created from a notification frame.
[<Sealed>]
type EncodedRpcNotification private (bytes: byte array) =
    member internal _.Bytes = bytes
    member _.Length = bytes.Length

    static member Create(frame: RpcFrame) =
        match frame with
        | Notification _ -> EncodedRpcNotification(MessagePackRpcCodec.encodeFrame frame)
        | _ ->
            invalidArg
                (nameof frame)
                "Only notification frames can be prepared for notification output."

type RpcNotificationSink
    internal (write: RpcFrame -> Task<unit>, writeEncoded: EncodedRpcNotification -> Task<unit>) =
    member _.WriteAsync(frame: RpcFrame) = write frame
    member _.WriteEncodedAsync(notification: EncodedRpcNotification) = writeEncoded notification
