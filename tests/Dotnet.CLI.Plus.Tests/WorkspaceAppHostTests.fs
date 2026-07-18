namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
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

    let writeProject path =
        File.WriteAllText(
            path,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        )

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

    let buildConfiguration =
        let frameworkDirectory = DirectoryInfo(AppContext.BaseDirectory)

        if isNull frameworkDirectory.Parent then
            failwith "Could not determine the active build configuration."

        frameworkDirectory.Parent.Name

    let apphost =
        let root = repositoryRoot AppContext.BaseDirectory

        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.CLI.Plus.exe"
            else
                "Dotnet.CLI.Plus"

        Path.Combine(root, "src", "Dotnet.CLI.Plus", "bin", buildConfiguration, "net10.0", name)

    let globalJson =
        Path.Combine(repositoryRoot AppContext.BaseDirectory, "global.json")

    let fixturePath name =
        Path.Combine(AppContext.BaseDirectory, "ConformanceFixtures", name)

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

    let readFrameWithSize (child: Process) =
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
                | Ok(RpcFrameDecodeResult.Frame value) -> frame <- Some(value, length)
                | Ok(RpcFrameDecodeResult.RecoverableError _) -> failwith "Server stdout contained a request error."
                | Error error -> failwithf "Invalid apphost frame: %A" error
            | Ok _ -> failwith "The frame reader consumed an unexpected byte count."

        frame.Value

    let readFrame child = readFrameWithSize child |> fst

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

    let responseAfterWorkspaceNotifications (child: Process) id expectedRevision =
        let mutable revision = expectedRevision
        let mutable result = None
        let notifications = ResizeArray<RpcFrame>()

        while result.IsNone do
            match readFrame child with
            | Notification("workspace/delta", parameters) ->
                let baseRevision =
                    field "baseRevision" parameters |> RpcValue.requireInteger "baseRevision"

                let nextRevision =
                    field "newRevision" parameters |> RpcValue.requireInteger "newRevision"

                Assert.Equal(revision, baseRevision)
                Assert.True(nextRevision > baseRevision)
                revision <- nextRevision
                notifications.Add(Notification("workspace/delta", parameters))
            | Notification("workspace/reset", parameters) ->
                let nextRevision = field "revision" parameters |> RpcValue.requireInteger "revision"
                Assert.True(nextRevision > revision)
                revision <- nextRevision
                notifications.Add(Notification("workspace/reset", parameters))
            | Response(actual, error, value) when actual = id -> result <- Some(error, value)
            | frame -> failwithf "Expected workspace notification or response %d, got %A" id frame

        result.Value, revision, notifications |> Seq.toList

    let shutdown (child: Process) id =
        send child false (request id "shutdown" RpcValue.emptyMap)
        let frame, size = readFrameWithSize child
        Assert.True(size <= 1024)
        let error, result = response id frame
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

