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

    let decode (bytes: byte array) =
        let rec consume offset decoded =
            if offset = bytes.Length then
                List.rev decoded
            else
                match RpcCodec.tryReadValueLength RpcCodec.secureLimits bytes[offset..] with
                | Ok size ->
                    match RpcCodec.decodeFrame RpcCodec.secureLimits bytes[offset .. offset + size - 1] with
                    | Ok(RpcFrameDecodeResult.Frame frame) -> consume (offset + size) ((frame, size) :: decoded)
                    | result -> failwithf "Response decode failed: %A" result
                | Error error -> failwithf "Response length decode failed: %A" error

        consume 0 []

    let frames bytes = decode bytes |> List.map fst

    let profile name methods =
        methods
        |> Seq.map (fun (methodName, classification) ->
            { Name = methodName
              Classification = classification })
        |> RpcProfile.create name 1 0

    let dispatchResult result stop =
        { Result = result
          Notifications = []
          BackgroundWork = None
          AfterResponse = None
          StopAfterResponse = stop }

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
                    Ok(dispatchResult (map [ "method", RpcValue.String methodName ]) (methodName = "shutdown"))
                ))

    let runStream configuration (input: Stream) cancellationToken =
        task {
            use output = new MemoryStream()
            use errors = new StringWriter()
            let! exitCode = RpcSession.runAsync configuration input output errors cancellationToken
            return exitCode, output.ToArray(), errors.ToString()
        }

    let run configuration (input: byte array) =
        use stream = new MemoryStream(input)
        runStream configuration stream CancellationToken.None |> _.Result

    let responseErrors bytes =
        frames bytes
        |> List.choose (function
            | Response(id, Some error, _) -> Some(id, error.Code)
            | _ -> None)

type private ChunkedReadStream(data: byte array, chunkSize: int) =
    inherit MemoryStream(data)

    override this.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()
        ValueTask<int>(this.Read(buffer.Span.Slice(0, min chunkSize buffer.Length)))

