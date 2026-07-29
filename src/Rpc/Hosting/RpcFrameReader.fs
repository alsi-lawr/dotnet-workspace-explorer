namespace Dotnet.WorkspaceExplorer.Rpc


open System
open System.Buffers
open System.IO
open System.Threading
open System.Threading.Tasks
open MessagePack

type private FrameLimitedReadStream(inner: Stream, maximumBytes: int) =
    inherit Stream()

    let mutable remaining = maximumBytes
    let mutable bytesRead = 0

    member _.BytesRead = bytesRead

    member private _.Consumed count =
        remaining <- remaining - count
        bytesRead <- bytesRead + count
        count

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override this.Read(buffer, offset, count) =
        if remaining = 0 then
            0
        else
            inner.Read(buffer, offset, min count remaining) |> this.Consumed

    override this.ReadAsync(buffer: Memory<byte>, cancellationToken) =
        if remaining = 0 then
            ValueTask<int> 0
        else
            ValueTask<int>(
                task {
                    let! read =
                        inner.ReadAsync(
                            buffer.Slice(0, min buffer.Length remaining),
                            cancellationToken
                        )

                    return this.Consumed read
                }
            )

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

type internal RpcFrameReadFailure =
    | Cancelled = 0
    | Transport = 1
    | Invalid = 2
    | TooLarge = 3

[<AbstractClass; Sealed>]
type internal RpcFrameReader private () =
    static member ReadOneAsync
        (input: Stream, maximumFrameBytes: int, cancellationToken: CancellationToken)
        =
        task {
            if
                maximumFrameBytes < 1
                || maximumFrameBytes > MessagePackRpcCodec.secureLimits.MaximumValueBytes
            then
                return Error RpcFrameReadFailure.TooLarge
            else
                use capped = new FrameLimitedReadStream(input, maximumFrameBytes + 1)
                use reader = new MessagePackStreamReader(capped, true)

                try
                    let! value = reader.ReadAsync(cancellationToken).AsTask()

                    if capped.BytesRead > maximumFrameBytes then
                        return Error RpcFrameReadFailure.TooLarge
                    elif not value.HasValue then
                        return Error RpcFrameReadFailure.Invalid
                    elif value.Value.Length > int64 maximumFrameBytes then
                        return Error RpcFrameReadFailure.TooLarge
                    elif reader.RemainingBytes.Length > 0L then
                        return Error RpcFrameReadFailure.Invalid
                    else
                        let bytes = Array.zeroCreate<byte> (int value.Value.Length)
                        value.Value.CopyTo bytes
                        return Ok bytes
                with
                | :? OperationCanceledException -> return Error RpcFrameReadFailure.Cancelled
                | :? MessagePackSerializationException
                | :? EndOfStreamException
                | :? InsufficientExecutionStackException
                | :? OverflowException
                | :? ArgumentException -> return Error RpcFrameReadFailure.Invalid
                | :? IOException
                | :? ObjectDisposedException -> return Error RpcFrameReadFailure.Transport
        }
