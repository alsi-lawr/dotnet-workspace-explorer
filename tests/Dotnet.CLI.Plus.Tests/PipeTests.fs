namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Threading
open Dotnet.CLI.Plus
open Dotnet.CLI.Plus.Transport
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private PipeTest =
    let request id name parameters =
        RpcCodec.encodeFrame (Request(id, name, parameters))

    let map values = RpcValue.map values

    let initialize =
        map
            [ "protocolVersion", map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
              "clientInfo", map [ "name", RpcValue.String "test" ]
              "capabilities",
              RpcValue.array
                  [ RpcValue.String "workspace.root"
                    RpcValue.String "workspace.export"
                    RpcValue.String "workspace.refresh"
                    RpcValue.String "operation.cancel"
                    RpcValue.String "unknown.claim" ]
              "limits", map [ "maxFrameBytes", RpcValue.Integer 1024L; "maxPageSize", RpcValue.Integer 50L ] ]

    let save path model =
        let serializer = SolutionSerializers.GetSerializerByMoniker path
        serializer.SaveAsync(path, model, CancellationToken.None).GetAwaiter().GetResult()

    let temporaryDirectory name =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-{name}-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let rec private repositoryRoot directory =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            directory
        else
            let parent = Directory.GetParent directory

            if isNull parent then
                failwith "Could not locate the repository root."

            repositoryRoot parent.FullName

    let apphost =
        let root = repositoryRoot AppContext.BaseDirectory

        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.CLI.Plus.exe"
            else
                "Dotnet.CLI.Plus"

        Path.Combine(root, "src", "Dotnet.CLI.Plus", "bin", "Release", "net10.0", name)

    let startPipe alias solution =
        let start = ProcessStartInfo(apphost)
        start.ArgumentList.Add alias
        start.ArgumentList.Add solution
        start.ArgumentList.Add "--pipe"
        start.UseShellExecute <- false
        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.CreateNoWindow <- true
        let child = Process.Start start

        if isNull child then
            failwith "Failed to start the built apphost."

        child

    let send (child: Process) fragmented bytes =
        if fragmented then
            for value in bytes do
                child.StandardInput.BaseStream.WriteByte value
                child.StandardInput.BaseStream.Flush()
        else
            child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
            child.StandardInput.BaseStream.Flush()

    let readFrame (child: Process) =
        let pending = ResizeArray<byte>()
        let mutable frame = None

        while frame.IsNone do
            let next = child.StandardOutput.BaseStream.ReadByte()

            if next < 0 then
                failwith "The apphost stdout ended before a complete frame was received."

            pending.Add(byte next)

            match RpcCodec.tryReadValueLength RpcCodec.secureLimits (pending.ToArray()) with
            | Error RpcDecodeError.Incomplete -> ()
            | Error error -> failwithf "Invalid apphost stdout: %A" error
            | Ok length when length = pending.Count ->
                match RpcCodec.decodeFrame RpcCodec.secureLimits (pending.ToArray()) with
                | Ok(RpcFrameDecodeResult.Frame value) -> frame <- Some value
                | Ok(RpcFrameDecodeResult.RecoverableError _) -> failwith "Server stdout contained a request error."
                | Error error -> failwithf "Invalid apphost frame: %A" error
            | Ok _ -> failwith "The frame reader consumed an unexpected byte count."

        frame.Value

    let readRemaining (stream: Stream) =
        use buffer = new MemoryStream()
        stream.CopyTo buffer
        buffer.ToArray()

    let response id =
        function
        | Response(actual, error, result) when actual = id -> error, result
        | frame -> failwithf "Expected response %d, got %A" id frame

    let fields value = RpcValue.requireMap "value" value

    let field name value =
        value |> fields |> RpcValue.requireField name

    let shutdown (child: Process) id =
        send child false (request id "shutdown" RpcValue.emptyMap)
        let error, result = readFrame child |> response id
        Assert.True(error.IsNone)
        Assert.Equal(RpcValue.Boolean true, field "accepted" result)
        child.StandardInput.Close()
        Assert.True(child.WaitForExit(5000), "The apphost did not exit after shutdown.")
        Assert.Equal(-1, child.StandardOutput.BaseStream.ReadByte())
        Assert.Equal(0, child.ExitCode)
        Assert.Equal(String.Empty, child.StandardError.ReadToEnd())

    let disposeProcess (child: Process) =
        if not child.HasExited then
            child.Kill(true)
            child.WaitForExit()

        child.Dispose()

