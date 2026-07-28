namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport
open FsUnit.Xunit
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module internal PipeTest =
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
              "limits",
              map [ "maxFrameBytes", RpcValue.Integer 1024L; "maxPageSize", RpcValue.Integer 50L ] ]

    let save path model =
        let serializer = SolutionSerializers.GetSerializerByMoniker path
        serializer.SaveAsync(path, model, CancellationToken.None).GetAwaiter().GetResult()

    let writeProject path =
        File.WriteAllText(
            path,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework>"
            + "</PropertyGroup></Project>"
        )

    let temporaryDirectory name =
        let path =
            Path.Combine(AppContext.BaseDirectory, $".dotnet-cli-plus-{name}-{Guid.NewGuid():N}")

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
        let frameworkDirectory = DirectoryInfo AppContext.BaseDirectory

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

    let startApphost arguments environment =
        let start = ProcessStartInfo apphost

        for argument in arguments do
            start.ArgumentList.Add argument

        start.UseShellExecute <- false
        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.CreateNoWindow <- true

        for name, value in environment do
            start.Environment[name] <- value

        let child = Process.Start start

        if isNull child then
            failwith "Failed to start the built apphost."

        child

    let startPipeWithEnvironment alias solution environment =
        startApphost [ alias; solution; "--pipe" ] environment

    let startPipeWithDataHome alias solution dataHome =
        let environment =
            dataHome
            |> Option.map (fun path -> [ "XDG_DATA_HOME", path ])
            |> Option.defaultValue []

        startPipeWithEnvironment alias solution environment

    let startPipe alias solution =
        startPipeWithDataHome alias solution None

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
                | Ok(RpcFrameDecodeResult.RecoverableError _) ->
                    failwith "Server stdout contained a request error."
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
        Assert.True error.IsNone
        Assert.Equal(RpcValue.Boolean true, field "accepted" result)
        child.StandardInput.Close()
        Assert.True(child.WaitForExit 5000, "The apphost did not exit after shutdown.")
        Assert.Equal(-1, child.StandardOutput.BaseStream.ReadByte())
        Assert.Equal(0, child.ExitCode)
        Assert.Equal(String.Empty, child.StandardError.ReadToEnd())

    type ExportCapture =
        { Revision: int64
          Nodes: RpcValue array
          ChunkSizes: int array
          LastValues: bool array
          CompletionSequence: int64
          Outcome: string
          DiagnosticCodes: string array }

    let readExport child operationId expectedRevision =
        let nodes = ResizeArray<RpcValue>()
        let chunkSizes = ResizeArray<int>()
        let lastValues = ResizeArray<bool>()
        let mutable sequence = 0L
        let mutable completed = None

        while completed.IsNone do
            let frame, size = readFrameWithSize child
            Assert.True(size <= 1024, $"Export emitted a {size}-byte frame.")

            match frame with
            | Notification("workspace/exportChunk", parameters) ->
                Assert.Equal(RpcValue.String operationId, field "operationId" parameters)

                Assert.Equal(
                    expectedRevision,
                    field "revision" parameters |> RpcValue.requireInteger "revision"
                )

                Assert.Equal(
                    sequence,
                    field "sequence" parameters |> RpcValue.requireInteger "sequence"
                )

                field "nodes" parameters |> RpcValue.requireArray "nodes" |> Seq.iter nodes.Add

                let last =
                    match field "last" parameters with
                    | RpcValue.Boolean value -> value
                    | value -> failwithf "Unexpected export last value: %A" value

                chunkSizes.Add size
                lastValues.Add last
                sequence <- sequence + 1L
            | Notification("operation/completed", parameters) ->
                Assert.Equal(RpcValue.String operationId, field "operationId" parameters)

                Assert.Equal(
                    expectedRevision,
                    field "revision" parameters |> RpcValue.requireInteger "revision"
                )

                let completionSequence =
                    field "sequence" parameters |> RpcValue.requireInteger "sequence"

                Assert.Equal(sequence, completionSequence)

                let diagnosticCodes =
                    field "diagnostics" parameters
                    |> RpcValue.requireArray "diagnostics"
                    |> Seq.map (field "code" >> RpcValue.requireString "code")
                    |> Seq.toArray

                completed <-
                    Some(
                        completionSequence,
                        field "outcome" parameters |> RpcValue.requireString "outcome",
                        diagnosticCodes
                    )
            | value -> failwithf "Unexpected export frame: %A" value

        let completionSequence, outcome, diagnosticCodes = completed.Value

        { Revision = expectedRevision
          Nodes = nodes.ToArray()
          ChunkSizes = chunkSizes.ToArray()
          LastValues = lastValues.ToArray()
          CompletionSequence = completionSequence
          Outcome = outcome
          DiagnosticCodes = diagnosticCodes }

    let startExport child requestId =
        send child false (request requestId "workspace/export" RpcValue.emptyMap)
        let error, result = readFrame child |> response requestId
        Assert.True error.IsNone

        field "operationId" result |> RpcValue.requireString "operationId",
        field "revision" result |> RpcValue.requireInteger "revision"

    let disposeProcess (child: Process) =
        if not child.HasExited then
            child.Kill true
            child.WaitForExit()

        child.Dispose()

    let previewAndExecute child id commandId targetId arguments revision expectsDelta =
        let preview =
            map
                [ "commandId", RpcValue.String commandId
                  "targetId", RpcValue.String targetId
                  "arguments", arguments
                  "expectedRevision", RpcValue.Integer revision ]

        send child false (request id "command/preview" preview)
        let previewError, previewResult = readFrame child |> response id

        match previewError with
        | Some error -> failwithf "%s preview failed: %s: %s" commandId error.Code error.Message
        | None -> ()

        let execute =
            map
                [ "commandId", RpcValue.String commandId
                  "targetId", RpcValue.String targetId
                  "arguments", arguments
                  "expectedRevision", RpcValue.Integer revision
                  "previewId", field "previewId" previewResult ]

        send child false (request (id + 1u) "command/execute" execute)
        let executeError, _ = readFrame child |> response (id + 1u)

        match executeError with
        | Some error -> failwithf "%s execute failed: %s: %s" commandId error.Code error.Message
        | None -> ()

        if expectsDelta then
            match readFrame child with
            | Notification("workspace/delta", _) -> ()
            | frame -> failwithf "Expected mutation delta, got %A" frame

    type ProjectSession =
        { Directory: string
          Project: string
          Child: Process
          ProjectId: string }

    let openProjectWithSetup name setup (projectContents: string) =
        let directory = temporaryDirectory name
        let solution = Path.Combine(directory, "Demo.slnx")
        let project = Path.Combine(directory, "Demo.csproj")
        let model = SolutionModel()
        model.AddProject("Demo.csproj", "Demo", null) |> ignore
        setup directory
        File.WriteAllText(project, projectContents)
        save solution model
        let child = startPipe "solution" solution

        let sessionInitialize =
            map
                [ "protocolVersion",
                  map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
                  "clientInfo", map [ "name", RpcValue.String "test" ]
                  "capabilities",
                  RpcValue.array
                      [ RpcValue.String "workspace.root"
                        RpcValue.String "workspace.children"
                        RpcValue.String "workspace.delta"
                        RpcValue.String "workspace.export"
                        RpcValue.String "workspace.refresh"
                        RpcValue.String "operation.cancel" ]
                  "limits",
                  map
                      [ "maxFrameBytes", RpcValue.Integer 65536L
                        "maxPageSize", RpcValue.Integer 100L ] ]

        send child false (request 1u "initialize" sessionInitialize)
        readFrame child |> response 1u |> ignore
        send child false (request 2u "workspace/root" RpcValue.emptyMap)
        let error, root = readFrame child |> response 2u

        match error with
        | Some value -> failwithf "Workspace root failed: %s" value.Message
        | None ->
            let projectId =
                field "nodes" root
                |> RpcValue.requireArray "nodes"
                |> Seq.find (fun node -> field "kind" node = RpcValue.String "project")
                |> field "id"
                |> RpcValue.requireString "id"

            { Directory = directory
              Project = project
              Child = child
              ProjectId = projectId }

    let openProject name projectContents =
        openProjectWithSetup name ignore projectContents

    let closeProject session =
        try
            shutdown session.Child 99u
        finally
            disposeProcess session.Child

            if Directory.Exists session.Directory then
                Directory.Delete(session.Directory, true)

    let previewFailure session id commandId arguments revision =
        send
            session.Child
            false
            (request
                id
                "command/preview"
                (map
                    [ "commandId", RpcValue.String commandId
                      "targetId", RpcValue.String session.ProjectId
                      "arguments", arguments
                      "expectedRevision", RpcValue.Integer revision ]))

        let error, _ = readFrame session.Child |> response id
        Assert.Equal("invalid_input", error.Value.Code)

    let readAllProjectChildNames session firstRequestId expectedRevision =
        let names = ResizeArray<string>()
        let mutable continuation = None
        let mutable requestId = firstRequestId
        let mutable first = true

        while first || continuation.IsSome do
            let fields =
                [ "parentId", RpcValue.String session.ProjectId
                  "pageSize", RpcValue.Integer 100L ]
                |> fun fields ->
                    continuation
                    |> Option.map (fun token ->
                        ("continuationToken", RpcValue.String token) :: fields)
                    |> Option.defaultValue fields

            send session.Child false (request requestId "workspace/children" (map fields))

            let (error, page), _, _ =
                responseAfterWorkspaceNotifications session.Child requestId expectedRevision

            Assert.True error.IsNone

            field "nodes" page
            |> RpcValue.requireArray "nodes"
            |> Seq.iter (fun node -> names.Add(field "name" node |> RpcValue.requireString "name"))

            continuation <-
                match RpcValue.tryField "nextToken" page with
                | Some(RpcValue.String token) -> Some token
                | Some RpcValue.Nil
                | None -> None
                | Some value -> failwithf "Unexpected continuation token: %A" value

            requestId <- requestId + 1u
            first <- false

        names |> Seq.toArray

