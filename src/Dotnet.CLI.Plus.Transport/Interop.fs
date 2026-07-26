namespace Dotnet.CLI.Plus.Transport

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open MessagePack

type private CappedReadStream(inner: Stream, maximumBytes: int) =
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
                || maximumFrameBytes > RpcCodec.secureLimits.MaximumValueBytes
            then
                return Error RpcFrameReadFailure.TooLarge
            else
                use capped = new CappedReadStream(input, maximumFrameBytes + 1)
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

[<Sealed>]
type internal RpcInteropResponse
    private (outcome: Result<RpcValue, RpcError>, stopAfterResponse: bool) =
    member internal _.Outcome = outcome
    member internal _.StopAfterResponse = stopAfterResponse

    static member Ok(result: RpcValue, stopAfterResponse: bool) =
        RpcInteropResponse(Ok result, stopAfterResponse)

    static member Fail(error: RpcError) = RpcInteropResponse(Error error, false)

[<AbstractClass; Sealed>]
type internal RpcHost private () =
    static member CreateProfile
        (name: string, major: int, minor: int, methods: IEnumerable<string>)
        =
        methods
        |> Seq.map (fun methodName ->
            { Name = methodName
              Classification =
                if methodName = "initialize" || methodName = "shutdown" then
                    Control
                else
                    Read })
        |> RpcProfile.create name major minor

    static member RunAsync
        (
            profile: RpcProfile,
            input: Stream,
            output: Stream,
            error: TextWriter,
            getOutboundFrameLimit: Func<int>,
            initialize: Func<RpcValue, CancellationToken, Task<RpcInteropResponse>>,
            dispatch: Func<string, RpcValue, CancellationToken, Task<RpcInteropResponse>>,
            cancellationToken: CancellationToken
        ) =
        let convertDispatch result stopAfterResponse =
            result
            |> Result.map (fun value ->
                { Result = value
                  Notifications = []
                  BackgroundWork = None
                  AfterResponse = None
                  StopAfterResponse = stopAfterResponse })

        let configuration =
            { Profile = profile
              Limits = RpcCodec.secureLimits
              GetOutboundFrameLimit = getOutboundFrameLimit.Invoke
              Initialize =
                fun parameters token ->
                    task {
                        let! response = initialize.Invoke(parameters, token)
                        return response.Outcome
                    }
              Dispatch =
                fun _ methodName parameters token ->
                    task {
                        let! response = dispatch.Invoke(methodName, parameters, token)
                        return convertDispatch response.Outcome response.StopAfterResponse
                    } }

        RpcSession.runAsync configuration input output error cancellationToken