type private CancellingReadStream(cancellation: CancellationTokenSource) =
    inherit MemoryStream()

    override _.ReadAsync(_: Memory<byte>, cancellationToken: CancellationToken) =
        cancellation.Cancel()
        ValueTask<int>(Task.FromCanceled<int>(cancellationToken))

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
            data.AsSpan(offset, count).CopyTo(buffer.Span)
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
    member _.``standard request response error and notification frames retain their golden wire shapes``() =
        let error =
            { Code = "e"
              Message = "m"
              Data = Some(Test.map [ "d", RpcValue.Integer 1L ]) }

        let cases =
            [ Request(7u, "x", Test.empty), [| 0x94uy; 0x00uy; 0x07uy; 0xa1uy; 0x78uy; 0x80uy |]
              Response(7u, Some error, Test.empty),
              [| 0x94uy
                 0x01uy
                 0x07uy
                 0x83uy
                 0xa4uy
                 0x63uy
                 0x6fuy
                 0x64uy
                 0x65uy
                 0xa1uy
                 0x65uy
                 0xa4uy
                 0x64uy
                 0x61uy
                 0x74uy
                 0x61uy
                 0x81uy
                 0xa1uy
                 0x64uy
                 0x01uy
                 0xa7uy
                 0x6duy
                 0x65uy
                 0x73uy
                 0x73uy
                 0x61uy
                 0x67uy
                 0x65uy
                 0xa1uy
                 0x6duy
                 0x80uy |]
              Notification("n", Test.map [ "v", RpcValue.Boolean true ]),
              [| 0x93uy; 0x02uy; 0xa1uy; 0x6euy; 0x81uy; 0xa1uy; 0x76uy; 0xc3uy |] ]

        for frame, golden in cases do
            Assert.Equal<byte>(golden, RpcCodec.encodeFrame frame)

            match RpcCodec.decodeFrame RpcCodec.secureLimits golden with
            | Ok(RpcFrameDecodeResult.Frame(Request(7u, "x", RpcValue.Map fields))) -> Assert.Empty(fields)
            | Ok(RpcFrameDecodeResult.Frame(Response(7u, Some decoded, RpcValue.Map result))) ->
                Assert.Equal("e", decoded.Code)
                Assert.Equal("m", decoded.Message)
                Assert.Equal(Some(RpcValue.Unsigned 1UL), decoded.Data |> Option.bind (RpcValue.tryField "d"))
                Assert.Empty(result)
            | Ok(RpcFrameDecodeResult.Frame(Notification("n", parameters))) ->
                Assert.Equal(Some(RpcValue.Boolean true), RpcValue.tryField "v" parameters)
            | result -> failwithf "Golden frame no longer decodes: %A" result

    [<Fact>]
    member _.``malformed values preserve recoverable IDs and session continuation``() =
        let invalidValues =
            [ "invalid UTF-8", [| 0xa1uy; 0xffuy |]
              "extension", [| 0xd4uy; 0uy; 0uy |]
              "non-string map key", [| 0x81uy; 1uy; 0xc0uy |]
              "duplicate map key", [| 0x82uy; 0xa1uy; byte 'x'; 1uy; 0xa1uy; byte 'x'; 2uy |]
              "excessive depth", Array.append (Array.replicate 65 0x91uy) [| 0xc0uy |] ]

        for name, bytes in invalidValues do
            match RpcCodec.tryDecodeValue RpcCodec.secureLimits bytes with
            | Error(RpcDecodeError.Invalid _) -> ()
            | result -> failwithf "%s: expected invalid value, got %A" name result

        let invalidFrames =
            [ "empty frame", [| 0x90uy |]
              "short request", [| 0x91uy; 0uy |]
              "incomplete error map",
              RpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 1UL
                        RpcValue.Unsigned 1UL
                        Test.map [ "code", RpcValue.String "bad" ]
                        RpcValue.Nil ]
              ) ]

        for name, bytes in invalidFrames do
            match RpcCodec.decodeFrame RpcCodec.secureLimits bytes with
            | Error(RpcDecodeError.Invalid _) -> ()
            | result -> failwithf "%s: expected invalid frame, got %A" name result

        match
            RpcCodec.tryReadValueLength
                { RpcCodec.secureLimits with
                    MaximumValueBytes = 4 }
                [| 0xc4uy; 4uy; 0uy; 0uy; 0uy; 0uy |]
        with
        | Error(RpcDecodeError.TooLarge _) -> ()
        | result -> failwithf "oversized value: expected too large, got %A" result

        let recoverable =
            [ "wrong arity",
              RpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 0UL
                        RpcValue.Unsigned 40UL
                        RpcValue.String "workspace/root" ]
              ),
              40u,
              "invalid_request"
              "non-string method",
              RpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 0UL
                        RpcValue.Unsigned 41UL
                        RpcValue.Integer 42L
                        Test.empty ]
              ),
              41u,
              "invalid_request"
              "non-map params",
              RpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 0UL
                        RpcValue.Unsigned 42UL
                        RpcValue.String "workspace/root"
                        RpcValue.Nil ]
              ),
              42u,
              "invalid_params"
              "invalid UTF-8 method", [| 0x94uy; 0uy; 43uy; 0xa1uy; 0xffuy; 0x80uy |], 43u, "invalid_request"
              "invalid params map key",
              [| 0x94uy; 0uy; 44uy; 0xa1uy; byte 'x'; 0x81uy; 1uy; 0xc0uy |],
              44u,
              "invalid_params" ]

        for name, bytes, expectedId, expectedCode in recoverable do
            match RpcCodec.decodeFrame RpcCodec.secureLimits bytes with
            | Ok(RpcFrameDecodeResult.RecoverableError(id, error)) ->
                Assert.Equal(expectedId, id)
                Assert.Equal(expectedCode, error.Code)
            | result -> failwithf "%s: expected recoverable error, got %A" name result

        let input =
            [ yield Test.request 1u "initialize" Test.empty
              yield! recoverable |> List.map (fun (_, bytes, _, _) -> bytes)
              yield Test.request 2u "shutdown" Test.empty ]
            |> Array.concat

        let exitCode, stdout, stderr =
            Test.run (Test.defaultConfiguration RpcProfile.publicProfile) input

        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)

        Assert.Equal<(uint32 * string) list>(
            recoverable |> List.map (fun (_, _, id, code) -> id, code),
            Test.responseErrors stdout
        )

        match Test.frames stdout |> List.last with
        | Response(2u, None, _) -> ()
        | frame -> failwithf "Session did not continue to shutdown: %A" frame

    [<Fact>]
    member _.``fragmented and coalesced streams stay frame bounded``() =
        let limits =
            { RpcCodec.secureLimits with
                MaximumValueBytes = 4096 }

        let profile = Test.profile "streams" [ "large", Read; "shutdown", Control ]
        let initialize = Test.request 1u "initialize" Test.empty

        let large =
            Test.request 2u "large" (Test.map [ "payload", RpcValue.Binary(Array.zeroCreate 3900) ])

        let shutdown = Test.request 3u "shutdown" Test.empty
        Assert.InRange(large.Length, 3072, limits.MaximumValueBytes)

        let configuration =
            { Test.defaultConfiguration profile with
                Limits = limits
                GetOutboundFrameLimit = fun () -> limits.MaximumValueBytes }

        let cases: (string * (unit -> Stream) * int) list =
            [ "one-byte fragments", (fun () -> new ChunkedReadStream(Array.concat [ initialize; shutdown ], 1)), 2
              "coalesced near-limit frames",
              (fun () -> new MemoryStream(Array.concat [ initialize; large; shutdown ])),
              3 ]

        for name, createStream, expectedFrames in cases do
            use stream = createStream ()

            let exitCode, stdout, stderr =
                Test.runStream configuration stream CancellationToken.None |> _.Result

            Assert.True((exitCode = 0), $"{name}: exit {exitCode}, {stderr}")
            Assert.Equal(expectedFrames, (Test.frames stdout).Length)

    [<Fact>]
    member _.``clean and truncated EOF have distinct outcomes``() =
        let configuration = Test.defaultConfiguration RpcProfile.publicProfile

        let cases =
            [ "clean", Array.empty, 0, None
              "truncated", [| 0x94uy; 0uy; 1uy |], 65, Some "incomplete" ]

        for name, input, expectedExit, diagnostic in cases do
            let exitCode, stdout, stderr = Test.run configuration input
            Assert.True((exitCode = expectedExit), $"{name}: exit {exitCode}")
            Assert.Empty(stdout)

            match diagnostic with
            | Some text -> Assert.Contains(text, stderr)
            | None -> Assert.Equal(String.Empty, stderr)

    [<Fact>]
    member _.``initialization profiles and notification callability gate requests``() =
        let worker = Test.profile "worker" [ "msbuild/evaluate", Read; "shutdown", Control ]

        let notifications =
            Test.profile
                "notifications"
                [ "workspace/exportChunk", NotificationMethod
                  "operation/completed", NotificationMethod
                  "shutdown", Control ]

        let cases =
            [ "initialization",
              RpcProfile.publicProfile,
              [ Test.request 1u "workspace/root" Test.empty
                Test.request 2u "initialize" Test.empty
                Test.request 3u "initialize" Test.empty
                Test.request 4u "shutdown" Test.empty ],
              [ 1u, "not_initialized"; 3u, "invalid_request" ]
              "worker profile isolation",
              worker,
              [ Test.request 1u "initialize" Test.empty
                Test.request 2u "workspace/root" Test.empty
                Test.request 3u "msbuild/evaluate" Test.empty
                Test.request 4u "shutdown" Test.empty ],
              [ 2u, "unknown_method" ]
              "public profile isolation",
              RpcProfile.publicProfile,
              [ Test.request 1u "initialize" Test.empty
                Test.request 2u "msbuild/evaluate" Test.empty
                Test.request 3u "shutdown" Test.empty ],
              [ 2u, "unknown_method" ]
              "notifications are not callable",
              notifications,
              [ Test.request 1u "initialize" Test.empty
                Test.request 2u "workspace/exportChunk" Test.empty
                Test.request 3u "operation/completed" Test.empty
                Test.request 4u "shutdown" Test.empty ],
              [ 2u, "unknown_method"; 3u, "unknown_method" ] ]

        for name, profile, requests, expectedErrors in cases do
            let exitCode, stdout, stderr =
                requests |> Array.concat |> Test.run (Test.defaultConfiguration profile)

            Assert.True((exitCode = 0), $"{name}: exit {exitCode}, {stderr}")
            Assert.Equal<(uint32 * string) list>(expectedErrors, Test.responseErrors stdout)

    [<Fact>]
    member _.``public initialization and paging schemas remain stable``() =
        let initialize major client capabilities limits =
            let fields =
                ResizeArray<string * RpcValue>(
                    [ "protocolVersion", Test.map [ "major", RpcValue.Integer major; "minor", RpcValue.Integer 9L ]
                      "clientInfo", Test.map [ "name", RpcValue.String client ]
                      "capabilities", capabilities |> List.map RpcValue.String |> RpcValue.array ]
                )

            limits |> Option.iter (fun value -> fields.Add("limits", value))
            Test.map fields

        let valid =
            initialize
                1L
                "test"
                [ "workspace.root"; "unknown.claim"; "operation.cancel" ]
                (Some(Test.map [ "maxFrameBytes", RpcValue.Integer 4096L; "maxPageSize", RpcValue.Integer 50L ]))

        let request =
            PublicProtocol.parseInitialize valid
            |> Result.defaultWith (fun error -> failwith error.Message)

        Assert.Equal(0, request.ProtocolMinor)
        Assert.Equal(4096, request.MaximumFrameBytes)
        Assert.Equal(50, request.MaximumPageSize)

        let descriptor =
            WorkspaceDescriptor.Create(
                WorkspaceTargetPath.Create(Path.GetTempPath()),
                HostFileSystemCaseSemantics.Sensitive,
                WorkspaceFormat.Slnf,
                WorkspaceRevision.Create 0L,
                WorkspaceAccess.ReadWrite
            )

        let resultFields =
            PublicProtocol.initializeResult descriptor 0L request
            |> RpcValue.requireMap "initialize.result"

        Assert.Equal<string list>(
            [ "capabilities"; "limits"; "protocolVersion"; "serverInfo"; "workspace" ],
            resultFields.Keys |> Seq.sort |> Seq.toList
        )

        let negotiated =
            resultFields["capabilities"]
            |> RpcValue.requireArray "capabilities"
            |> Seq.map (RpcValue.requireString "capability")
            |> Seq.toList

        Assert.Equal<string list>([ "operation.cancel"; "workspace.root" ], negotiated)

        let resultLimits = resultFields["limits"] |> RpcValue.requireMap "limits"
        Assert.Equal(RpcValue.Integer 4096L, resultLimits["maxFrameBytes"])
        Assert.Equal(RpcValue.Integer 50L, resultLimits["maxPageSize"])

        let invalid =
            [ "missing fields", Test.empty
              "unsupported major", initialize 2L "test" [] None
              "blank client", initialize 1L "" [] None
              "duplicate capability", initialize 1L "test" [ "x"; "x" ] None ]

        for name, parameters in invalid do
            match PublicProtocol.parseInitialize parameters with
            | Error error -> Assert.Equal("invalid_params", error.Code)
            | Ok value -> failwithf "%s: expected invalid initialize, got %A" name value

        let defaults =
            initialize 1L "test" [] None
            |> PublicProtocol.parseInitialize
            |> Result.defaultWith (fun error -> failwith error.Message)

        Assert.Equal(256, defaults.MaximumPageSize)

        let children pageSize =
            PublicProtocol.parseRequest
                "workspace/children"
                (Test.map
                    [ "parentId", RpcValue.String "parent"
                      "pageSize", RpcValue.Integer pageSize
                      "continuationToken", RpcValue.String "next" ])

        match children 4096L with
        | Ok(PublicRequest.Children("parent", Some 4096, Some "next")) -> ()
        | result -> failwithf "maximum page and continuation schema changed: %A" result

        match children 4097L with
        | Error error -> Assert.Equal("invalid_params", error.Code)
        | result -> failwithf "oversized page should be rejected: %A" result

    [<Fact>]
    member _.``read cancellation exits 130 without protocol output``() =
        use cancellation = new CancellationTokenSource()
        use source = new CancellingReadStream(cancellation)

        let exitCode, stdout, stderr =
            Test.runStream (Test.defaultConfiguration RpcProfile.publicProfile) source cancellation.Token
            |> _.Result

        Assert.Equal(130, exitCode)
        Assert.Empty(stdout)
        Assert.Equal(String.Empty, stderr)

    [<Fact>]
    member _.``shutdown cancels background work before its final response``() =
        let profile = Test.profile "background" [ "start", Read; "shutdown", Control ]

        let dispatch _ methodName _ _ =
            if methodName = "start" then
                let background (sink: RpcNotificationSink) cancellationToken =
                    task {
                        try
                            do! Task.Delay(Timeout.Infinite, cancellationToken)
                        with :? OperationCanceledException ->
                            do! sink.WriteAsync(Notification("operation/completed", Test.empty))
                    }

                Task.FromResult(
                    Ok
                        { Test.dispatchResult Test.empty false with
                            BackgroundWork = Some background }
                )
            else
                Task.FromResult(Ok(Test.dispatchResult Test.empty true))

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let configuration =
            Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch

        let exitCode, stdout, stderr = Test.run configuration input
        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)

        match Test.frames stdout with
        | [ Response(1u, None, _)
            Response(2u, None, _)
            Notification("operation/completed", RpcValue.Map fields)
            Response(3u, None, _) ] -> Assert.Empty(fields)
        | frames -> failwithf "Shutdown ordering changed: %A" frames

    [<Fact>]
    member _.``output failure is fatal``() =
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
    member _.``background faults are fatal while reading and during shutdown``() =
        let cases = [ "while reading", false; "during shutdown", true ]

        for name, failOnCancellation in cases do
            let methods =
                if failOnCancellation then
                    [ "start", Read; "shutdown", Control ]
                else
                    [ "start", Read ]

            let profile = Test.profile $"fault-{name}" methods

            let dispatch _ methodName _ _ =
                if methodName = "start" then
                    let background (_: RpcNotificationSink) cancellationToken =
                        if failOnCancellation then
                            task {
                                try
                                    do! Task.Delay(Timeout.Infinite, cancellationToken)
                                with :? OperationCanceledException ->
                                    return raise (InvalidOperationException("shutdown fault"))
                            }
                        else
                            Task.FromException<unit>(InvalidOperationException("background fault"))

                    Task.FromResult(
                        Ok
                            { Test.dispatchResult Test.empty false with
                                BackgroundWork = Some background }
                    )
                else
                    Task.FromResult(Ok(Test.dispatchResult Test.empty true))

            let input =
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  if failOnCancellation then
                      Test.request 3u "shutdown" Test.empty ]
                |> Array.concat

            use source =
                if failOnCancellation then
                    new MemoryStream(input) :> Stream
                else
                    new BlockingAfterDataStream(input)

            let configuration =
                Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch

            let exitCode, stdout, stderr =
                Test.runStream configuration source CancellationToken.None |> _.Result

            Assert.True((exitCode = 65), $"{name}: exit {exitCode}")
            Assert.Equal(2, (Test.frames stdout).Length)
            Assert.Contains("background RPC operation failed", stderr)

    [<Fact>]
    member _.``outbound limits bound responses and background notifications``() =
        let limit = 1024
        let oversized = Test.map [ "payload", RpcValue.String(String('x', 5000)) ]

        let profile =
            Test.profile "limits" [ "big", Read; "start", Read; "shutdown", Control ]

        let run initialize dispatch input =
            let mutable outboundLimit = RpcCodec.secureLimits.MaximumValueBytes

            let configuration =
                Test.configurationWithLimit
                    profile
                    (fun () -> outboundLimit)
                    (fun parameters token ->
                        outboundLimit <- limit
                        initialize parameters token)
                    dispatch

            Test.run configuration input

        let assertBounded name stdout =
            Test.decode stdout
            |> List.iter (fun (_, size) -> Assert.True(size <= limit, $"{name}: {size}-byte frame"))

        let initializeInput =
            Array.concat [ Test.request 1u "initialize" Test.empty; Test.request 2u "big" Test.empty ]

        let initializeExit, initializeOutput, initializeError =
            run
                (fun _ _ -> Task.FromResult(Ok oversized))
                (fun _ _ _ _ -> Task.FromResult(Ok(Test.dispatchResult Test.empty false)))
                initializeInput

        Assert.Equal(0, initializeExit)
        Assert.Equal(String.Empty, initializeError)
        assertBounded "initialize" initializeOutput

        Assert.Equal<(uint32 * string) list>(
            [ 1u, "response_too_large"; 2u, "not_initialized" ],
            Test.responseErrors initializeOutput
        )

        let requestInput =
            Array.concat [ Test.request 1u "initialize" Test.empty; Test.request 2u "big" Test.empty ]

        let requestExit, requestOutput, requestError =
            run
                (fun _ _ -> Task.FromResult(Ok Test.empty))
                (fun _ _ _ _ -> Task.FromResult(Ok(Test.dispatchResult oversized false)))
                requestInput

        Assert.Equal(0, requestExit)
        Assert.Equal(String.Empty, requestError)
        assertBounded "request" requestOutput
        Assert.Equal<(uint32 * string) list>([ 2u, "response_too_large" ], Test.responseErrors requestOutput)

        let backgroundDispatch _ methodName _ _ =
            if methodName = "start" then
                let background (sink: RpcNotificationSink) _ =
                    task {
                        try
                            do! sink.WriteAsync(Notification("operation/output", oversized))
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
                        { Test.dispatchResult Test.empty false with
                            BackgroundWork = Some background }
                )
            else
                Task.FromResult(Ok(Test.dispatchResult Test.empty true))

        let backgroundInput =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let backgroundExit, backgroundOutput, backgroundError =
            run (fun _ _ -> Task.FromResult(Ok Test.empty)) backgroundDispatch backgroundInput

        Assert.Equal(0, backgroundExit)
        Assert.Equal(String.Empty, backgroundError)
        assertBounded "background" backgroundOutput

        Assert.Contains(
            Test.frames backgroundOutput,
            function
            | Notification("operation/completed", parameters) ->
                RpcValue.tryField "code" parameters = Some(RpcValue.String "response_too_large")
            | _ -> false
        )