type WorkspaceAppHostTests() =
    [<Fact>]
    member _.``should accept only the reserved export worker startup grammar``() =
        let directory = PipeTest.temporaryDirectory "export-worker-cli"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            PipeTest.save solution (SolutionModel())

            use valid =
                PipeTest.startApphost [ "solution"; solution; "--pipe"; "--export-workers"; "1" ] []

            PipeTest.send valid false (PipeTest.request 1u "initialize" PipeTest.initialize)
            let initializeError, _ = PipeTest.readFrame valid |> PipeTest.response 1u
            Assert.True initializeError.IsNone
            PipeTest.shutdown valid 2u

            let invalidForms =
                [ [ "solution"; solution; "--export-workers"; "1" ]
                  [ "solution"; solution; "--pipe"; "--export-workers" ]
                  [ "solution"; solution; "--pipe"; "--export-workers"; "0" ]
                  [ "solution"; solution; "--pipe"; "--export-workers"; "-1" ]
                  [ "solution"; solution; "--pipe"; "--export-workers"; "+1" ]
                  [ "solution"; solution; "--pipe"; "--export-workers"; "1.0" ]
                  [ "solution"; solution; "--pipe"; "--export-workers"; "one" ]
                  [ "solution"; solution; "--pipe"; "--export-workers"; "" ]
                  [ "solution"; solution; "--pipe"; "--export-workers"; "2147483648" ]
                  [ "solution"; solution; "--export-workers"; "1"; "--pipe" ]
                  [ "solution"; solution; "--pipe"; "--export-workers=1" ]
                  [ "solution"; solution; "--pipe=true" ]
                  [ "solution"
                    solution
                    "--pipe"
                    "--export-workers"
                    "1"
                    "--export-workers"
                    "2" ]
                  [ "solution"; solution; "--pipe"; "--export-workers"; "1"; "extra" ]
                  [ "solution"; solution; "--pipe"; "--pipe" ]
                  [ "--json"; "solution"; solution; "--pipe" ] ]

            for arguments in invalidForms do
                use invalid = PipeTest.startApphost arguments []
                invalid.StandardInput.Close()
                Assert.True(invalid.WaitForExit 5000, "Invalid startup did not terminate.")
                Assert.Equal(64, invalid.ExitCode)
                Assert.Empty(PipeTest.readRemaining invalid.StandardOutput.BaseStream)

                Assert.Equal(
                    "dotnet-plus pipe startup failure: invalid pipe invocation.",
                    invalid.StandardError.ReadToEnd().Trim()
                )
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should bound export admission release canonically and recover after cancellation``
        ()
        =
        let runScenario capacity projectCount =
            let directory = PipeTest.temporaryDirectory "export-scheduler"

            try
                let solution = Path.Combine(directory, "Demo.slnx")
                let model = SolutionModel()

                let projects =
                    [| for index in 1..projectCount do
                           let name = $"{char (int 'A' + index - 1)}"
                           let path = Path.Combine(directory, $"{name}.fsproj")
                           PipeTest.writeProject path
                           model.AddProject(Path.GetFileName path, name, null) |> ignore
                           yield path |]

                PipeTest.save solution model

                let workspace =
                    match SolutionStore.OpenAsync(solution).Result with
                    | Success value -> value
                    | Failure failure -> failwithf "Could not open scheduler fixture: %A" failure

                let gates =
                    projects
                    |> Seq.map (fun path ->
                        path,
                        TaskCompletionSource<unit>
                            TaskCreationOptions.RunContinuationsAsynchronously)
                    |> dict

                use started = new SemaphoreSlim 0
                use completed = new SemaphoreSlim 0
                let metricsGate = obj ()
                let mutable active = 0
                let mutable maximumActive = 0
                let mutable disposedSessions = 0
                let emitted = ResizeArray<string>()

                let snapshot path =
                    EvaluationSnapshot(
                        WorkspaceArtifactPath.Create path,
                        ImmutableArray<EvaluationDimensionSnapshot>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        WorkspaceCapabilityProfile.Full,
                        ImmutableArray<WorkspaceCapabilityId>.Empty,
                        ImmutableArray<WorkspaceDiagnostic>.Empty
                    )

                let services =
                    { OpenAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<SolutionWorkspace>>(Success workspace)
                      EvaluateAsync =
                        fun _ _ _ -> failwith "Interactive evaluation was not expected."
                      InvalidateAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<MsBuildInvalidationKind>>(
                                Success MsBuildInvalidationKind.None
                            )
                      OpenExportSessionAsync =
                        fun _ observedCapacity _ ->
                            Assert.Equal(capacity, observedCapacity)

                            Task.FromResult<WorkspaceOutcome<WorkspaceExportSession>>(
                                Success
                                    { EvaluateAsync =
                                        fun projectPath _ ->
                                            task {
                                                lock metricsGate (fun () ->
                                                    active <- active + 1
                                                    maximumActive <- max maximumActive active)

                                                started.Release() |> ignore
                                                do! gates[projectPath.Value].Task

                                                lock metricsGate (fun () -> active <- active - 1)
                                                completed.Release() |> ignore
                                                return Success(snapshot projectPath.Value)
                                            }
                                      DisposeAsync =
                                        fun () ->
                                            Interlocked.Increment(&disposedSessions) |> ignore
                                            Task.CompletedTask }
                            )
                      RefreshAsync = fun () -> Task.CompletedTask
                      DisposeAsync = fun () -> Task.CompletedTask }

                let state =
                    WorkspaceState.Create(
                        solution,
                        workspace,
                        services,
                        { HydrationLimit = 32
                          ExportCapacity = capacity
                          TokenSecret = Array.create 32 1uy }
                    )

                let writeBatch (batch: WorkspaceExportBatch) =
                    lock emitted (fun () ->
                        batch.Nodes
                        |> Seq.filter (fun node -> node.NodeKind = WorkspaceNodeKind.Project)
                        |> Seq.iter (fun node -> emitted.Add node.Name))

                    Task.FromResult()

                let export =
                    state.ExportAsync(
                        workspace.WorkspaceDescriptor.WorkspaceRevision.Value,
                        writeBatch,
                        CancellationToken.None
                    )

                let initiallyAdmitted = min capacity projectCount

                for _ in 1..initiallyAdmitted do
                    Assert.True(started.Wait 5000, "An admitted evaluation did not start.")

                Assert.Equal(initiallyAdmitted, maximumActive)

                if capacity = 2 && projectCount = 4 then
                    gates[projects[1]].SetResult()
                    Assert.True(completed.Wait 5000, "The reverse completion did not settle.")
                    Assert.Empty emitted
                    gates[projects[0]].SetResult()

                    for _ in 1..2 do
                        Assert.True(
                            started.Wait 5000,
                            "The bounded window did not admit more work."
                        )

                    gates[projects[3]].SetResult()
                    gates[projects[2]].SetResult()
                else
                    projects |> Array.rev |> Array.iter (fun path -> gates[path].SetResult())

                match export.GetAwaiter().GetResult() with
                | Ok() -> ()
                | Error error -> failwithf "Export scheduler failed: %s" error.Message

                emitted
                |> Seq.toArray
                |> should equal (projects |> Array.map Path.GetFileNameWithoutExtension)

                Assert.Equal(1, disposedSessions)
                Assert.True(maximumActive <= min capacity projectCount)
                state.DisposeAsync().GetAwaiter().GetResult()
            finally
                if Directory.Exists directory then
                    Directory.Delete(directory, true)

        runScenario 2 4
        runScenario Int32.MaxValue 2

        let runCancellationScenario () =
            let directory = PipeTest.temporaryDirectory "export-scheduler-cancellation"

            try
                let solution = Path.Combine(directory, "Demo.slnx")
                let model = SolutionModel()

                let projects =
                    [| for name in [ "Alpha"; "Middle"; "Zulu" ] do
                           let path = Path.Combine(directory, $"{name}.fsproj")
                           PipeTest.writeProject path
                           model.AddProject(Path.GetFileName path, name, null) |> ignore
                           yield path |]

                PipeTest.save solution model

                let workspace =
                    match SolutionStore.OpenAsync(solution).Result with
                    | Success value -> value
                    | Failure failure -> failwithf "Could not open cancellation fixture: %A" failure

                let snapshot path =
                    EvaluationSnapshot(
                        WorkspaceArtifactPath.Create path,
                        ImmutableArray<EvaluationDimensionSnapshot>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        WorkspaceCapabilityProfile.Full,
                        ImmutableArray<WorkspaceCapabilityId>.Empty,
                        ImmutableArray<WorkspaceDiagnostic>.Empty
                    )

                let cancelled () =
                    Failure(
                        Cancelled(
                            OperationId.New(),
                            WorkspaceDiagnostic.CreateSimple(
                                WorkspaceDiagnosticSeverity.Error,
                                WorkspaceDiagnosticCode.Create "workspace.test_cancelled",
                                "The test export was cancelled.",
                                false,
                                CorrelationId.New()
                            )
                        )
                    )

                use started = new SemaphoreSlim 0
                let mutable openedSessions = 0
                let mutable settledEvaluations = 0
                let mutable disposedSessions = 0

                let services =
                    { OpenAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<SolutionWorkspace>>(Success workspace)
                      EvaluateAsync =
                        fun _ _ _ -> failwith "Interactive evaluation was not expected."
                      InvalidateAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<MsBuildInvalidationKind>>(
                                Success MsBuildInvalidationKind.None
                            )
                      OpenExportSessionAsync =
                        fun _ observedCapacity _ ->
                            Assert.Equal(3, observedCapacity)
                            let sessionNumber = Interlocked.Increment(&openedSessions)

                            Task.FromResult<WorkspaceOutcome<WorkspaceExportSession>>(
                                Success
                                    { EvaluateAsync =
                                        if sessionNumber = 1 then
                                            fun _ cancellationToken ->
                                                task {
                                                    let cancelledSignal =
                                                        TaskCompletionSource<unit>
                                                            TaskCreationOptions.RunContinuationsAsynchronously

                                                    use _registration =
                                                        cancellationToken.Register(fun () ->
                                                            cancelledSignal.TrySetResult()
                                                            |> ignore)

                                                    started.Release() |> ignore
                                                    do! cancelledSignal.Task

                                                    Interlocked.Increment(&settledEvaluations)
                                                    |> ignore

                                                    return cancelled ()
                                                }
                                        else
                                            fun projectPath _ ->
                                                Task.FromResult<WorkspaceOutcome<EvaluationSnapshot>>(
                                                    Success(snapshot projectPath.Value)
                                                )
                                      DisposeAsync =
                                        fun () ->
                                            if sessionNumber = 1 then
                                                Assert.Equal(projects.Length, settledEvaluations)

                                            Interlocked.Increment(&disposedSessions) |> ignore
                                            Task.CompletedTask }
                            )
                      RefreshAsync = fun () -> Task.CompletedTask
                      DisposeAsync = fun () -> Task.CompletedTask }

                let state =
                    WorkspaceState.Create(
                        solution,
                        workspace,
                        services,
                        { HydrationLimit = 32
                          ExportCapacity = 3
                          TokenSecret = Array.create 32 1uy }
                    )

                use cancellation = new CancellationTokenSource()
                let firstLastValues = ResizeArray<bool>()

                let firstExport =
                    state.ExportAsync(
                        workspace.WorkspaceDescriptor.WorkspaceRevision.Value,
                        (fun batch ->
                            firstLastValues.Add batch.IsFinal
                            Task.FromResult()),
                        cancellation.Token
                    )

                for _ in projects do
                    Assert.True(started.Wait 5000, "A default-capacity lane did not start.")

                Assert.False firstExport.IsCompleted
                Assert.Equal(0, settledEvaluations)
                cancellation.Cancel()

                match firstExport.GetAwaiter().GetResult() with
                | Error error -> Assert.Equal("cancelled", error.Code)
                | Ok() -> failwith "The blocked export unexpectedly succeeded."

                Assert.Equal(3, settledEvaluations)
                Assert.Equal(1, openedSessions)
                Assert.Equal(1, disposedSessions)
                Assert.DoesNotContain(true, firstLastValues)

                let secondLastValues = ResizeArray<bool>()

                let secondExport =
                    state.ExportAsync(
                        workspace.WorkspaceDescriptor.WorkspaceRevision.Value,
                        (fun batch ->
                            secondLastValues.Add batch.IsFinal
                            Task.FromResult()),
                        CancellationToken.None
                    )

                match secondExport.GetAwaiter().GetResult() with
                | Ok() -> ()
                | Error error -> failwithf "The fresh export failed: %s" error.Message

                Assert.Equal(2, openedSessions)
                Assert.Equal(2, disposedSessions)

                Assert.Equal<bool array>(
                    [| true |],
                    secondLastValues |> Seq.filter id |> Seq.toArray
                )

                state.DisposeAsync().GetAwaiter().GetResult()
            finally
                if Directory.Exists directory then
                    Directory.Delete(directory, true)

        runCancellationScenario ()

    [<Fact>]
    member _.``should honor Worker default Content items without redundant declarations``() =
        let session =
            PipeTest.openProjectWithSetup
                "worker-content-default-scenario"
                (fun directory ->
                    File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{}"))
                ("<Project Sdk=\"Microsoft.NET.Sdk.Worker\"><PropertyGroup>"
                 + "<TargetFramework>net10.0</TargetFramework>"
                 + "</PropertyGroup></Project>")

        try
            let settings = Path.Combine(session.Directory, "appsettings.json")
            let before = File.ReadAllBytes session.Project

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String settings; "itemType", RpcValue.String "Content" ])
                0L
                true

            Assert.Equal<byte>(before, File.ReadAllBytes session.Project)

            PipeTest.previewAndExecute
                session.Child
                5u
                "project.item.add"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String settings; "itemType", RpcValue.String "None" ])
                1L
                true

            let project = File.ReadAllText session.Project
            Assert.Contains("<Content Remove=\"appsettings.json\"", project)
            Assert.Contains("<None Include=\"appsettings.json\"", project)
            Assert.DoesNotContain("<Content Include=\"appsettings.json\"", project)

            let names = PipeTest.readAllProjectChildNames session 7u 2L

            Assert.Contains(
                names,
                fun name -> name.StartsWith("None: appsettings.json", StringComparison.Ordinal)
            )

            Assert.False(
                names
                |> Array.exists (fun name ->
                    name.StartsWith("Content: appsettings.json", StringComparison.Ordinal))
            )
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should honor Web wwwroot Content defaults and changing build action``() =
        let session =
            PipeTest.openProjectWithSetup
                "web-content-default-scenario"
                (fun directory ->
                    let wwwroot = Path.Combine(directory, "wwwroot")
                    Directory.CreateDirectory wwwroot |> ignore
                    File.WriteAllText(Path.Combine(wwwroot, "site.css"), "body {}"))
                ("<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup>"
                 + "<TargetFramework>net10.0</TargetFramework>"
                 + "</PropertyGroup></Project>")

        try
            let site = Path.Combine(session.Directory, "wwwroot", "site.css")
            let before = File.ReadAllBytes session.Project

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String site; "itemType", RpcValue.String "Content" ])
                0L
                true

            Assert.Equal<byte>(before, File.ReadAllBytes session.Project)

            PipeTest.previewAndExecute
                session.Child
                5u
                "project.item.set-build-action"
                session.ProjectId
                (PipeTest.map [ "path", RpcValue.String site; "itemType", RpcValue.String "None" ])
                1L
                true

            let project = File.ReadAllText session.Project
            Assert.Contains("<Content Remove=\"wwwroot/site.css\"", project)
            Assert.Contains("<None Include=\"wwwroot/site.css\"", project)
            Assert.DoesNotContain("<Content Include=\"wwwroot/site.css\"", project)

            let names = PipeTest.readAllProjectChildNames session 7u 2L

            Assert.Contains(
                names,
                fun name -> name.StartsWith("None: wwwroot/site.css", StringComparison.Ordinal)
            )

            Assert.False(
                names
                |> Array.exists (fun name ->
                    name.StartsWith("Content: wwwroot/site.css", StringComparison.Ordinal))
            )
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should keep directory item additions explicit only when needed``() =
        let session =
            PipeTest.openProjectWithSetup
                "item-glob-scenario"
                (fun directory ->
                    let included = Path.Combine(directory, "Included")
                    Directory.CreateDirectory included |> ignore
                    File.WriteAllText(Path.Combine(included, "Nested.cs"), "class Nested { }"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                 + "<TargetFramework>net10.0</TargetFramework>"
                 + "<DefaultItemExcludes>$(DefaultItemExcludes);Excluded.cs</DefaultItemExcludes>"
                 + "</PropertyGroup></Project>")

        try
            let before = File.ReadAllBytes session.Project
            let included = Path.Combine(session.Directory, "Included")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String included; "itemType", RpcValue.String "Compile" ])
                0L
                true

            Assert.Equal<byte>(before, File.ReadAllBytes session.Project)
            let excluded = Path.Combine(session.Directory, "Excluded.cs")

            PipeTest.previewAndExecute
                session.Child
                5u
                "project.item.new"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String excluded
                      "itemType", RpcValue.String "Compile"
                      "contents", RpcValue.String "class Excluded { }" ])
                1L
                true

            Assert.Contains("<Compile Include=\"Excluded.cs\"", File.ReadAllText session.Project)
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should normalize default directory items when adding a different build action``() =
        let session =
            PipeTest.openProjectWithSetup
                "directory-build-action-scenario"
                (fun directory ->
                    let assets = Path.Combine(directory, "Assets")
                    Directory.CreateDirectory assets |> ignore
                    File.WriteAllText(Path.Combine(assets, "Readme.txt"), "readme"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                 + "<TargetFramework>net10.0</TargetFramework>"
                 + "</PropertyGroup></Project>")

        try
            let assets = Path.Combine(session.Directory, "Assets")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String assets; "itemType", RpcValue.String "Content" ])
                0L
                true

            let project = File.ReadAllText session.Project
            Assert.Contains("<None Remove=\"Assets/**/*\"", project)
            Assert.Contains("<Content Include=\"Assets/**/*\"", project)

            let names = PipeTest.readAllProjectChildNames session 5u 1L

            Assert.Contains(
                names,
                fun name -> name.StartsWith("Content: Assets/Readme.txt", StringComparison.Ordinal)
            )

            Assert.False(
                names
                |> Array.exists (fun name ->
                    name.StartsWith("None: Assets/Readme.txt", StringComparison.Ordinal))
            )
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should copy or link external project files without local directory operands``() =
        let external = PipeTest.temporaryDirectory "external-item-scenario"
        let source = Path.Combine(external, "Source.txt")
        let link = Path.Combine(external, "Link.txt")
        File.WriteAllText(source, "copy")
        File.WriteAllText(link, "link")

        let session =
            PipeTest.openProject "external-item-scenario" "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        try
            let addArguments =
                PipeTest.map [ "path", RpcValue.String source; "itemType", RpcValue.String "None" ]

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                addArguments
                0L
                true

            Assert.Equal("copy", File.ReadAllText(Path.Combine(session.Directory, "Source.txt")))

            PipeTest.previewAndExecute
                session.Child
                5u
                "project.item.add"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String link
                      "itemType", RpcValue.String "Content"
                      "link", RpcValue.Boolean true ])
                1L
                true

            Assert.False(File.Exists(Path.Combine(session.Directory, "Link.txt")))
            Assert.Contains("<Link>Link.txt</Link>", File.ReadAllText session.Project)

            PipeTest.previewFailure
                session
                7u
                "project.item.add"
                (PipeTest.map
                    [ "path", RpcValue.String source; "itemType", RpcValue.String "Content" ])
                2L
        finally
            PipeTest.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should set metadata and build action through public project commands``() =
        let session =
            PipeTest.openProject "metadata-scenario" "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        try
            let source = Path.Combine(session.Directory, "Source.cs")
            File.WriteAllText(source, "class Source { }")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.item.set-metadata"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String source
                      "name", RpcValue.String "CopyToOutputDirectory"
                      "value", RpcValue.String "Always" ])
                0L
                true

            Assert.Contains("<Compile Update=\"Source.cs\"", File.ReadAllText session.Project)

            PipeTest.previewAndExecute
                session.Child
                5u
                "project.item.set-build-action"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String source; "itemType", RpcValue.String "Content" ])
                1L
                true

            Assert.Contains("<Content Include=\"Source.cs\"", File.ReadAllText session.Project)
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should refuse directory operands for file project commands``() =
        let session =
            PipeTest.openProject
                "directory-refusal-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        try
            let folder = Path.Combine(session.Directory, "Folder")
            Directory.CreateDirectory folder |> ignore

            [ "project.item.new",
              PipeTest.map [ "path", RpcValue.String folder; "itemType", RpcValue.String "Compile" ]
              "project.item.copy",
              PipeTest.map
                  [ "source", RpcValue.String folder
                    "path", RpcValue.String(Path.Combine(session.Directory, "Copy.cs"))
                    "itemType", RpcValue.String "Compile" ]
              "project.item.add",
              PipeTest.map
                  [ "path", RpcValue.String folder
                    "itemType", RpcValue.String "Compile"
                    "link", RpcValue.Boolean true ]
              "project.item.rename",
              PipeTest.map [ "path", RpcValue.String folder; "name", RpcValue.String "Renamed" ]
              "project.item.move",
              PipeTest.map [ "path", RpcValue.String folder; "destination", RpcValue.String folder ]
              "project.item.remove", PipeTest.map [ "path", RpcValue.String folder ]
              "project.item.delete", PipeTest.map [ "path", RpcValue.String folder ] ]
            |> List.iteri (fun index (command, arguments) ->
                PipeTest.previewFailure session (uint32 (3 + index)) command arguments 0L)
        finally
            PipeTest.closeProject session









    [<Fact>]
    member _.``should write a local curated property``() =
        let session =
            PipeTest.openProject "local-property-scenario" "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        try
            PipeTest.previewAndExecute
                session.Child
                3u
                "project.property.set"
                session.ProjectId
                (PipeTest.map
                    [ "name", RpcValue.String "RootNamespace"
                      "value", RpcValue.String "Demo.Root" ])
                0L
                true

            Assert.Contains(
                "<RootNamespace>Demo.Root</RootNamespace>",
                File.ReadAllText session.Project
            )
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should reject unsupported conditional property mutation``() =
        let session =
            PipeTest.openProject
                "conditional-property-scenario"
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                 + "<Version Condition=\"'$(MSBuildProjectName)' == 'Demo'\">1.0</Version>"
                 + "</PropertyGroup></Project>")

        try
            PipeTest.previewFailure
                session
                3u
                "project.property.set"
                (PipeTest.map [ "name", RpcValue.String "Version"; "value", RpcValue.String "2.0" ])
                0L
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should refuse project mutations for unknown project systems``() =
        let session =
            PipeTest.openProject
                "unknown-project-system-scenario"
                "<Project><PropertyGroup><Value>readable</Value></PropertyGroup></Project>"

        try
            PipeTest.send
                session.Child
                false
                (PipeTest.request
                    3u
                    "command/preview"
                    (PipeTest.map
                        [ "commandId", RpcValue.String "project.property.set"
                          "targetId", RpcValue.String session.ProjectId
                          "arguments",
                          PipeTest.map
                              [ "name", RpcValue.String "RootNamespace"
                                "value", RpcValue.String "Demo.Root" ]
                          "expectedRevision", RpcValue.Integer 0L ]))

            let error, _ = PipeTest.readFrame session.Child |> PipeTest.response 3u
            Assert.Equal("unsupported_capability", error.Value.Code)
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should rename move and remove file project items without directory mutation``() =
        let session =
            PipeTest.openProjectWithSetup
                "rename-move-scenario"
                (fun directory -> File.WriteAllText(Path.Combine(directory, "Move.txt"), "move"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
                 + "<Content Include=\"Move.txt\" /></ItemGroup></Project>")

        try
            let source = Path.Combine(session.Directory, "Move.txt")
            let renamed = Path.Combine(session.Directory, "Renamed.txt")
            let moved = Path.Combine(session.Directory, "Moved.txt")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.item.rename"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String source; "name", RpcValue.String "Renamed.txt" ])
                0L
                true

            PipeTest.previewAndExecute
                session.Child
                5u
                "project.item.move"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String renamed; "destination", RpcValue.String moved ])
                1L
                true

            PipeTest.previewAndExecute
                session.Child
                7u
                "project.item.remove"
                session.ProjectId
                (PipeTest.map [ "path", RpcValue.String moved ])
                2L
                true

            Assert.True(File.Exists moved)
            Assert.Contains("<None Remove=\"Moved.txt\"", File.ReadAllText session.Project)
            Assert.DoesNotContain("<None Include=\"Moved.txt\"", File.ReadAllText session.Project)
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should preserve external encoded imported property files``() =
        let external = PipeTest.temporaryDirectory "encoded-property-scenario"
        let props = Path.Combine(external, "Shared.props")
        let encoding = Encoding.GetEncoding 28591

        File.WriteAllBytes(
            props,
            encoding.GetBytes(
                "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\r\n"
                + "<Project>\r\n  <!-- café shared -->\r\n"
                + "  <PropertyGroup Condition=\"'$(MSBuildProjectName)' == 'Demo'\">"
                + "<AssemblyName>Café</AssemblyName></PropertyGroup>\r\n"
                + "</Project>\r\n"
            )
        )

        let session =
            PipeTest.openProject
                "encoded-property-scenario"
                ($"<Project Sdk=\"Microsoft.NET.Sdk\">"
                 + $"<Import Project=\"{props.Replace('\\', '/')}\" /></Project>")

        try
            PipeTest.previewAndExecute
                session.Child
                3u
                "project.property.set"
                session.ProjectId
                (PipeTest.map
                    [ "name", RpcValue.String "AssemblyName"
                      "value", RpcValue.String "After"
                      "scope", RpcValue.String props
                      "condition", RpcValue.String "'$(MSBuildProjectName)' == 'Demo'" ])
                0L
                true

            let contents = File.ReadAllText(props, encoding)
            Assert.Contains("encoding=\"iso-8859-1\"", contents)
            Assert.Contains("<!-- café shared -->", contents)
            Assert.Contains("\r\n", contents)
            Assert.Contains("Condition=\"'$(MSBuildProjectName)' == 'Demo'\"", contents)
            Assert.Contains("<AssemblyName>After</AssemblyName>", contents)

            PipeTest.send
                session.Child
                false
                (PipeTest.request
                    5u
                    "workspace/children"
                    (PipeTest.map
                        [ "parentId", RpcValue.String session.ProjectId
                          "pageSize", RpcValue.Integer 100L ]))

            let (childrenError, children), _, _ =
                PipeTest.responseAfterWorkspaceNotifications session.Child 5u 1L

            Assert.True childrenError.IsNone

            let names = ResizeArray<string>()

            let appendNames page =
                PipeTest.field "nodes" page
                |> RpcValue.requireArray "nodes"
                |> Seq.iter (fun node ->
                    names.Add(PipeTest.field "name" node |> RpcValue.requireString "name"))

            appendNames children

            let mutable continuation =
                match RpcValue.tryField "nextToken" children with
                | Some(RpcValue.String token) -> Some token
                | Some RpcValue.Nil
                | None -> None
                | Some value -> failwithf "Unexpected continuation token: %A" value

            let mutable requestId = 6u

            while continuation.IsSome do
                PipeTest.send
                    session.Child
                    false
                    (PipeTest.request
                        requestId
                        "workspace/children"
                        (PipeTest.map
                            [ "parentId", RpcValue.String session.ProjectId
                              "pageSize", RpcValue.Integer 100L
                              "continuationToken", RpcValue.String continuation.Value ]))

                let (pageError, page), _, _ =
                    PipeTest.responseAfterWorkspaceNotifications session.Child requestId 1L

                Assert.True pageError.IsNone
                appendNames page

                continuation <-
                    match RpcValue.tryField "nextToken" page with
                    | Some(RpcValue.String token) -> Some token
                    | Some RpcValue.Nil
                    | None -> None
                    | Some value -> failwithf "Unexpected continuation token: %A" value

                requestId <- requestId + 1u

            Assert.Contains(
                names,
                fun name ->
                    name.Contains("Evaluated AssemblyName = After", StringComparison.Ordinal)
            )

            Assert.Contains(
                names,
                fun name ->
                    name.Contains("Declared AssemblyName = After", StringComparison.Ordinal)
                    && name.Contains(
                        "condition: '$(MSBuildProjectName)' == 'Demo'",
                        StringComparison.Ordinal
                    )
            )
        finally
            PipeTest.closeProject session
            Directory.Delete(external, true)



    [<Fact>]
    member _.``should delete project files through the native trash boundary``() =
        let directory = PipeTest.temporaryDirectory "delete-trash-scenario"
        let trashHome = Path.Combine(directory, "data")
        let solution = Path.Combine(directory, "Demo.slnx")
        let project = Path.Combine(directory, "Demo.csproj")
        let deleted = Path.Combine(directory, "Delete.txt")
        let model = SolutionModel()
        model.AddProject("Demo.csproj", "Demo", null) |> ignore
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(deleted, "delete")
        Directory.CreateDirectory trashHome |> ignore
        PipeTest.save solution model

        use child =
            if OperatingSystem.IsLinux() then
                PipeTest.startPipeWithDataHome "solution" solution (Some trashHome)
            else
                PipeTest.startPipe "solution" solution

        try
            PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
            PipeTest.readFrame child |> PipeTest.response 1u |> ignore
            PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
            let _, root = PipeTest.readFrame child |> PipeTest.response 2u

            let projectId =
                PipeTest.field "nodes" root
                |> RpcValue.requireArray "nodes"
                |> Seq.find (fun node -> PipeTest.field "kind" node = RpcValue.String "project")
                |> PipeTest.field "id"
                |> RpcValue.requireString "id"

            PipeTest.previewAndExecute
                child
                3u
                "project.item.delete"
                projectId
                (PipeTest.map [ "path", RpcValue.String deleted ])
                0L
                true

            Assert.False(File.Exists deleted)
            Assert.Contains("<None Remove=\"Delete.txt\"", File.ReadAllText project)

            if OperatingSystem.IsLinux() then
                let trashed =
                    Directory.EnumerateFiles(Path.Combine(trashHome, "Trash", "files"))
                    |> Seq.exactlyOne

                Assert.Equal("delete", File.ReadAllText trashed)

            PipeTest.shutdown child 5u
        finally
            PipeTest.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Theory>]
    [<InlineData("solution")>]
    [<InlineData("sln")>]
    member _.``should serve a framed workspace session from the built apphost for both aliases``
        (alias: string)
        =
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

                Assert.True initializeError.IsNone

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
                Assert.True rootError.IsNone

                Assert.Equal(
                    0L,
                    PipeTest.field "revision" rootResult |> RpcValue.requireInteger "revision"
                )

                PipeTest.send child false (PipeTest.request 3u "workspace/export" RpcValue.emptyMap)
                let exportError, exportResult = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.True exportError.IsNone

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
                        Assert.Equal(
                            RpcValue.String operationId,
                            PipeTest.field "operationId" parameters
                        )

                        Assert.Equal(
                            sequence,
                            PipeTest.field "sequence" parameters
                            |> RpcValue.requireInteger "sequence"
                        )

                        sequence <- sequence + 1L
                    | Notification("operation/completed", parameters) ->
                        Assert.Equal(
                            RpcValue.String operationId,
                            PipeTest.field "operationId" parameters
                        )

                        Assert.Equal(
                            RpcValue.String "succeeded",
                            PipeTest.field "outcome" parameters
                        )

                        completions <- completions + 1
                        completed <- true
                    | frame -> failwithf "Unexpected export frame: %A" frame

                Assert.Equal(1, completions)

                PipeTest.send
                    child
                    false
                    (PipeTest.request 4u "workspace/refresh" RpcValue.emptyMap)

                let noOpError, noOpResult = PipeTest.readFrame child |> PipeTest.response 4u
                Assert.True noOpError.IsNone

                Assert.Equal(
                    0L,
                    PipeTest.field "revision" noOpResult |> RpcValue.requireInteger "revision"
                )

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
                            PipeTest.field "revision" changedResult
                            |> RpcValue.requireInteger "revision"

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
                                PipeTest.field "newRevision" parameters
                                |> RpcValue.requireInteger "newRevision"
                            )

                            let added = HashSet<string> StringComparer.Ordinal
                            let mutable secondAdded = false

                            for change in
                                PipeTest.field "changes" parameters
                                |> RpcValue.requireArray "changes" do
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

                                    let name = PipeTest.field "name" (PipeTest.field "node" change)

                                    if name = RpcValue.String "Second" then
                                        secondAdded <- true

                            Assert.True(
                                secondAdded,
                                "The refreshed delta did not add the Second project."
                            )

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
                                        let node = PipeTest.field "node" change

                                        PipeTest.field "kind" change = RpcValue.String "add"
                                        && PipeTest.field "name" node = RpcValue.String "Second")
                                | _ -> false
                        )

                        PipeTest.send
                            child
                            false
                            (PipeTest.request 9u "workspace/refresh" RpcValue.emptyMap)

                        let ((recoveredError, recoveredResult),
                             recoveredRevision,
                             recoveredNotifications) =
                            PipeTest.responseAfterWorkspaceNotifications child 9u observedRevision

                        Assert.True recoveredError.IsNone
                        Assert.Equal(RpcValue.Boolean false, PipeTest.field "reset" recoveredResult)
                        Assert.True(recoveredRevision >= observedRevision)
                        Assert.Empty recoveredNotifications
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
    member _.``should stream repeatable bounded exports with stable identity cardinality and order``
        ()
        =
        let directory = PipeTest.temporaryDirectory "pipe-bounded-export-order"

        let projectContents prefix =
            let items =
                [ for index in 1..48 ->
                      $"<Compile Include=\"{prefix}/{String('x', 48)}-{index:D3}.cs\" />" ]
                |> String.concat String.Empty

            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework>"
            + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
            + $"</PropertyGroup><ItemGroup>{items}</ItemGroup></Project>"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()

            for name in [ "Zulu"; "Alpha"; "Middle" ] do
                model.AddProject($"{name}.csproj", name, null) |> ignore
                File.WriteAllText(Path.Combine(directory, $"{name}.csproj"), projectContents name)

            PipeTest.save solution model
            use child = PipeTest.startPipe "solution" solution

            try
                PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore

                let firstId, firstRevision = PipeTest.startExport child 2u
                let first = PipeTest.readExport child firstId firstRevision
                let secondId, secondRevision = PipeTest.startExport child 3u
                let second = PipeTest.readExport child secondId secondRevision

                Assert.Equal(firstRevision, secondRevision)
                Assert.Equal("succeeded", first.Outcome)
                Assert.Equal("succeeded", second.Outcome)
                Assert.True(first.ChunkSizes.Length > 1)
                Assert.True(first.ChunkSizes |> Array.max >= 768)
                Assert.Equal<bool array>([| true |], first.LastValues |> Array.filter id)
                Assert.True(first.LastValues[first.LastValues.Length - 1])
                Assert.Equal(first.Nodes.Length, second.Nodes.Length)
                Assert.Equal<int array>(first.ChunkSizes, second.ChunkSizes)

                let nodeShape node =
                    let capabilities =
                        PipeTest.field "capabilities" node
                        |> RpcValue.requireArray "capabilities"
                        |> Seq.map (RpcValue.requireString "capability")
                        |> String.concat ","

                    String.concat
                        "\u001f"
                        [ PipeTest.field "id" node |> RpcValue.requireString "id"
                          PipeTest.field "kind" node |> RpcValue.requireString "kind"
                          PipeTest.field "name" node |> RpcValue.requireString "name"
                          PipeTest.field "loadState" node |> RpcValue.requireString "loadState"
                          capabilities ]

                let firstShapes = first.Nodes |> Array.map nodeShape
                let secondShapes = second.Nodes |> Array.map nodeShape
                Assert.Equal<string array>(firstShapes, secondShapes)

                let nodeIds =
                    first.Nodes |> Array.map (PipeTest.field "id" >> RpcValue.requireString "id")

                Assert.Equal(nodeIds.Length, nodeIds |> Array.distinct |> Array.length)

                let projectNames =
                    first.Nodes
                    |> Array.filter (fun node ->
                        PipeTest.field "kind" node = RpcValue.String "project")
                    |> Array.map (PipeTest.field "name" >> RpcValue.requireString "name")

                Assert.Equal<string array>([| "Alpha"; "Middle"; "Zulu" |], projectNames)
                PipeTest.shutdown child 4u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should fail a later export evaluation after non-final chunks exactly once``() =
        let directory = PipeTest.temporaryDirectory "pipe-bounded-export-failure"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let first = Path.Combine(directory, "Alpha.csproj")
            let missing = Path.Combine(directory, "Zulu.csproj")
            let model = SolutionModel()
            model.AddProject("Alpha.csproj", "Alpha", null) |> ignore
            model.AddProject("Zulu.csproj", "Zulu", null) |> ignore
            PipeTest.writeProject first
            PipeTest.writeProject missing
            PipeTest.save solution model
            use child = PipeTest.startPipe "solution" solution

            try
                PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore
                File.Delete missing

                let operationId, revision = PipeTest.startExport child 2u
                let exported = PipeTest.readExport child operationId revision

                Assert.Equal("failed", exported.Outcome)
                Assert.Contains("not_found", exported.DiagnosticCodes)
                Assert.True(exported.ChunkSizes.Length > 0)
                Assert.DoesNotContain(true, exported.LastValues)
                Assert.Equal(int64 exported.ChunkSizes.Length, exported.CompletionSequence)

                PipeTest.send
                    child
                    false
                    (PipeTest.request
                        3u
                        "operation/cancel"
                        (PipeTest.map [ "operationId", RpcValue.String operationId ]))

                let cancelError, cancelResult = PipeTest.readFrame child |> PipeTest.response 3u

                Assert.True cancelError.IsNone
                Assert.Equal(RpcValue.Boolean false, PipeTest.field "accepted" cancelResult)
                PipeTest.shutdown child 4u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should cancel a streaming export without a final chunk and release its operation``
        ()
        =
        let directory = PipeTest.temporaryDirectory "pipe-bounded-export-cancel"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.csproj")
            let model = SolutionModel()
            model.AddProject("Demo.csproj", "Demo", null) |> ignore

            let items =
                [ for index in 1..2500 ->
                      $"<Compile Include=\"generated/{String('y', 48)}-{index:D4}.cs\" />" ]
                |> String.concat String.Empty

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                + $"</PropertyGroup><ItemGroup>{items}</ItemGroup></Project>"
            )

            PipeTest.save solution model
            use child = PipeTest.startPipe "solution" solution

            try
                PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore
                let operationId, revision = PipeTest.startExport child 2u

                let firstFrame, firstSize = PipeTest.readFrameWithSize child
                Assert.True(firstSize <= 1024)

                match firstFrame with
                | Notification("workspace/exportChunk", parameters) ->
                    Assert.Equal(RpcValue.Boolean false, PipeTest.field "last" parameters)

                    Assert.Equal(
                        0L,
                        PipeTest.field "sequence" parameters |> RpcValue.requireInteger "sequence"
                    )
                | frame -> failwithf "Expected the first non-final export chunk, got %A" frame

                PipeTest.send
                    child
                    false
                    (PipeTest.request
                        3u
                        "operation/cancel"
                        (PipeTest.map [ "operationId", RpcValue.String operationId ]))

                let mutable sequence = 1L
                let mutable cancelResult = None

                while cancelResult.IsNone do
                    match PipeTest.readFrame child with
                    | Notification("workspace/exportChunk", parameters) ->
                        Assert.Equal(RpcValue.Boolean false, PipeTest.field "last" parameters)

                        Assert.Equal(
                            sequence,
                            PipeTest.field "sequence" parameters
                            |> RpcValue.requireInteger "sequence"
                        )

                        sequence <- sequence + 1L
                    | Response(3u, error, result) ->
                        Assert.True error.IsNone
                        cancelResult <- Some result
                    | frame -> failwithf "Unexpected frame before cancellation response: %A" frame

                Assert.Equal(RpcValue.Boolean true, PipeTest.field "accepted" cancelResult.Value)

                match PipeTest.readFrame child with
                | Notification("operation/completed", parameters) ->
                    Assert.Equal(
                        RpcValue.String operationId,
                        PipeTest.field "operationId" parameters
                    )

                    Assert.Equal(RpcValue.String "cancelled", PipeTest.field "outcome" parameters)

                    Assert.Equal(
                        revision,
                        PipeTest.field "revision" parameters |> RpcValue.requireInteger "revision"
                    )

                    Assert.Equal(
                        sequence,
                        PipeTest.field "sequence" parameters |> RpcValue.requireInteger "sequence"
                    )
                | frame -> failwithf "Expected one cancelled completion, got %A" frame

                PipeTest.send
                    child
                    false
                    (PipeTest.request
                        4u
                        "operation/cancel"
                        (PipeTest.map [ "operationId", RpcValue.String operationId ]))

                let retryError, retryResult = PipeTest.readFrame child |> PipeTest.response 4u

                Assert.True retryError.IsNone
                Assert.Equal(RpcValue.Boolean false, PipeTest.field "accepted" retryResult)
                PipeTest.shutdown child 5u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should consume the public pipe lifecycle from headless neovim``() =
        let nvimAvailable =
            try
                let start = ProcessStartInfo "nvim"
                start.ArgumentList.Add "--version"
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                start.UseShellExecute <- false
                use nvim = Process.Start start
                not (isNull nvim) && nvim.WaitForExit 5000 && nvim.ExitCode = 0
            with :? ComponentModel.Win32Exception ->
                false

        if not nvimAvailable then
            raise (
                Sdk.SkipException.ForSkip
                    "Neovim is not available; native editor coverage is unavailable."

            )

        let directory = PipeTest.temporaryDirectory "nvim-conformance"

        try
            let solution = Path.Combine(directory, "Neovim.slnx")
            let model = SolutionModel()
            model.AddProject("Included.csproj", "Included", null) |> ignore

            File.Copy(
                PipeTest.fixturePath "Solutions/src/Included.csproj",
                Path.Combine(directory, "Included.csproj")
            )

            for index in 1..20 do
                let name = $"Project{index}"
                model.AddProject($"{name}.csproj", name, null) |> ignore
                PipeTest.writeProject (Path.Combine(directory, $"{name}.csproj"))

            PipeTest.save solution model

            let start = ProcessStartInfo "nvim"
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
            let completed = nvim.WaitForExit 30000

            if not completed then
                nvim.Kill true
                nvim.WaitForExit()

            Assert.True(completed, "The headless Neovim client did not complete its lifecycle.")
            let stdout = nvim.StandardOutput.ReadToEnd()
            let stderr = nvim.StandardError.ReadToEnd()
            Assert.True((nvim.ExitCode = 0), $"Neovim exited {nvim.ExitCode}: {stdout}{stderr}")
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should page hydrated children watch an edit and rebase commands after reset``() =
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
                        [ "protocolVersion",
                          PipeTest.map
                              [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                          "clientInfo", PipeTest.map [ "name", RpcValue.String "watch-test" ]
                          "capabilities",
                          RpcValue.array
                              [ RpcValue.String "workspace.root"
                                RpcValue.String "workspace.children"
                                RpcValue.String "workspace.delta"
                                RpcValue.String "command.list" ]
                          "limits",
                          PipeTest.map
                              [ "maxFrameBytes", RpcValue.Integer 65536L
                                "maxPageSize", RpcValue.Integer 100L ] ]

                PipeTest.send child false (PipeTest.request 1u "initialize" initialize)

                let initializeError, initializeResult =
                    PipeTest.readFrame child |> PipeTest.response 1u

                Assert.True initializeError.IsNone

                let workspaceId =
                    PipeTest.field "workspace" initializeResult
                    |> PipeTest.field "id"
                    |> RpcValue.requireString "id"

                PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let _, root = PipeTest.readFrame child |> PipeTest.response 2u

                let projectId =
                    PipeTest.field "nodes" root
                    |> RpcValue.requireArray "nodes"
                    |> Seq.filter (fun node ->
                        PipeTest.field "kind" node = RpcValue.String "project")
                    |> Seq.map (PipeTest.field "id" >> RpcValue.requireString "id")
                    |> Seq.exactlyOne

                let children =
                    PipeTest.map
                        [ "parentId", RpcValue.String projectId; "pageSize", RpcValue.Integer 1L ]

                PipeTest.send child false (PipeTest.request 3u "workspace/children" children)
                let childError, page = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.True childError.IsNone

                Assert.Single(PipeTest.field "nodes" page |> RpcValue.requireArray "nodes")
                |> ignore

                match PipeTest.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        0L,
                        PipeTest.field "baseRevision" parameters
                        |> RpcValue.requireInteger "revision"
                    )

                    Assert.Equal(
                        1L,
                        PipeTest.field "newRevision" parameters
                        |> RpcValue.requireInteger "revision"
                    )
                | frame -> failwithf "Expected hydration delta, got %A" frame

                let token = PipeTest.field "nextToken" page |> RpcValue.requireString "nextToken"

                let forged =
                    token[.. token.Length - 2]
                    + if token.EndsWith("A", StringComparison.Ordinal) then
                          "B"
                      else
                          "A"

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
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                    + "<TargetFramework>net10.0</TargetFramework>"
                    + "<WatchedValue>changed</WatchedValue>"
                    + "</PropertyGroup></Project>"
                )

                let watching = Task.Run(fun () -> PipeTest.readFrame child)

                Assert.True(
                    watching.Wait(TimeSpan.FromSeconds 10.0),
                    "The watcher did not publish a transition."
                )

                let mutable watchedRevision = 1L

                match watching.Result with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        1L,
                        PipeTest.field "baseRevision" parameters
                        |> RpcValue.requireInteger "revision"
                    )

                    watchedRevision <-
                        PipeTest.field "newRevision" parameters
                        |> RpcValue.requireInteger "revision"

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
                            |> Option.map (fun token ->
                                ("continuationToken", RpcValue.String token) :: fields)
                            |> Option.defaultValue fields
                        |> PipeTest.map

                    PipeTest.send
                        child
                        false
                        (PipeTest.request requestId "workspace/children" freshChildren)

                    let projectError, projectPage =
                        PipeTest.readFrame child |> PipeTest.response requestId

                    Assert.True projectError.IsNone

                    Assert.Equal(
                        watchedRevision,
                        PipeTest.field "revision" projectPage |> RpcValue.requireInteger "revision"
                    )

                    watchedValueFound <-
                        PipeTest.field "nodes" projectPage
                        |> RpcValue.requireArray "nodes"
                        |> Seq.exists (fun node ->
                            PipeTest.field "kind" node = RpcValue.String "projectItem"
                            && PipeTest.field "name" node = RpcValue.String
                                "Evaluated WatchedValue = changed")

                    continuation <-
                        match PipeTest.field "nextToken" projectPage with
                        | RpcValue.String token -> Some token
                        | RpcValue.Nil -> None
                        | value -> failwithf "Unexpected continuation token: %A" value

                    hasMore <- continuation.IsSome
                    requestId <- requestId + 1u

                Assert.True(
                    watchedValueFound,
                    "Fresh project paging did not expose Evaluated WatchedValue = changed."
                )

                File.Copy(PipeTest.globalJson, Path.Combine(directory, "global.json"))
                let selection = Task.Run(fun () -> PipeTest.readFrame child)

                Assert.True(
                    selection.Wait(TimeSpan.FromSeconds 10.0),
                    "global.json creation was not observed."
                )

                match selection.Result with
                | Notification("workspace/reset", parameters) ->
                    let resetRevision =
                        PipeTest.field "revision" parameters |> RpcValue.requireInteger "revision"

                    Assert.True(resetRevision > watchedRevision)

                    PipeTest.send
                        child
                        false
                        (PipeTest.request 100u "workspace/root" RpcValue.emptyMap)

                    let freshError, freshRoot = PipeTest.readFrame child |> PipeTest.response 100u
                    Assert.True freshError.IsNone

                    Assert.Equal(
                        resetRevision,
                        PipeTest.field "revision" freshRoot |> RpcValue.requireInteger "revision"
                    )

                    let workspaceTarget = PipeTest.map [ "targetId", RpcValue.String workspaceId ]
                    PipeTest.send child false (PipeTest.request 101u "command/list" workspaceTarget)
                    let commandError, commands = PipeTest.readFrame child |> PipeTest.response 101u
                    Assert.True commandError.IsNone

                    PipeTest.field "commands" commands
                    |> RpcValue.requireArray "commands"
                    |> Seq.exists (fun command ->
                        PipeTest.field "id" command = RpcValue.String "solution.project.add")
                    |> Assert.True
                | frame -> failwithf "Expected a toolset reset, got %A" frame

                PipeTest.shutdown child 102u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should reset the built apphost when a child hydration delta exceeds its frame limit``
        ()
        =
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
                    [ "protocolVersion",
                      PipeTest.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
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
                Assert.True probeRootError.IsNone

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

                    Assert.True probeChildrenError.IsNone

                    match PipeTest.readFrame probe with
                    | Notification("workspace/delta", _) as delta when index = 1 ->
                        let deltaSize = (RpcCodec.encodeFrame delta).Length

                        Assert.True(
                            deltaSize > 1024,
                            $"Expected a delta above 1024 bytes, got {deltaSize}."
                        )
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

                Assert.Equal(
                    0L,
                    PipeTest.field "revision" root |> RpcValue.requireInteger "revision"
                )

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
                Assert.True firstError.IsNone

                Assert.Equal(
                    1L,
                    PipeTest.field "revision" firstPage |> RpcValue.requireInteger "revision"
                )

                let firstDelta, firstDeltaSize = PipeTest.readFrameWithSize child
                Assert.True(firstDeltaSize <= 1024)

                match firstDelta with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        0L,
                        PipeTest.field "baseRevision" parameters
                        |> RpcValue.requireInteger "baseRevision"
                    )

                    Assert.Equal(
                        1L,
                        PipeTest.field "newRevision" parameters
                        |> RpcValue.requireInteger "newRevision"
                    )
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
                Assert.True childrenError.IsNone

                Assert.Equal(
                    2L,
                    PipeTest.field "revision" page |> RpcValue.requireInteger "revision"
                )

                let resetFrame, resetSize = PipeTest.readFrameWithSize child
                Assert.True(resetSize <= 1024)

                match resetFrame with
                | Notification("workspace/reset", parameters) ->
                    Assert.Equal(
                        3L,
                        PipeTest.field "revision" parameters |> RpcValue.requireInteger "revision"
                    )

                    let diagnostic =
                        PipeTest.field "diagnostics" parameters
                        |> RpcValue.requireArray "diagnostics"
                        |> Seq.exactlyOne

                    Assert.Equal(
                        RpcValue.String "workspace.delta_pressure",
                        PipeTest.field "code" diagnostic
                    )
                | frame -> failwithf "Expected bounded child-hydration reset, got %A" frame

                PipeTest.send child false (PipeTest.request 14u "workspace/root" RpcValue.emptyMap)
                let freshFrame, freshSize = PipeTest.readFrameWithSize child
                Assert.True(freshSize <= 1024)
                let freshError, freshRoot = PipeTest.response 14u freshFrame
                Assert.True freshError.IsNone

                Assert.Equal(
                    3L,
                    PipeTest.field "revision" freshRoot |> RpcValue.requireInteger "revision"
                )

                PipeTest.shutdown child 15u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should apply negotiated frame limits to all outbound frames``() =
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
                Assert.True exportError.IsNone

                let operationId =
                    PipeTest.field "operationId" exportResult
                    |> RpcValue.requireString "operationId"

                let mutable completed = false

                while not completed do
                    let frame, size = PipeTest.readFrameWithSize child
                    Assert.True(size <= 1024)

                    match frame with
                    | Notification("operation/completed", parameters) ->
                        Assert.Equal(
                            RpcValue.String operationId,
                            PipeTest.field "operationId" parameters
                        )

                        Assert.Equal(
                            RpcValue.String "succeeded",
                            PipeTest.field "outcome" parameters
                        )

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
        Assert.True(startup.WaitForExit 5000)
        Assert.Equal(64, startup.ExitCode)
        Assert.Empty(PipeTest.readRemaining startup.StandardOutput.BaseStream)
        Assert.Contains("startup failure", startup.StandardError.ReadToEnd())

        let directory = PipeTest.temporaryDirectory "pipe-fatal"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            PipeTest.save solution (SolutionModel())
            use fatal = PipeTest.startPipe "solution" solution
            fatal.StandardInput.BaseStream.Write [| 0xd4uy; 0uy; 0uy |]
            fatal.StandardInput.Close()
            Assert.True(fatal.WaitForExit 5000)
            Assert.Equal(65, fatal.ExitCode)
            Assert.Empty(PipeTest.readRemaining fatal.StandardOutput.BaseStream)
            Assert.Contains("protocol failure", fatal.StandardError.ReadToEnd())

            use orderlyEof = PipeTest.startPipe "solution" solution
            PipeTest.send orderlyEof false (PipeTest.request 1u "initialize" PipeTest.initialize)
            PipeTest.readFrame orderlyEof |> PipeTest.response 1u |> ignore
            PipeTest.send orderlyEof false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
            PipeTest.readFrame orderlyEof |> PipeTest.response 2u |> ignore
            orderlyEof.StandardInput.Close()

            Assert.True(
                orderlyEof.WaitForExit 5000,
                "The watched pipe did not exit after stdin closed."
            )

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

            PipeTest.send
                invalidInitialize
                false
                (PipeTest.request 1u "initialize" RpcValue.emptyMap)

            let initializeError, _ =
                PipeTest.readFrame invalidInitialize |> PipeTest.response 1u

            Assert.Equal("invalid_params", initializeError.Value.Code)
            invalidInitialize.StandardInput.Close()
            Assert.True(invalidInitialize.WaitForExit 5000)
            Assert.Equal(0, invalidInitialize.ExitCode)
            Assert.Equal(String.Empty, invalidInitialize.StandardError.ReadToEnd())
        finally
            if Directory.Exists invalidDirectory then
                Directory.Delete(invalidDirectory, true)

        let start = ProcessStartInfo PipeTest.apphost
        start.ArgumentList.Add "--json"
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        use direct = Process.Start start
        Assert.NotNull direct
        Assert.True(direct.WaitForExit 5000)
        Assert.NotEqual(0, direct.ExitCode)
        Assert.StartsWith("{", direct.StandardOutput.ReadToEnd().TrimStart())

    [<Fact>]
    member _.``should bind incoming conditional references into a confirmed project rename``() =
        let directory = PipeTest.temporaryDirectory "pipe-command"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let source = Path.Combine(directory, "One.fsproj")
            let destination = Path.Combine(directory, "Renamed.fsproj")
            let incoming = Path.Combine(directory, "Ref.fsproj")
            let model = SolutionModel()
            model.AddProject("One.fsproj", null, null) |> ignore
            model.AddProject("Ref.fsproj", null, null) |> ignore
            PipeTest.writeProject source

            File.WriteAllText(
                incoming,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
                + "<ProjectReference Include=\"One.fsproj\" Condition=\"'$(Configuration)' == 'Never'\" />"
                + "</ItemGroup><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
                + "</PropertyGroup></Project>"
            )

            PipeTest.save solution model
            use child = PipeTest.startPipe "solution" solution

            try
                let initialize =
                    PipeTest.map
                        [ "protocolVersion",
                          PipeTest.map
                              [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
                          "clientInfo", PipeTest.map [ "name", RpcValue.String "command-test" ]
                          "capabilities",
                          RpcValue.array
                              [ RpcValue.String "workspace.root"
                                RpcValue.String "workspace.children"
                                RpcValue.String "workspace.delta"
                                RpcValue.String "command.list"
                                RpcValue.String "command.preview"
                                RpcValue.String "command.execute" ]
                          "limits",
                          PipeTest.map
                              [ "maxFrameBytes", RpcValue.Integer 4194304L
                                "maxPageSize", RpcValue.Integer 50L ] ]

                PipeTest.send child false (PipeTest.request 1u "initialize" initialize)

                let initializeError, initializeResult =
                    PipeTest.readFrame child |> PipeTest.response 1u

                Assert.True initializeError.IsNone

                let workspaceId =
                    PipeTest.field "workspace" initializeResult
                    |> PipeTest.field "id"
                    |> RpcValue.requireString "id"

                let workspaceTarget = PipeTest.map [ "targetId", RpcValue.String workspaceId ]
                PipeTest.send child false (PipeTest.request 30u "command/list" workspaceTarget)

                let workspaceListError, workspaceList =
                    PipeTest.readFrame child |> PipeTest.response 30u

                Assert.True workspaceListError.IsNone

                PipeTest.field "commands" workspaceList
                |> RpcValue.requireArray "commands"
                |> Seq.exists (fun command ->
                    PipeTest.field "id" command = RpcValue.String "solution.project.add")
                |> Assert.True

                PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let rootError, rootResult = PipeTest.readFrame child |> PipeTest.response 2u
                Assert.True rootError.IsNone

                let projectId =
                    PipeTest.field "nodes" rootResult
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        PipeTest.field "kind" node = RpcValue.String "project"
                        && PipeTest.field "name" node = RpcValue.String "One")
                    |> PipeTest.field "id"
                    |> RpcValue.requireString "id"

                let children =
                    PipeTest.map
                        [ "parentId", RpcValue.String projectId; "pageSize", RpcValue.Integer 50L ]

                PipeTest.send child false (PipeTest.request 3u "workspace/children" children)
                let hydrationError, _ = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.True hydrationError.IsNone

                match PipeTest.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        0L,
                        PipeTest.field "baseRevision" parameters
                        |> RpcValue.requireInteger "revision"
                    )

                    Assert.Equal(
                        1L,
                        PipeTest.field "newRevision" parameters
                        |> RpcValue.requireInteger "revision"
                    )
                | frame -> failwithf "Expected the hydration delta, got %A" frame

                let target = PipeTest.map [ "targetId", RpcValue.String projectId ]
                PipeTest.send child false (PipeTest.request 4u "command/list" target)
                let listError, listResult = PipeTest.readFrame child |> PipeTest.response 4u
                Assert.True listError.IsNone

                PipeTest.field "commands" listResult
                |> RpcValue.requireArray "commands"
                |> Seq.exists (fun command ->
                    PipeTest.field "id" command = RpcValue.String "solution.project.rename")
                |> Assert.True

                let arguments = PipeTest.map [ "name", RpcValue.String "Renamed" ]

                let invalidRevision =
                    PipeTest.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer -1L ]

                PipeTest.send child false (PipeTest.request 20u "command/preview" invalidRevision)
                let revisionError, _ = PipeTest.readFrame child |> PipeTest.response 20u
                Assert.Equal("invalid_params", revisionError.Value.Code)

                let malformedPreview =
                    PipeTest.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 1L
                          "previewId", RpcValue.String "bad" ]

                PipeTest.send child false (PipeTest.request 21u "command/execute" malformedPreview)
                let previewIdError, _ = PipeTest.readFrame child |> PipeTest.response 21u
                Assert.Equal("invalid_params", previewIdError.Value.Code)

                let preview =
                    PipeTest.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 1L ]

                PipeTest.send child false (PipeTest.request 5u "command/preview" preview)
                let previewError, previewResult = PipeTest.readFrame child |> PipeTest.response 5u
                Assert.True previewError.IsNone

                let previewId =
                    PipeTest.field "previewId" previewResult |> RpcValue.requireString "previewId"

                Assert.True(File.Exists source)

                File.ReadAllText incoming
                |> fun contents -> contents.Contains "One.fsproj"
                |> should equal true

                let execute =
                    PipeTest.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 1L
                          "previewId", RpcValue.String previewId ]

                PipeTest.send child false (PipeTest.request 6u "command/execute" execute)
                let executeError, executeResult = PipeTest.readFrame child |> PipeTest.response 6u
                Assert.True executeError.IsNone

                Assert.Equal(
                    2L,
                    PipeTest.field "revision" executeResult |> RpcValue.requireInteger "revision"
                )

                match PipeTest.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        1L,
                        PipeTest.field "baseRevision" parameters
                        |> RpcValue.requireInteger "baseRevision"
                    )

                    Assert.Equal(
                        2L,
                        PipeTest.field "newRevision" parameters
                        |> RpcValue.requireInteger "newRevision"
                    )
                | frame ->
                    failwithf
                        "Expected the transaction delta after the execute response, got %A"
                        frame

                Assert.False(File.Exists source)
                Assert.True(File.Exists destination)

                File.ReadAllText incoming
                |> fun contents -> contents.Contains "Renamed.fsproj"
                |> should equal true

                File.ReadAllText incoming
                |> fun contents -> contents.Contains "Condition=\"'$(Configuration)' == 'Never'\""
                |> should equal true

                let reopened =
                    SolutionSerializers
                        .GetSerializerByMoniker(solution)
                        .OpenAsync(solution, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()

                reopened.SolutionProjects
                |> Seq.exists (fun project -> project.FilePath = "Renamed.fsproj")
                |> Assert.True

                reopened.SolutionProjects
                |> Seq.exists (fun project -> project.FilePath = "One.fsproj")
                |> Assert.False

                PipeTest.send child false (PipeTest.request 7u "command/execute" execute)
                let duplicateError, _ = PipeTest.readFrame child |> PipeTest.response 7u
                Assert.Equal("not_found", duplicateError.Value.Code)
                PipeTest.shutdown child 8u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should expose only read commands and refuse mutation requests for a solution filter``
        ()
        =
        let directory = PipeTest.temporaryDirectory "pipe-command-slnf"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let filter = Path.Combine(directory, "Demo.slnf")
            PipeTest.save solution (SolutionModel())
            File.WriteAllText(filter, """{ "solution": { "path": "Demo.slnx" } }""")
            let before = File.ReadAllBytes solution
            use child = PipeTest.startPipe "solution" filter

            try
                let initialize =
                    PipeTest.map
                        [ "protocolVersion",
                          PipeTest.map
                              [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
                          "clientInfo", PipeTest.map [ "name", RpcValue.String "test" ]
                          "capabilities",
                          RpcValue.array
                              [ RpcValue.String "workspace.root"
                                RpcValue.String "workspace.export"
                                RpcValue.String "workspace.refresh"
                                RpcValue.String "operation.cancel"
                                RpcValue.String "unknown.claim" ]
                          "limits",
                          PipeTest.map
                              [ "maxFrameBytes", RpcValue.Integer 4096L
                                "maxPageSize", RpcValue.Integer 50L ] ]

                PipeTest.send child false (PipeTest.request 1u "initialize" initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore
                PipeTest.send child false (PipeTest.request 2u "command/list" RpcValue.emptyMap)
                let listError, listResult = PipeTest.readFrame child |> PipeTest.response 2u
                Assert.True listError.IsNone

                PipeTest.field "commands" listResult
                |> RpcValue.requireArray "commands"
                |> Seq.map (PipeTest.field "id" >> RpcValue.requireString "id")
                |> Seq.toArray
                |> should
                    equal
                    [| "solution.launch.list"
                       "lifecycle.restore"
                       "lifecycle.build"
                       "lifecycle.test"
                       "template.list"
                       "template.show" |]

                let describe = PipeTest.map [ "commandId", RpcValue.String "solution.folder.add" ]

                PipeTest.send child false (PipeTest.request 3u "command/describe" describe)
                let describeError, _ = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.Equal("not_found", describeError.Value.Code)

                let arguments = PipeTest.map [ "name", RpcValue.String "src" ]

                let preview =
                    PipeTest.map
                        [ "commandId", RpcValue.String "solution.folder.add"
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 0L ]

                PipeTest.send child false (PipeTest.request 4u "command/preview" preview)
                let previewError, _ = PipeTest.readFrame child |> PipeTest.response 4u
                Assert.Equal("unsupported_capability", previewError.Value.Code)

                let execute =
                    PipeTest.map
                        [ "commandId", RpcValue.String "solution.folder.add"
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 0L
                          "previewId", RpcValue.String(String('A', 64)) ]

                PipeTest.send child false (PipeTest.request 5u "command/execute" execute)
                let executeError, _ = PipeTest.readFrame child |> PipeTest.response 5u
                Assert.Equal("unsupported_capability", executeError.Value.Code)
                Assert.Equal<byte>(before, File.ReadAllBytes solution)
                PipeTest.shutdown child 6u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
