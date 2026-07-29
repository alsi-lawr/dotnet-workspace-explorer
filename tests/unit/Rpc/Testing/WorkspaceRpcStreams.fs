namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

type internal ChunkedReadStream(data: byte array, chunkSize: int) =
    inherit MemoryStream(data)

    override this.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()
        ValueTask<int>(this.Read(buffer.Span.Slice(0, min chunkSize buffer.Length)))

type internal CancellingReadStream(cancellation: CancellationTokenSource) =
    inherit MemoryStream()

    override _.ReadAsync(_: Memory<byte>, cancellationToken: CancellationToken) =
        cancellation.Cancel()
        ValueTask<int>(Task.FromCanceled<int> cancellationToken)

type internal BlockingAfterDataStream(data: byte array) =
    inherit Stream()
    let mutable offset = 0
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = int64 data.Length

    override _.Position
        with get () = int64 offset
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) =
        if offset < data.Length then
            let count = min buffer.Length (data.Length - offset)
            data.AsSpan(offset, count).CopyTo buffer.Span
            offset <- offset + count
            ValueTask<int> count
        else
            ValueTask<int>(
                task {
                    do! Task.Delay(Timeout.Infinite, cancellationToken)
                    return 0
                }
            )

type internal FailingWriteStream() =
    inherit Stream()
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (IOException "write failed")

    override _.WriteAsync(_: ReadOnlyMemory<byte>, _: CancellationToken) =
        ValueTask(Task.FromException(IOException "write failed"))

type internal BlockingNotificationWriteStream(notificationStarted: TaskCompletionSource) =
    inherit Stream()
    let bytes = new MemoryStream()
    let mutable writes = 0
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = bytes.Length

    override _.Position
        with get () = bytes.Position
        and set _ = raise (NotSupportedException())

    member _.ToArray() = bytes.ToArray()
    override _.Flush() = ()
    override _.FlushAsync(_: CancellationToken) = Task.CompletedTask
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken: CancellationToken) =
        let write = Interlocked.Increment(&writes)

        if write <= 2 then
            bytes.Write buffer.Span
            ValueTask()
        else
            notificationStarted.TrySetResult() |> ignore

            ValueTask(task { do! Task.Delay(Timeout.Infinite, cancellationToken) })
