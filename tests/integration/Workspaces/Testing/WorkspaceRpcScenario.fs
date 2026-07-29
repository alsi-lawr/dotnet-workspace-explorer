namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Xml.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.CommandLine
open FsUnit.Xunit
open Xunit

module internal WorkspaceRpcScenario =
    let request id name parameters =
        MessagePackRpcCodec.encodeFrame (Request(id, name, parameters))

    let map values = RpcValue.map values

    let initialize =
        map
            [ "protocolVersion", map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
              "clientInfo", map [ "name", RpcValue.String "test" ]
              "capabilities",
              RpcValue.array
                  [ RpcValue.String "workspace.root"
                    RpcValue.String "workspace.export.start"
                    RpcValue.String "workspace.refresh"
                    RpcValue.String "workspace.operations.cancel"
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
            Path.Combine(
                AppContext.BaseDirectory,
                $".dotnet-workspace-explorer-{name}-{Guid.NewGuid():N}"
            )

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

    let executable =
        let root = repositoryRoot AppContext.BaseDirectory

        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.WorkspaceExplorer.exe"
            else
                "Dotnet.WorkspaceExplorer"

        Path.Combine(root, "src", "WorkspaceExplorer", "bin", buildConfiguration, "net10.0", name)

    let globalJson =
        Path.Combine(repositoryRoot AppContext.BaseDirectory, "global.json")

    let fixturePath name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

    let startApphost arguments environment =
        let start = ProcessStartInfo executable

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
            failwith "Failed to start the built executable."

        child

    let startPipeWithEnvironment _subject solution environment =
        startApphost [ "workspace"; solution; "--pipe" ] environment

    let startPipeWithDataHome alias solution dataHome =
        let environment =
            dataHome
            |> Option.map (fun path -> [ "XDG_DATA_HOME", path ])
            |> Option.defaultValue []

        startPipeWithEnvironment alias solution environment

    let startWorkspaceRpc alias solution =
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
                failwith "The executable stdout ended before a complete frame was received."

            pending.Add(byte next)

            match
                MessagePackRpcCodec.tryReadValueLength
                    MessagePackRpcCodec.secureLimits
                    (pending.ToArray())
            with
            | Error RpcFrameDecodeError.Incomplete -> ()
            | Error error -> failwithf "Invalid executable stdout: %A" error
            | Ok length when length = pending.Count ->
                match
                    MessagePackRpcCodec.decodeFrame
                        MessagePackRpcCodec.secureLimits
                        (pending.ToArray())
                with
                | Ok(RpcFrameDecodeResult.Frame value) -> frame <- Some(value, length)
                | Ok(RpcFrameDecodeResult.RecoverableError _) ->
                    failwith "Server stdout contained a request error."
                | Error error -> failwithf "Invalid executable frame: %A" error
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
        Assert.True(child.WaitForExit 5000, "The executable did not exit after shutdown.")
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
            | Notification("workspace/export/chunk", parameters) ->
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
            | Notification("workspace/operations/completed", parameters) ->
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
        send child false (request requestId "workspace/export/start" RpcValue.emptyMap)
        let error, result = readFrame child |> response requestId
        Assert.True error.IsNone

        field "operationId" result |> RpcValue.requireString "operationId",
        field "revision" result |> RpcValue.requireInteger "revision"

    let disposeProcess (child: Process) =
        if not child.HasExited then
            child.Kill true
            child.WaitForExit()

        child.Dispose()

    let previewAndExecute child id commandId targetNodeId arguments revision expectsDelta =
        let preview =
            map
                [ "commandId", RpcValue.String commandId
                  "targetNodeId", RpcValue.String targetNodeId
                  "arguments", arguments
                  "expectedRevision", RpcValue.Integer revision ]

        send child false (request id "workspace/commands/preview" preview)
        let previewError, previewResult = readFrame child |> response id

        match previewError with
        | Some error -> failwithf "%s preview failed: %s: %s" commandId error.Code error.Message
        | None -> ()

        let execute =
            map
                [ "commandId", RpcValue.String commandId
                  "targetNodeId", RpcValue.String targetNodeId
                  "arguments", arguments
                  "expectedRevision", RpcValue.Integer revision
                  "confirmationToken", field "confirmationToken" previewResult ]

        send child false (request (id + 1u) "workspace/commands/execute" execute)
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
        let child = startWorkspaceRpc "solution" solution

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
                        RpcValue.String "workspace.export.start"
                        RpcValue.String "workspace.refresh"
                        RpcValue.String "workspace.operations.cancel" ]
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
                "workspace/commands/preview"
                (map
                    [ "commandId", RpcValue.String commandId
                      "targetNodeId", RpcValue.String session.ProjectId
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
                [ "parentNodeId", RpcValue.String session.ProjectId
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