type PipeTests() =
    [<Theory>]
    [<InlineData("solution")>]
    [<InlineData("sln")>]
    member _.``built apphost serves framed workspace session for both aliases``(alias: string) =
        let directory = PipeTest.temporaryDirectory "pipe-apphost"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()
            model.AddProject("Demo.fsproj", "Demo", null) |> ignore
            PipeTest.save solution model
            use child = PipeTest.startPipe alias solution

            try
                PipeTest.send child true (PipeTest.request 1u "initialize" PipeTest.initialize)

                let initializeError, initializeResult =
                    PipeTest.readFrame child |> PipeTest.response 1u

                Assert.True(initializeError.IsNone)

                Assert.Equal(
                    0L,
                    PipeTest.field "minor" (PipeTest.field "protocolVersion" initializeResult)
                    |> RpcValue.requireInteger "minor"
                )

                Assert.Equal(
                    4,
                    (PipeTest.field "capabilities" initializeResult
                     |> RpcValue.requireArray "capabilities")
                        .Length
                )

                PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let rootError, rootResult = PipeTest.readFrame child |> PipeTest.response 2u
                Assert.True(rootError.IsNone)
                Assert.Equal(0L, PipeTest.field "revision" rootResult |> RpcValue.requireInteger "revision")

                PipeTest.send child false (PipeTest.request 3u "workspace/export" RpcValue.emptyMap)
                let exportError, exportResult = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.True(exportError.IsNone)

                let operationId =
                    PipeTest.field "operationId" exportResult
                    |> RpcValue.requireString "operationId"

                let mutable sequence = 0L
                let mutable completed = false
                let mutable completions = 0

                while not completed do
                    let frame = PipeTest.readFrame child
                    Assert.True(RpcCodec.encodeFrame frame |> _.Length <= 1024)

                    match frame with
                    | Notification("workspace/exportChunk", parameters) ->
                        Assert.Equal(RpcValue.String operationId, PipeTest.field "operationId" parameters)

                        Assert.Equal(
                            sequence,
                            PipeTest.field "sequence" parameters |> RpcValue.requireInteger "sequence"
                        )

                        sequence <- sequence + 1L
                    | Notification("operation/completed", parameters) ->
                        Assert.Equal(RpcValue.String operationId, PipeTest.field "operationId" parameters)
                        Assert.Equal(RpcValue.String "succeeded", PipeTest.field "outcome" parameters)
                        completions <- completions + 1
                        completed <- true
                    | frame -> failwithf "Unexpected export frame: %A" frame

                Assert.Equal(1, completions)

                PipeTest.send child false (PipeTest.request 4u "workspace/refresh" RpcValue.emptyMap)
                let noOpError, noOpResult = PipeTest.readFrame child |> PipeTest.response 4u
                Assert.True(noOpError.IsNone)
                Assert.Equal(0L, PipeTest.field "revision" noOpResult |> RpcValue.requireInteger "revision")
                Assert.Equal(RpcValue.Boolean false, PipeTest.field "reset" noOpResult)

                model.AddProject("Second.fsproj", "Second", null) |> ignore
                PipeTest.save solution model

                let expected = PipeTest.map [ "expectedRevision", RpcValue.Integer 0L ]
                PipeTest.send child false (PipeTest.request 5u "workspace/refresh" expected)
                let changedError, changedResult = PipeTest.readFrame child |> PipeTest.response 5u
                Assert.True(changedError.IsNone)
                Assert.Equal(1L, PipeTest.field "revision" changedResult |> RpcValue.requireInteger "revision")
                Assert.Equal(RpcValue.Boolean true, PipeTest.field "reset" changedResult)

                PipeTest.send child false (PipeTest.request 6u "workspace/refresh" expected)
                let staleError, _ = PipeTest.readFrame child |> PipeTest.response 6u
                Assert.Equal("workspace_conflict", staleError.Value.Code)

                PipeTest.send child false (PipeTest.request 7u "msbuild/evaluate" RpcValue.emptyMap)
                let workerError, _ = PipeTest.readFrame child |> PipeTest.response 7u
                Assert.Equal("unknown_method", workerError.Value.Code)
                PipeTest.shutdown child 8u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``concurrent export cancellation is accepted and completes exactly once``() =
        let directory = PipeTest.temporaryDirectory "pipe-cancel"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()

            for index in 1..100 do
                model.AddProject($"Project{index}.fsproj", $"Project{index}", null) |> ignore

            PipeTest.save solution model
            use child = PipeTest.startPipe "solution" solution

            try
                PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore
                PipeTest.send child false (PipeTest.request 2u "workspace/export" RpcValue.emptyMap)
                let _, exportResult = PipeTest.readFrame child |> PipeTest.response 2u

                let operationId =
                    PipeTest.field "operationId" exportResult
                    |> RpcValue.requireString "operationId"

                let cancel = PipeTest.map [ "operationId", RpcValue.String operationId ]
                PipeTest.send child false (PipeTest.request 3u "operation/cancel" cancel)
                let cancelError, cancelResult = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.True(cancelError.IsNone)
                Assert.Equal(RpcValue.Boolean true, PipeTest.field "accepted" cancelResult)
                let mutable completions = 0
                let mutable completed = false

                while not completed do
                    match PipeTest.readFrame child with
                    | Notification("workspace/exportChunk", _) -> ()
                    | Notification("operation/completed", parameters) ->
                        Assert.Equal(RpcValue.String operationId, PipeTest.field "operationId" parameters)
                        Assert.Equal(RpcValue.String "cancelled", PipeTest.field "outcome" parameters)
                        Assert.NotEmpty(PipeTest.field "diagnostics" parameters |> RpcValue.requireArray "diagnostics")
                        completions <- completions + 1
                        completed <- true
                    | frame -> failwithf "Unexpected cancellation frame: %A" frame

                Assert.Equal(1, completions)
                PipeTest.shutdown child 4u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``slnf writes fail before deferred command handling``() =
        let directory = PipeTest.temporaryDirectory "pipe-filter"

        try
            let backing = Path.Combine(directory, "Demo.slnx")
            let filter = Path.Combine(directory, "Demo.slnf")
            PipeTest.save backing (SolutionModel())
            File.WriteAllText(filter, "{ \"solution\": { \"path\": \"Demo.slnx\" } }")

            let execute =
                PipeTest.map
                    [ "commandId", RpcValue.String "anything"
                      "arguments", RpcValue.emptyMap
                      "expectedRevision", RpcValue.Integer 0L ]

            let input =
                Array.concat
                    [ PipeTest.request 1u "initialize" PipeTest.initialize
                      PipeTest.request 2u "command/execute" execute
                      PipeTest.request 3u "shutdown" RpcValue.emptyMap ]

            use stdin = new MemoryStream(input)
            use stdout = new MemoryStream()
            use stderr = new StringWriter()
            Assert.Equal(0, Pipe.runAsync filter stdin stdout stderr CancellationToken.None |> _.Result)
            let bytes = stdout.ToArray()
            let mutable offset = 0
            let frames = ResizeArray<RpcFrame>()

            while offset < bytes.Length do
                let remaining = bytes[offset..]

                let length =
                    RpcCodec.tryReadValueLength RpcCodec.secureLimits remaining
                    |> Result.defaultWith (fun error -> failwithf "%A" error)

                match RpcCodec.decodeFrame RpcCodec.secureLimits remaining[.. length - 1] with
                | Ok(RpcFrameDecodeResult.Frame frame) -> frames.Add frame
                | result -> failwithf "%A" result

                offset <- offset + length

            Assert.Contains(
                frames,
                function
                | Response(2u, Some error, _) when error.Code = "unsupported_capability" -> true
                | _ -> false
            )
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``built apphost isolates startup fatal and direct cli output``() =
        let missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.slnx")
        use startup = PipeTest.startPipe "solution" missing
        startup.StandardInput.Close()
        Assert.True(startup.WaitForExit(5000))
        Assert.Equal(64, startup.ExitCode)
        Assert.Empty(PipeTest.readRemaining startup.StandardOutput.BaseStream)
        Assert.Contains("startup failure", startup.StandardError.ReadToEnd())

        let directory = PipeTest.temporaryDirectory "pipe-fatal"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            PipeTest.save solution (SolutionModel())
            use fatal = PipeTest.startPipe "solution" solution
            fatal.StandardInput.BaseStream.Write([| 0xd4uy; 0uy; 0uy |])
            fatal.StandardInput.Close()
            Assert.True(fatal.WaitForExit(5000))
            Assert.Equal(65, fatal.ExitCode)
            Assert.Empty(PipeTest.readRemaining fatal.StandardOutput.BaseStream)
            Assert.Contains("protocol failure", fatal.StandardError.ReadToEnd())
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

        let invalidDirectory = PipeTest.temporaryDirectory "pipe-invalid-initialize"

        try
            let solution = Path.Combine(invalidDirectory, "Demo.slnx")
            PipeTest.save solution (SolutionModel())
            use invalidInitialize = PipeTest.startPipe "solution" solution
            PipeTest.send invalidInitialize false (PipeTest.request 1u "initialize" RpcValue.emptyMap)

            let initializeError, _ =
                PipeTest.readFrame invalidInitialize |> PipeTest.response 1u

            Assert.Equal("invalid_params", initializeError.Value.Code)
            invalidInitialize.StandardInput.Close()
            Assert.True(invalidInitialize.WaitForExit(5000))
            Assert.Equal(0, invalidInitialize.ExitCode)
            Assert.Equal(String.Empty, invalidInitialize.StandardError.ReadToEnd())
        finally
            if Directory.Exists invalidDirectory then
                Directory.Delete(invalidDirectory, true)

        let start = ProcessStartInfo(PipeTest.apphost)
        start.ArgumentList.Add "--json"
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        use direct = Process.Start start
        Assert.NotNull direct
        Assert.True(direct.WaitForExit(5000))
        Assert.NotEqual(0, direct.ExitCode)
        Assert.StartsWith("{", direct.StandardOutput.ReadToEnd().TrimStart())
