namespace Dotnet.CLI.Plus.Transport.Tests

#nowarn "3511"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Transport
open Xunit

module private Test =
    let map values = RpcValue.map values
    let empty = RpcValue.emptyMap

    let request id name parameters =
        RpcCodec.encodeFrame (Request(id, name, parameters))

    let decodeAll (bytes: byte array) =
        let rec consume offset frames =
            if offset = bytes.Length then
                List.rev frames
            else
                match RpcCodec.tryReadValueLength RpcCodec.secureLimits bytes[offset..] with
                | Ok used ->
                    match RpcCodec.decodeFrame RpcCodec.secureLimits bytes[offset .. offset + used - 1] with
                    | Ok(RpcFrameDecodeResult.Frame frame) -> consume (offset + used) (frame :: frames)
                    | Ok(RpcFrameDecodeResult.RecoverableError(id, error)) ->
                        consume (offset + used) (Response(id, Some error, RpcValue.Nil) :: frames)
                    | Error error -> failwithf "decode failed: %A" error
                | Error error -> failwithf "value decode failed: %A" error

        consume 0 []

    let decodeAllWithSizes (bytes: byte array) =
        let rec consume offset frames =
            if offset = bytes.Length then
                List.rev frames
            else
                match RpcCodec.tryReadValueLength RpcCodec.secureLimits bytes[offset..] with
                | Ok used ->
                    match RpcCodec.decodeFrame RpcCodec.secureLimits bytes[offset .. offset + used - 1] with
                    | Ok(RpcFrameDecodeResult.Frame frame) -> consume (offset + used) ((frame, used) :: frames)
                    | result -> failwithf "decode failed: %A" result
                | Error error -> failwithf "value decode failed: %A" error

        consume 0 []

    let configurationWithLimit profile getOutboundLimit initialize dispatch =
        { Profile = profile
          Limits = RpcCodec.secureLimits
          GetOutboundFrameLimit = getOutboundLimit
          Initialize = initialize
          Dispatch = dispatch }

    let configuration profile initialize dispatch =
        configurationWithLimit profile (fun () -> RpcCodec.secureLimits.MaximumValueBytes) initialize dispatch

    let defaultConfiguration profile =
        configuration
            profile
            (fun _ _ -> Task.FromResult(Ok(map [ "ok", RpcValue.Boolean true ])))
            (fun _ methodName _ _ ->
                Task.FromResult(
                    Ok
                        { Result = map [ "method", RpcValue.String methodName ]
                          Notifications = []
                          BackgroundWork = None
                          AfterResponse = None
                          StopAfterResponse = methodName = "shutdown" }
                ))

    let runWithToken configuration (input: Stream) cancellationToken =
        task {
            use output = new MemoryStream()
            use errors = new StringWriter()
            let! exitCode = RpcSession.runAsync configuration input output errors cancellationToken
            return exitCode, output.ToArray(), errors.ToString()
        }

    let run configuration (input: byte array) =
        use source = new MemoryStream(input)
        runWithToken configuration source CancellationToken.None |> _.Result

    let errorCode =
        function
        | Response(_, Some error, _) -> Some error.Code
        | _ -> None

type private ChunkedReadStream(data: byte array, chunkSize: int) =
    inherit MemoryStream(data)

    override this.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()
        let count = min chunkSize buffer.Length
        ValueTask<int>(this.Read(buffer.Span.Slice(0, count)))

type private BlockingReadStream() =
    inherit Stream()
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.ReadAsync(_: Memory<byte>, cancellationToken: CancellationToken) =
        ValueTask<int>(
            task {
                do! Task.Delay(Timeout.Infinite, cancellationToken)
                return 0
            }
        )

type private BlockingAfterDataStream(data: byte array) =
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
            ValueTask<int>(count)
        else
            ValueTask<int>(
                task {
                    do! Task.Delay(Timeout.Infinite, cancellationToken)
                    return 0
                }
            )

type private FailingWriteStream() =
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
    override _.Write(_, _, _) = raise (IOException("write failed"))

    override _.WriteAsync(_: ReadOnlyMemory<byte>, _: CancellationToken) =
        ValueTask(Task.FromException(IOException("write failed")))