type WorkspaceAppHostTests() =
    [<Theory>]
    [<InlineData("solution")>]
    [<InlineData("sln")>]
    member _.``should serve a framed workspace session from the built apphost for both aliases``(alias: string) =
        let directory = PipeTest.temporaryDirectory "pipe-apphost"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()
            model.AddProject("Demo.fsproj", "Demo", null) |> ignore
            PipeTest.writeProject (Path.Combine(directory, "Demo.fsproj"))
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

                let folder = model.AddFolder "/nested/"
                model.AddProject("Second.fsproj", "Second", folder) |> ignore
                PipeTest.writeProject (Path.Combine(directory, "Second.fsproj"))
                PipeTest.save solution model

                let expected = PipeTest.map [ "expectedRevision", RpcValue.Integer 0L ]
                PipeTest.send child false (PipeTest.request 5u "workspace/refresh" expected)

                let (changedError, changedResult), observedRevision, observedNotifications =
                    PipeTest.responseAfterWorkspaceNotifications child 5u 0L

                let finalRevision =
                    match changedError with
                    | None ->
                        let changedRevision =
                            PipeTest.field "revision" changedResult |> RpcValue.requireInteger "revision"

                        Assert.True(changedRevision > observedRevision)

                        match PipeTest.readFrame child with
                        | Notification("workspace/delta", parameters) ->
                            Assert.Equal(
                                changedRevision - 1L,
                                PipeTest.field "baseRevision" parameters
                                |> RpcValue.requireInteger "baseRevision"
                            )

                            Assert.Equal(
                                changedRevision,
                                PipeTest.field "newRevision" parameters |> RpcValue.requireInteger "newRevision"
                            )

                            let added = HashSet<string>(StringComparer.Ordinal)
                            let mutable secondAdded = false

                            for change in PipeTest.field "changes" parameters |> RpcValue.requireArray "changes" do
                                if PipeTest.field "kind" change = RpcValue.String "add" then
                                    match PipeTest.field "parentId" change with
                                    | RpcValue.String parentId -> Assert.Contains(parentId, added)
                                    | RpcValue.Nil -> ()
                                    | value -> failwithf "Unexpected parent ID: %A" value

                                    PipeTest.field "node" change
                                    |> PipeTest.field "id"
                                    |> RpcValue.requireString "id"
                                    |> added.Add
                                    |> ignore

                                    if
                                        PipeTest.field "name" (PipeTest.field "node" change) = RpcValue.String "Second"
                                    then
                                        secondAdded <- true

                            Assert.True(secondAdded, "The refreshed delta did not add the Second project.")

                            changedRevision
                        | frame -> failwithf "Expected refresh delta, got %A" frame
                    | Some error ->
                        Assert.Equal("workspace_conflict", error.Code)
                        Assert.True(observedRevision > 0L)

                        Assert.Contains(
                            observedNotifications,
                            fun frame ->
                                match frame with
                                | Notification("workspace/delta", parameters) ->
                                    PipeTest.field "changes" parameters
                                    |> RpcValue.requireArray "changes"
                                    |> Seq.exists (fun change ->
                                        PipeTest.field "kind" change = RpcValue.String "add"
                                        && PipeTest.field "name" (PipeTest.field "node" change) = RpcValue.String
                                            "Second")
                                | _ -> false
                        )

                        PipeTest.send child false (PipeTest.request 9u "workspace/refresh" RpcValue.emptyMap)

                        let (recoveredError, recoveredResult), recoveredRevision, recoveredNotifications =
                            PipeTest.responseAfterWorkspaceNotifications child 9u observedRevision

                        Assert.True(recoveredError.IsNone)
                        Assert.Equal(RpcValue.Boolean false, PipeTest.field "reset" recoveredResult)
                        Assert.True(recoveredRevision >= observedRevision)
                        Assert.Empty(recoveredNotifications)
                        recoveredRevision

                Assert.True(finalRevision > 0L)

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
    member _.``should consume the public pipe lifecycle from headless neovim``() =
        let nvimAvailable =
            try
                let start = ProcessStartInfo("nvim")
                start.ArgumentList.Add("--version")
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                start.UseShellExecute <- false
                use nvim = Process.Start start
                not (isNull nvim) && nvim.WaitForExit(5000) && nvim.ExitCode = 0
            with :? ComponentModel.Win32Exception ->
                false

        if not nvimAvailable then
            raise (Xunit.Sdk.SkipException.ForSkip("Neovim is not available; T-014 will provision it for CI."))

        let directory = PipeTest.temporaryDirectory "nvim-conformance"

        try
            let solution = Path.Combine(directory, "Neovim.slnx")
            let model = SolutionModel()
            model.AddProject("Included.csproj", "Included", null) |> ignore
            File.Copy(PipeTest.fixturePath "Solutions/src/Included.csproj", Path.Combine(directory, "Included.csproj"))

            for index in 1..20 do
                let name = $"Project{index}"
                model.AddProject($"{name}.csproj", name, null) |> ignore
                PipeTest.writeProject (Path.Combine(directory, $"{name}.csproj"))

            PipeTest.save solution model

            let start = ProcessStartInfo("nvim")
            start.WorkingDirectory <- directory
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.UseShellExecute <- false

            for argument in
                [ "--clean"
                  "--headless"
                  "-u"
                  "NONE"
                  "-i"
                  "NONE"
                  "-l"
                  PipeTest.fixturePath "Neovim/conformance.lua"
                  PipeTest.apphost
                  solution
                  directory
                  PipeTest.globalJson ] do
                start.ArgumentList.Add argument

            use nvim = Process.Start start
            Assert.NotNull nvim
            let completed = nvim.WaitForExit(30000)

            if not completed then
                nvim.Kill(true)
                nvim.WaitForExit()

            Assert.True(completed, "The headless Neovim client did not complete its lifecycle.")
            let stdout = nvim.StandardOutput.ReadToEnd()
            let stderr = nvim.StandardError.ReadToEnd()
            Assert.True((nvim.ExitCode = 0), $"Neovim exited {nvim.ExitCode}: {stdout}{stderr}")
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should page hydrated children and watch a real project edit in the built apphost``() =
        let directory = PipeTest.temporaryDirectory "pipe-children-watch"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.fsproj")
            let model = SolutionModel()
            model.AddProject("Demo.fsproj", "Demo", null) |> ignore
            PipeTest.writeProject project
            PipeTest.save solution model
            use child = PipeTest.startPipe "solution" solution

            try
                let initialize =
                    PipeTest.map
                        [ "protocolVersion", PipeTest.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                          "clientInfo", PipeTest.map [ "name", RpcValue.String "watch-test" ]
                          "capabilities",
                          RpcValue.array
                              [ RpcValue.String "workspace.root"
                                RpcValue.String "workspace.children"
                                RpcValue.String "workspace.delta" ]
                          "limits",
                          PipeTest.map
                              [ "maxFrameBytes", RpcValue.Integer 65536L
                                "maxPageSize", RpcValue.Integer 100L ] ]

                PipeTest.send child false (PipeTest.request 1u "initialize" initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore
                PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let _, root = PipeTest.readFrame child |> PipeTest.response 2u

                let projectId =
                    PipeTest.field "nodes" root
                    |> RpcValue.requireArray "nodes"
                    |> Seq.filter (fun node -> PipeTest.field "kind" node = RpcValue.String "project")
                    |> Seq.map (PipeTest.field "id" >> RpcValue.requireString "id")
                    |> Seq.exactlyOne

                let children =
                    PipeTest.map [ "parentId", RpcValue.String projectId; "pageSize", RpcValue.Integer 1L ]

                PipeTest.send child false (PipeTest.request 3u "workspace/children" children)
                let childError, page = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.True(childError.IsNone)

                Assert.Single(PipeTest.field "nodes" page |> RpcValue.requireArray "nodes")
                |> ignore

                match PipeTest.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(0L, PipeTest.field "baseRevision" parameters |> RpcValue.requireInteger "revision")
                    Assert.Equal(1L, PipeTest.field "newRevision" parameters |> RpcValue.requireInteger "revision")
                | frame -> failwithf "Expected hydration delta, got %A" frame

                let token = PipeTest.field "nextToken" page |> RpcValue.requireString "nextToken"

                let forged =
                    token[.. token.Length - 2]
                    + (if token.EndsWith("A", StringComparison.Ordinal) then
                           "B"
                       else
                           "A")

                let invalidPage =
                    PipeTest.map
                        [ "parentId", RpcValue.String projectId
                          "pageSize", RpcValue.Integer 1L
                          "continuationToken", RpcValue.String forged ]

                PipeTest.send child false (PipeTest.request 4u "workspace/children" invalidPage)
                let tokenError, _ = PipeTest.readFrame child |> PipeTest.response 4u
                Assert.Equal("invalid_params", tokenError.Value.Code)

                File.WriteAllText(
                    project,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><WatchedValue>changed</WatchedValue></PropertyGroup></Project>"
                )

                let watching = Task.Run(fun () -> PipeTest.readFrame child)
                Assert.True(watching.Wait(TimeSpan.FromSeconds 10.0), "The watcher did not publish a transition.")
                let mutable watchedRevision = 1L

                match watching.Result with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(1L, PipeTest.field "baseRevision" parameters |> RpcValue.requireInteger "revision")
                    watchedRevision <- PipeTest.field "newRevision" parameters |> RpcValue.requireInteger "revision"
                    Assert.True(watchedRevision > 1L)
                | frame -> failwithf "Expected watcher delta, got %A" frame

                let mutable continuation = None
                let mutable requestId = 5u
                let mutable hasMore = true
                let mutable watchedValueFound = false

                while hasMore && not watchedValueFound do
                    let freshChildren =
                        [ "parentId", RpcValue.String projectId; "pageSize", RpcValue.Integer 100L ]
                        |> fun fields ->
                            continuation
                            |> Option.map (fun token -> ("continuationToken", RpcValue.String token) :: fields)
                            |> Option.defaultValue fields
                        |> PipeTest.map

                    PipeTest.send child false (PipeTest.request requestId "workspace/children" freshChildren)

                    let projectError, projectPage =
                        PipeTest.readFrame child |> PipeTest.response requestId

                    Assert.True(projectError.IsNone)

                    Assert.Equal(
                        watchedRevision,
                        PipeTest.field "revision" projectPage |> RpcValue.requireInteger "revision"
                    )

                    watchedValueFound <-
                        PipeTest.field "nodes" projectPage
                        |> RpcValue.requireArray "nodes"
                        |> Seq.exists (fun node ->
                            PipeTest.field "kind" node = RpcValue.String "projectItem"
                            && PipeTest.field "name" node = RpcValue.String "WatchedValue = changed")

                    continuation <-
                        match PipeTest.field "nextToken" projectPage with
                        | RpcValue.String token -> Some token
                        | RpcValue.Nil -> None
                        | value -> failwithf "Unexpected continuation token: %A" value

                    hasMore <- continuation.IsSome
                    requestId <- requestId + 1u

                Assert.True(watchedValueFound, "Fresh project paging did not expose WatchedValue = changed.")

                File.Copy(PipeTest.globalJson, Path.Combine(directory, "global.json"))
                let selection = Task.Run(fun () -> PipeTest.readFrame child)
                Assert.True(selection.Wait(TimeSpan.FromSeconds 10.0), "global.json creation was not observed.")

                match selection.Result with
                | Notification("workspace/reset", parameters) ->
                    let resetRevision =
                        PipeTest.field "revision" parameters |> RpcValue.requireInteger "revision"

                    Assert.True(resetRevision > watchedRevision)

                    PipeTest.send child false (PipeTest.request 100u "workspace/root" RpcValue.emptyMap)
                    let freshError, freshRoot = PipeTest.readFrame child |> PipeTest.response 100u
                    Assert.True(freshError.IsNone)

                    Assert.Equal(
                        resetRevision,
                        PipeTest.field "revision" freshRoot |> RpcValue.requireInteger "revision"
                    )
                | frame -> failwithf "Expected a toolset reset, got %A" frame

                PipeTest.shutdown child 101u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should reset the built apphost when a child hydration delta exceeds its frame limit``() =
        let directory = PipeTest.temporaryDirectory "pipe-children-delta-pressure"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()

            for name in [ "A"; "B" ] do
                model.AddProject($"{name}.fsproj", name, null) |> ignore
                PipeTest.writeProject (Path.Combine(directory, $"{name}.fsproj"))

            model.AddBuildType "D"
            PipeTest.save solution model

            let initialize maximumFrameBytes =
                PipeTest.map
                    [ "protocolVersion", PipeTest.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                      "clientInfo", PipeTest.map [ "name", RpcValue.String "child-pressure-test" ]
                      "capabilities",
                      RpcValue.array
                          [ RpcValue.String "workspace.root"
                            RpcValue.String "workspace.children"
                            RpcValue.String "workspace.delta" ]
                      "limits",
                      PipeTest.map
                          [ "maxFrameBytes", RpcValue.Integer maximumFrameBytes
                            "maxPageSize", RpcValue.Integer 2L ] ]

            let projectIds root =
                PipeTest.field "nodes" root
                |> RpcValue.requireArray "nodes"
                |> Seq.filter (fun node -> PipeTest.field "kind" node = RpcValue.String "project")
                |> Seq.sortBy (PipeTest.field "name" >> RpcValue.requireString "name")
                |> Seq.map (PipeTest.field "id" >> RpcValue.requireString "id")
                |> Seq.toArray

            use probe = PipeTest.startPipe "solution" solution

            try
                PipeTest.send probe false (PipeTest.request 1u "initialize" (initialize 65536L))
                PipeTest.readFrame probe |> PipeTest.response 1u |> ignore
                PipeTest.send probe false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let probeRootError, probeRoot = PipeTest.readFrame probe |> PipeTest.response 2u
                Assert.True(probeRootError.IsNone)

                let probeProjectIds = projectIds probeRoot
                Assert.Equal(2, probeProjectIds.Length)

                for index in 0..1 do
                    PipeTest.send
                        probe
                        false
                        (PipeTest.request
                            (uint32 (3 + index))
                            "workspace/children"
                            (PipeTest.map
                                [ "parentId", RpcValue.String probeProjectIds[index]
                                  "pageSize", RpcValue.Integer 1L ]))

                    let probeChildrenError, _ =
                        PipeTest.readFrame probe |> PipeTest.response (uint32 (3 + index))

                    Assert.True(probeChildrenError.IsNone)

                    match PipeTest.readFrame probe with
                    | Notification("workspace/delta", _) as delta when index = 1 ->
                        let deltaSize = (RpcCodec.encodeFrame delta).Length
                        Assert.True(deltaSize > 1024, $"Expected a delta above 1024 bytes, got {deltaSize}.")
                    | Notification("workspace/delta", _) -> ()
                    | frame -> failwithf "Expected child-hydration delta, got %A" frame

                PipeTest.shutdown probe 5u
            finally
                PipeTest.disposeProcess probe

            use child = PipeTest.startPipe "solution" solution

            try
                PipeTest.send child false (PipeTest.request 10u "initialize" (initialize 1024L))
                let initializeFrame, initializeSize = PipeTest.readFrameWithSize child
                Assert.True(initializeSize <= 1024)
                PipeTest.response 10u initializeFrame |> ignore

                PipeTest.send child false (PipeTest.request 11u "workspace/root" RpcValue.emptyMap)
                let rootFrame, rootSize = PipeTest.readFrameWithSize child
                Assert.True(rootSize <= 1024)
                let rootError, root = PipeTest.response 11u rootFrame
                Assert.True(rootError.IsNone, $"Expected bounded root, got {rootError}.")
                Assert.Equal(0L, PipeTest.field "revision" root |> RpcValue.requireInteger "revision")

                let childProjectIds = projectIds root
                Assert.Equal(2, childProjectIds.Length)

                PipeTest.send
                    child
                    false
                    (PipeTest.request
                        12u
                        "workspace/children"
                        (PipeTest.map
                            [ "parentId", RpcValue.String childProjectIds[0]
                              "pageSize", RpcValue.Integer 1L ]))

                let firstFrame, firstSize = PipeTest.readFrameWithSize child
                Assert.True(firstSize <= 1024)
                let firstError, firstPage = PipeTest.response 12u firstFrame
                Assert.True(firstError.IsNone)
                Assert.Equal(1L, PipeTest.field "revision" firstPage |> RpcValue.requireInteger "revision")

                let firstDelta, firstDeltaSize = PipeTest.readFrameWithSize child
                Assert.True(firstDeltaSize <= 1024)

                match firstDelta with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        0L,
                        PipeTest.field "baseRevision" parameters
                        |> RpcValue.requireInteger "baseRevision"
                    )

                    Assert.Equal(1L, PipeTest.field "newRevision" parameters |> RpcValue.requireInteger "newRevision")
                | frame -> failwithf "Expected in-limit child-hydration delta, got %A" frame

                PipeTest.send
                    child
                    false
                    (PipeTest.request
                        13u
                        "workspace/children"
                        (PipeTest.map
                            [ "parentId", RpcValue.String childProjectIds[1]
                              "pageSize", RpcValue.Integer 1L ]))

                let childrenFrame, childrenSize = PipeTest.readFrameWithSize child
                Assert.True(childrenSize <= 1024)
                let childrenError, page = PipeTest.response 13u childrenFrame
                Assert.True(childrenError.IsNone)
                Assert.Equal(2L, PipeTest.field "revision" page |> RpcValue.requireInteger "revision")

                let resetFrame, resetSize = PipeTest.readFrameWithSize child
                Assert.True(resetSize <= 1024)

                match resetFrame with
                | Notification("workspace/reset", parameters) ->
                    Assert.Equal(3L, PipeTest.field "revision" parameters |> RpcValue.requireInteger "revision")

                    let diagnostic =
                        PipeTest.field "diagnostics" parameters
                        |> RpcValue.requireArray "diagnostics"
                        |> Seq.exactlyOne

                    Assert.Equal(RpcValue.String "workspace.delta_pressure", PipeTest.field "code" diagnostic)
                | frame -> failwithf "Expected bounded child-hydration reset, got %A" frame

                PipeTest.send child false (PipeTest.request 14u "workspace/root" RpcValue.emptyMap)
                let freshFrame, freshSize = PipeTest.readFrameWithSize child
                Assert.True(freshSize <= 1024)
                let freshError, freshRoot = PipeTest.response 14u freshFrame
                Assert.True(freshError.IsNone)
                Assert.Equal(3L, PipeTest.field "revision" freshRoot |> RpcValue.requireInteger "revision")
                PipeTest.shutdown child 15u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should apply the global negotiated frame limit to responses errors and export notifications``() =
        let directory = PipeTest.temporaryDirectory "pipe-global-limit"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()

            for index in 1..2 do
                model.AddProject($"Project{index}.fsproj", $"Project{index}", null) |> ignore
                PipeTest.writeProject (Path.Combine(directory, $"Project{index}.fsproj"))

            model.AddProject("Oversized.fsproj", "Oversized", null) |> ignore
            PipeTest.writeProject (Path.Combine(directory, "Oversized.fsproj"))
            PipeTest.save solution model
            use child = PipeTest.startPipe "solution" solution

            try
                PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
                let initializeFrame, initializeSize = PipeTest.readFrameWithSize child
                Assert.True(initializeSize <= 1024)
                PipeTest.response 1u initializeFrame |> ignore

                PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let rootFrame, rootSize = PipeTest.readFrameWithSize child
                Assert.True(rootSize <= 1024)
                let rootError, _ = PipeTest.response 2u rootFrame
                Assert.Equal("response_too_large", rootError.Value.Code)

                let unknownMethod = String('m', 3000)
                PipeTest.send child false (PipeTest.request 3u unknownMethod RpcValue.emptyMap)
                let errorFrame, errorSize = PipeTest.readFrameWithSize child
                Assert.True(errorSize <= 1024)
                let methodError, _ = PipeTest.response 3u errorFrame
                Assert.Equal("response_too_large", methodError.Value.Code)

                PipeTest.send child false (PipeTest.request 4u "workspace/export" RpcValue.emptyMap)
                let exportFrame, exportSize = PipeTest.readFrameWithSize child
                Assert.True(exportSize <= 1024)
                let exportError, exportResult = PipeTest.response 4u exportFrame
                Assert.True(exportError.IsNone)

                let operationId =
                    PipeTest.field "operationId" exportResult
                    |> RpcValue.requireString "operationId"

                let mutable completed = false

                while not completed do
                    let frame, size = PipeTest.readFrameWithSize child
                    Assert.True(size <= 1024)

                    match frame with
                    | Notification("operation/completed", parameters) ->
                        Assert.Equal(RpcValue.String operationId, PipeTest.field "operationId" parameters)
                        Assert.Equal(RpcValue.String "succeeded", PipeTest.field "outcome" parameters)
                        completed <- true
                    | Notification("workspace/exportChunk", _) -> ()
                    | value -> failwithf "Unexpected globally bounded frame: %A" value

                PipeTest.shutdown child 5u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should isolate startup fatal and direct cli output in the built apphost``() =
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

            use orderlyEof = PipeTest.startPipe "solution" solution
            PipeTest.send orderlyEof false (PipeTest.request 1u "initialize" PipeTest.initialize)
            PipeTest.readFrame orderlyEof |> PipeTest.response 1u |> ignore
            PipeTest.send orderlyEof false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
            PipeTest.readFrame orderlyEof |> PipeTest.response 2u |> ignore
            orderlyEof.StandardInput.Close()
            Assert.True(orderlyEof.WaitForExit(5000), "The watched pipe did not exit after stdin closed.")
            Assert.Equal(0, orderlyEof.ExitCode)
            Assert.Equal(String.Empty, orderlyEof.StandardError.ReadToEnd())
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