type TransportTests() =
    [<Fact>]
    member _.``codec round trips standard frames and nested values``() =
        let nested =
            Test.map [ "items", RpcValue.array [ RpcValue.Integer -4L; Test.map [ "nested", RpcValue.Boolean true ] ] ]

        let frames =
            [ Request(1u, "workspace/root", nested)
              Response(
                  1u,
                  Some
                      { Code = "invalid_params"
                        Message = "bad"
                        Data = Some nested },
                  RpcValue.Nil
              )
              Notification("workspace/reset", nested) ]

        let encoded =
            frames |> List.collect (RpcCodec.encodeFrame >> Array.toList) |> List.toArray

        Assert.Equal(3, (Test.decodeAll encoded).Length)

    [<Fact>]
    member _.``empty short arrays and incomplete error maps never throw``() =
        let values =
            [ [| 0x90uy |]
              [| 0x91uy; 0uy |]
              RpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 1UL
                        RpcValue.Unsigned 1UL
                        Test.map [ "code", RpcValue.String "bad" ]
                        RpcValue.Nil ]
              ) ]

        for value in values do
            match RpcCodec.decodeFrame RpcCodec.secureLimits value with
            | Error(RpcDecodeError.Invalid _) -> ()
            | result -> failwithf "Expected a typed decode error, got %A" result

    [<Fact>]
    member _.``correlatable malformed method and params preserve response IDs``() =
        let invalidMethod =
            RpcCodec.encodeValue (
                RpcValue.array
                    [ RpcValue.Unsigned 0UL
                      RpcValue.Unsigned 41UL
                      RpcValue.Integer 42L
                      Test.empty ]
            )

        let invalidParams =
            RpcCodec.encodeValue (
                RpcValue.array
                    [ RpcValue.Unsigned 0UL
                      RpcValue.Unsigned 42UL
                      RpcValue.String "workspace/root"
                      RpcValue.Nil ]
            )

        match RpcCodec.decodeFrame RpcCodec.secureLimits invalidMethod with
        | Ok(RpcFrameDecodeResult.RecoverableError(41u, error)) -> Assert.Equal("invalid_request", error.Code)
        | result -> failwithf "%A" result

        match RpcCodec.decodeFrame RpcCodec.secureLimits invalidParams with
        | Ok(RpcFrameDecodeResult.RecoverableError(42u, error)) -> Assert.Equal("invalid_params", error.Code)
        | result -> failwithf "%A" result

        let invalidUtf8Method = [| 0x94uy; 0uy; 43uy; 0xa1uy; 0xffuy; 0x80uy |]
        let invalidMapKey = [| 0x94uy; 0uy; 44uy; 0xa1uy; byte 'x'; 0x81uy; 1uy; 0xc0uy |]

        match RpcCodec.decodeFrame RpcCodec.secureLimits invalidUtf8Method with
        | Ok(RpcFrameDecodeResult.RecoverableError(43u, error)) -> Assert.Equal("invalid_request", error.Code)
        | result -> failwithf "%A" result

        match RpcCodec.decodeFrame RpcCodec.secureLimits invalidMapKey with
        | Ok(RpcFrameDecodeResult.RecoverableError(44u, error)) -> Assert.Equal("invalid_params", error.Code)
        | result -> failwithf "%A" result

    [<Fact>]
    member _.``session returns typed errors for correlatable malformed requests``() =
        let invalidMethod =
            RpcCodec.encodeValue (
                RpcValue.array
                    [ RpcValue.Unsigned 0UL
                      RpcValue.Unsigned 21UL
                      RpcValue.Integer 42L
                      Test.empty ]
            )

        let invalidParams =
            RpcCodec.encodeValue (
                RpcValue.array
                    [ RpcValue.Unsigned 0UL
                      RpcValue.Unsigned 22UL
                      RpcValue.String "workspace/root"
                      RpcValue.Nil ]
            )

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  invalidMethod
                  invalidParams
                  Test.request 2u "shutdown" Test.empty ]

        let exitCode, stdout, stderr =
            Test.run (Test.defaultConfiguration RpcProfile.publicProfile) input

        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)

        Assert.Contains(
            Test.decodeAll stdout,
            function
            | Response(21u, Some error, _) when error.Code = "invalid_request" -> true
            | _ -> false
        )

        Assert.Contains(
            Test.decodeAll stdout,
            function
            | Response(22u, Some error, _) when error.Code = "invalid_params" -> true
            | _ -> false
        )

    [<Fact>]
    member _.``strict application decoding rejects utf8 extensions keys duplicates depth and oversize``() =
        let invalidValues =
            [ [| 0xa1uy; 0xffuy |]
              [| 0xd4uy; 0uy; 0uy |]
              [| 0x81uy; 1uy; 0xc0uy |]
              [| 0x82uy; 0xa1uy; byte 'x'; 1uy; 0xa1uy; byte 'x'; 2uy |]
              Array.append (Array.replicate 65 0x91uy) [| 0xc0uy |] ]

        for value in invalidValues do
            match RpcCodec.tryDecodeValue RpcCodec.secureLimits value with
            | Error(RpcDecodeError.Invalid _) -> ()
            | result -> failwithf "Expected invalid value, got %A" result

        match
            RpcCodec.tryReadValueLength
                { RpcCodec.secureLimits with
                    MaximumValueBytes = 4 }
                [| 0xc4uy; 4uy; 0uy; 0uy; 0uy; 0uy |]
        with
        | Error(RpcDecodeError.TooLarge _) -> ()
        | result -> failwithf "%A" result

    [<Fact>]
    member _.``fragmented session reads and coalesced near limit values remain frame bounded``() =
        let initialize = Test.request 1u "initialize" Test.empty
        let payload = Array.zeroCreate<byte> (RpcCodec.secureLimits.MaximumValueBytes - 256)

        let large =
            Test.request 2u "large" (Test.map [ "payload", RpcValue.Binary payload ])

        Assert.True(large.Length <= RpcCodec.secureLimits.MaximumValueBytes)
        let shutdown = Test.request 3u "shutdown" Test.empty

        let profile =
            RpcProfile.create
                "large"
                1
                0
                [ { Name = "large"
                    Classification = Read }
                  { Name = "shutdown"
                    Classification = Control } ]

        use fragmented = new ChunkedReadStream(Array.concat [ initialize; shutdown ], 1)

        let fragmentedExit, fragmentedOutput, fragmentedError =
            Test.runWithToken (Test.defaultConfiguration profile) fragmented CancellationToken.None
            |> _.Result

        Assert.Equal(0, fragmentedExit)
        Assert.Equal(String.Empty, fragmentedError)
        Assert.Equal(2, (Test.decodeAll fragmentedOutput).Length)

        let exitCode, stdout, stderr =
            Test.run (Test.defaultConfiguration profile) (Array.concat [ initialize; large; shutdown ])

        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)
        Assert.Equal(3, (Test.decodeAll stdout).Length)

    [<Fact>]
    member _.``truncated eof is fatal while clean eof is normal``() =
        let configuration = Test.defaultConfiguration RpcProfile.publicProfile

        let truncatedExit, truncatedOutput, truncatedError =
            Test.run configuration [| 0x94uy; 0uy; 1uy |]

        Assert.Equal(65, truncatedExit)
        Assert.Empty(truncatedOutput)
        Assert.Contains("incomplete", truncatedError)
        let cleanExit, cleanOutput, cleanError = Test.run configuration Array.empty
        Assert.Equal(0, cleanExit)
        Assert.Empty(cleanOutput)
        Assert.Equal(String.Empty, cleanError)

    [<Fact>]
    member _.``read cancellation returns 130 without stdout or stack trace``() =
        use source = new BlockingReadStream()
        use cancellation = new CancellationTokenSource(50)

        let exitCode, stdout, stderr =
            Test.runWithToken (Test.defaultConfiguration RpcProfile.publicProfile) source cancellation.Token
            |> _.Result

        Assert.Equal(130, exitCode)
        Assert.Empty(stdout)
        Assert.Equal(String.Empty, stderr)

    [<Fact>]
    member _.``session enforces initialization reinitialize and profile isolation``() =
        let worker =
            RpcProfile.create
                "worker"
                1
                0
                [ { Name = "msbuild/evaluate"
                    Classification = Read }
                  { Name = "shutdown"
                    Classification = Control } ]

        let input =
            [ Test.request 1u "workspace/root" Test.empty
              Test.request 2u "initialize" Test.empty
              Test.request 3u "initialize" Test.empty
              Test.request 4u "workspace/root" Test.empty
              Test.request 5u "msbuild/evaluate" Test.empty
              Test.request 6u "shutdown" Test.empty ]
            |> Array.concat

        let exitCode, stdout, stderr = Test.run (Test.defaultConfiguration worker) input
        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)

        Assert.Equal<string list>(
            [ "not_initialized"; "invalid_request"; "unknown_method" ],
            Test.decodeAll stdout |> List.choose Test.errorCode
        )

    [<Fact>]
    member _.``public profile rejects worker methods``() =
        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "msbuild/evaluate" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let _, stdout, _ =
            Test.run (Test.defaultConfiguration RpcProfile.publicProfile) input

        Assert.Contains(
            Test.decodeAll stdout,
            function
            | Response(2u, Some error, _) when error.Code = "unknown_method" -> true
            | _ -> false
        )

    [<Fact>]
    member _.``shutdown cancels background work and emits no frames after its response``() =
        let profile =
            RpcProfile.create
                "background"
                1
                0
                [ { Name = "start"
                    Classification = Read }
                  { Name = "shutdown"
                    Classification = Control } ]

        let dispatch _ methodName _ _ =
            task {
                if methodName = "start" then
                    let background (sink: RpcNotificationSink) cancellationToken =
                        task {
                            try
                                do! Task.Delay(Timeout.Infinite, cancellationToken)
                            with :? OperationCanceledException ->
                                do! sink.WriteAsync(Notification("operation/completed", Test.empty))
                        }

                    return
                        Ok
                            { Result = Test.empty
                              Notifications = []
                              BackgroundWork = Some background
                              AfterResponse = None
                              StopAfterResponse = false }
                else
                    return
                        Ok
                            { Result = Test.empty
                              Notifications = []
                              BackgroundWork = None
                              AfterResponse = None
                              StopAfterResponse = true }
            }

        let configuration =
            Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let exitCode, stdout, stderr = Test.run configuration input
        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)
        let frames = Test.decodeAll stdout
        Assert.Equal(4, frames.Length)

        match frames[2] with
        | Notification("operation/completed", RpcValue.Map fields) -> Assert.Empty fields
        | frame -> failwithf "Expected completion before shutdown, got %A" frame

        match frames[3] with
        | Response(3u, None, _) -> ()
        | frame -> failwithf "Shutdown response was not the final frame: %A" frame

    [<Fact>]
    member _.``request dispatch remains serialized for mutation handlers``() =
        let mutable active = 0
        let mutable maximum = 0

        let profile =
            RpcProfile.create
                "mutations"
                1
                0
                [ { Name = "mutate"
                    Classification = Mutation }
                  { Name = "shutdown"
                    Classification = Control } ]

        let dispatch _ methodName _ _ =
            task {
                if methodName = "mutate" then
                    let current = Interlocked.Increment &active
                    maximum <- max maximum current
                    do! Task.Delay 20
                    Interlocked.Decrement &active |> ignore

                return
                    Ok
                        { Result = Test.empty
                          Notifications = []
                          BackgroundWork = None
                          AfterResponse = None
                          StopAfterResponse = methodName = "shutdown" }
            }

        let configuration =
            Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "mutate" Test.empty
                  Test.request 3u "mutate" Test.empty
                  Test.request 4u "shutdown" Test.empty ]

        let exitCode, stdout, _ = Test.run configuration input
        Assert.Equal(0, exitCode)
        Assert.Equal(1, maximum)
        Assert.Equal(4, (Test.decodeAll stdout).Length)

    [<Fact>]
    member _.``irrecoverable output failure is fatal and never exits zero``() =
        use input = new MemoryStream(Test.request 1u "initialize" Test.empty)
        use output = new FailingWriteStream()
        use errors = new StringWriter()

        let exitCode =
            RpcSession.runAsync
                (Test.defaultConfiguration RpcProfile.publicProfile)
                input
                output
                errors
                CancellationToken.None
            |> _.Result

        Assert.Equal(65, exitCode)
        Assert.Contains("failed while reading or writing", errors.ToString())

    [<Fact>]
    member _.``negotiated outbound limit replaces oversized initialize and request responses``() =
        let mutable outboundLimit = RpcCodec.secureLimits.MaximumValueBytes

        let profile =
            RpcProfile.create
                "limits"
                1
                0
                [ { Name = "big"; Classification = Read }
                  { Name = "shutdown"
                    Classification = Control } ]

        let initialize _ _ =
            outboundLimit <- 1024
            Task.FromResult(Ok(Test.map [ "payload", RpcValue.String(String('x', 5000)) ]))

        let dispatch _ methodName _ _ =
            Task.FromResult(
                Ok
                    { Result = Test.map [ "payload", RpcValue.String(String('y', 5000)) ]
                      Notifications = []
                      BackgroundWork = None
                      AfterResponse = None
                      StopAfterResponse = methodName = "shutdown" }
            )

        let configuration =
            Test.configurationWithLimit profile (fun () -> outboundLimit) initialize dispatch

        let input =
            Array.concat [ Test.request 1u "initialize" Test.empty; Test.request 2u "big" Test.empty ]

        let exitCode, stdout, stderr = Test.run configuration input
        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)
        let frames = Test.decodeAllWithSizes stdout
        Assert.All(frames, fun (_, size) -> Assert.True(size <= 1024))

        match frames[0] |> fst with
        | Response(1u, Some error, _) -> Assert.Equal("response_too_large", error.Code)
        | frame -> failwithf "%A" frame

        match frames[1] |> fst with
        | Response(2u, Some error, _) -> Assert.Equal("not_initialized", error.Code)
        | frame -> failwithf "%A" frame

    [<Fact>]
    member _.``oversized background notification can complete as a bounded typed failure``() =
        let mutable outboundLimit = RpcCodec.secureLimits.MaximumValueBytes

        let profile =
            RpcProfile.create
                "bounded-background"
                1
                0
                [ { Name = "start"
                    Classification = Read }
                  { Name = "shutdown"
                    Classification = Control } ]

        let initialize _ _ =
            outboundLimit <- 1024
            Task.FromResult(Ok Test.empty)

        let dispatch _ methodName _ _ =
            if methodName = "start" then
                let background (sink: RpcNotificationSink) _ =
                    task {
                        try
                            do!
                                sink.WriteAsync(
                                    Notification(
                                        "operation/output",
                                        Test.map [ "text", RpcValue.String(String('x', 5000)) ]
                                    )
                                )
                        with :? RpcOutboundFrameTooLargeException ->
                            do!
                                sink.WriteAsync(
                                    Notification(
                                        "operation/completed",
                                        Test.map
                                            [ "outcome", RpcValue.String "failed"
                                              "code", RpcValue.String "response_too_large" ]
                                    )
                                )
                    }

                Task.FromResult(
                    Ok
                        { Result = Test.empty
                          Notifications = []
                          BackgroundWork = Some background
                          AfterResponse = None
                          StopAfterResponse = false }
                )
            else
                Task.FromResult(
                    Ok
                        { Result = Test.empty
                          Notifications = []
                          BackgroundWork = None
                          AfterResponse = None
                          StopAfterResponse = true }
                )

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let exitCode, stdout, stderr =
            Test.run (Test.configurationWithLimit profile (fun () -> outboundLimit) initialize dispatch) input

        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)
        let frames = Test.decodeAllWithSizes stdout
        Assert.All(frames, fun (_, size) -> Assert.True(size <= 1024))

        Assert.Contains(
            frames |> List.map fst,
            function
            | Notification("operation/completed", parameters) ->
                parameters |> RpcValue.tryField "code" = Some(RpcValue.String "response_too_large")
            | _ -> false
        )

    [<Fact>]
    member _.``faulted background work wakes reads and exits with protocol failure``() =
        let profile =
            RpcProfile.create
                "fault"
                1
                0
                [ { Name = "start"
                    Classification = Read } ]

        let dispatch _ _ _ _ =
            let background (_: RpcNotificationSink) _ =
                task {
                    do! Task.Yield()
                    failwith "boom"
                }

            Task.FromResult(
                Ok
                    { Result = Test.empty
                      Notifications = []
                      BackgroundWork = Some background
                      AfterResponse = None
                      StopAfterResponse = false }
            )

        let input =
            Array.concat [ Test.request 1u "initialize" Test.empty; Test.request 2u "start" Test.empty ]

        use source = new BlockingAfterDataStream(input)

        let exitCode, stdout, stderr =
            Test.runWithToken
                (Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch)
                source
                CancellationToken.None
            |> _.Result

        Assert.Equal(65, exitCode)
        Assert.Equal(2, (Test.decodeAll stdout).Length)
        Assert.Contains("background RPC operation failed", stderr)

    [<Fact>]
    member _.``background fault during shutdown prevents a false successful shutdown``() =
        let profile =
            RpcProfile.create
                "shutdown-fault"
                1
                0
                [ { Name = "start"
                    Classification = Read }
                  { Name = "shutdown"
                    Classification = Control } ]

        let dispatch _ methodName _ _ =
            if methodName = "start" then
                let background (_: RpcNotificationSink) cancellationToken =
                    task {
                        try
                            do! Task.Delay(Timeout.Infinite, cancellationToken)
                        with :? OperationCanceledException ->
                            failwith "shutdown fault"
                    }

                Task.FromResult(
                    Ok
                        { Result = Test.empty
                          Notifications = []
                          BackgroundWork = Some background
                          AfterResponse = None
                          StopAfterResponse = false }
                )
            else
                Task.FromResult(
                    Ok
                        { Result = Test.empty
                          Notifications = []
                          BackgroundWork = None
                          AfterResponse = None
                          StopAfterResponse = true }
                )

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let exitCode, stdout, stderr =
            Test.run (Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch) input

        Assert.Equal(65, exitCode)
        Assert.Equal(2, (Test.decodeAll stdout).Length)
        Assert.Contains("background RPC operation failed", stderr)

    [<Fact>]
    member _.``initialize validation negotiates version capabilities and limits``() =
        let valid =
            Test.map
                [ "protocolVersion", Test.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 9L ]
                  "clientInfo", Test.map [ "name", RpcValue.String "test" ]
                  "capabilities",
                  RpcValue.array
                      [ RpcValue.String "workspace.root"
                        RpcValue.String "unknown.claim"
                        RpcValue.String "operation.cancel" ]
                  "limits", Test.map [ "maxFrameBytes", RpcValue.Integer 4096L; "maxPageSize", RpcValue.Integer 50L ] ]

        let request =
            PublicProtocol.parseInitialize valid
            |> Result.defaultWith (fun error -> failwith error.Message)

        Assert.Equal(0, request.ProtocolMinor)
        Assert.Equal(4096, request.MaximumFrameBytes)

        let descriptor =
            WorkspaceDescriptor.Create(
                WorkspaceTargetPath.Create(Path.GetTempPath()),
                HostFileSystemCaseSemantics.Sensitive,
                WorkspaceFormat.Slnf,
                WorkspaceRevision.Create 0L,
                WorkspaceAccess.ReadWrite
            )

        let result = PublicProtocol.initializeResult descriptor 0L request

        let capabilities =
            result
            |> RpcValue.tryField "capabilities"
            |> Option.map (RpcValue.requireArray "capabilities")

        Assert.Equal(2, capabilities.Value.Length)

        let invalid =
            [ Test.empty
              Test.map
                  [ "protocolVersion", Test.map [ "major", RpcValue.Integer 2L; "minor", RpcValue.Integer 0L ]
                    "clientInfo", Test.map [ "name", RpcValue.String "test" ]
                    "capabilities", RpcValue.array [] ]
              Test.map
                  [ "protocolVersion", Test.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                    "clientInfo", Test.map [ "name", RpcValue.String "" ]
                    "capabilities", RpcValue.array [] ]
              Test.map
                  [ "protocolVersion", Test.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                    "clientInfo", Test.map [ "name", RpcValue.String "test" ]
                    "capabilities", RpcValue.array [ RpcValue.String "x"; RpcValue.String "x" ] ] ]

        for parameters in invalid do
            match PublicProtocol.parseInitialize parameters with
            | Error error -> Assert.Equal("invalid_params", error.Code)
            | Ok value -> failwithf "Expected invalid initialize, got %A" value
