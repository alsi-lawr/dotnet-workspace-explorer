namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

module internal Test =
    let private pendingFrames = Dictionary<int, ResizeArray<byte>>()

    let temporaryDirectory name =
        let path =
            Path.Combine(
                Path.GetTempPath(),
                $"dotnet-workspace-explorer-msbuild-{name}-{Guid.NewGuid():N}"
            )

        Directory.CreateDirectory path |> ignore
        File.WriteAllText(Path.Combine(path, "Directory.Build.props"), "<Project />")
        path

    let fixturePath name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProjectEvaluation", name)

    let copyFixture directory name =
        let source = fixturePath name
        let destination = Path.Combine(directory, name)

        for path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
            let target = Path.Combine(destination, Path.GetRelativePath(source, path))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(path, target)

        destination

    let rec private tryRepositoryRoot (directory: string) =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            Some directory
        else
            match Directory.GetParent directory with
            | null -> None
            | parent -> tryRepositoryRoot parent.FullName

    let repositoryRoot directory =
        [ directory; Directory.GetCurrentDirectory() ]
        |> Seq.choose tryRepositoryRoot
        |> Seq.tryHead
        |> Option.defaultWith (fun () -> failwith "Could not locate the repository root.")

    let configuration =
        let baseDirectory = DirectoryInfo AppContext.BaseDirectory

        match baseDirectory.Parent with
        | null -> failwith "Could not determine the build configuration."
        | parent when parent.Name = "Debug" || parent.Name = "Release" -> parent.Name
        | _ -> "Debug"

    let executable =
        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.WorkspaceExplorer.exe"
            else
                "Dotnet.WorkspaceExplorer"

        Path.Combine(
            repositoryRoot AppContext.BaseDirectory,
            "src",
            "WorkspaceExplorer",
            "bin",
            configuration,
            "net10.0",
            name
        )

    let write (path: string) (contents: string) = File.WriteAllText(path, contents)

    let simpleProject directory name extension =
        let project = Path.Combine(directory, name + extension)

        write
            project
            """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
</Project>
"""

        project

    let writeGlobalJson directory version =
        write
            (Path.Combine(directory, "global.json"))
            (sprintf
                """{"sdk":{"version":"%s","rollForward":"disable","allowPrerelease":false}}"""
                version)

    let writeSolution directory (projects: seq<string>) =
        let solution = Path.Combine(directory, "Demo.slnx")

        projects
        |> Seq.map (fun project -> $"  <Project Path=\"{Path.GetFileName project}\" />")
        |> String.concat Environment.NewLine
        |> fun entries ->
            $"<Solution>{Environment.NewLine}{entries}{Environment.NewLine}</Solution>"
        |> write solution

        solution

    let runDotnet workingDirectory argument =
        let start = ProcessStartInfo "dotnet"
        start.WorkingDirectory <- workingDirectory
        start.ArgumentList.Add argument
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false

        use child = Process.Start start
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()

        if child.ExitCode <> 0 then
            failwithf "dotnet %s failed with exit code %d: %s" argument child.ExitCode error

        output

    let currentSdkVersion workingDirectory =
        runDotnet workingDirectory "--version" |> _.Trim()

    let currentToolsetPath workingDirectory =
        runDotnet workingDirectory "--info"
        |> _.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.tryPick (fun line ->
            let prefix = "Base Path:"

            if line.StartsWith(prefix, StringComparison.Ordinal) then
                Some(line.Substring(prefix.Length).Trim() |> Path.TrimEndingDirectorySeparator)
            else
                None)
        |> Option.defaultWith (fun () ->
            failwith "dotnet --info did not report the selected SDK base path.")

    let start arguments =
        let start = ProcessStartInfo executable

        for argument in arguments do
            start.ArgumentList.Add argument

        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false
        start.CreateNoWindow <- true

        match Process.Start start with
        | null -> failwith "Could not start the built executable."
        | child -> child

    let startWorker toolsetPath =
        start [ "internal"; "project-evaluation-host"; "--sdk"; toolsetPath ]

    let startWorkspaceRpc solution =
        start [ "workspace"; solution; "--pipe" ]

    let disposeProcess (child: Process) =
        if not child.HasExited then
            child.Kill true
            child.WaitForExit()

        pendingFrames.Remove child.Id |> ignore
        child.Dispose()

    let request id name parameters =
        MessagePackRpcCodec.encodeFrame (Request(id, name, parameters))

    let send (child: Process) id name parameters =
        let bytes = request id name parameters
        child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
        child.StandardInput.BaseStream.Flush()

    let readFrame (child: Process) =
        let bytes =
            match pendingFrames.TryGetValue child.Id with
            | true, pending -> pending
            | false, _ ->
                let pending = ResizeArray<byte>()
                pendingFrames.Add(child.Id, pending)
                pending

        let mutable frame = None

        while frame.IsNone do
            match
                MessagePackRpcCodec.tryReadValueLength
                    MessagePackRpcCodec.secureLimits
                    (bytes.ToArray())
            with
            | Error RpcFrameDecodeError.Incomplete ->
                let buffer = Array.zeroCreate<byte> 8192
                let count = child.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)

                if count = 0 then
                    failwith "Apphost stdout ended before a complete frame."

                for index in 0 .. count - 1 do
                    bytes.Add buffer[index]
            | Error error -> failwithf "Invalid executable frame: %A" error
            | Ok length ->
                let encoded = bytes.GetRange(0, length).ToArray()
                bytes.RemoveRange(0, length)

                match MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits encoded with
                | Ok(RpcFrameDecodeResult.Frame decoded) -> frame <- Some decoded
                | decoded -> failwithf "Invalid executable frame: %A" decoded

        frame.Value

    let response expectedId =
        function
        | Response(id, error, result) when id = expectedId -> error, result
        | frame -> failwithf "Expected response %d, got %A" expectedId frame

    let field name value =
        value |> RpcValue.requireMap "value" |> RpcValue.requireField name

    let stringField name value =
        field name value |> RpcValue.requireString name

    let values name value =
        field name value |> RpcValue.requireArray name

    let strings name value =
        values name value |> Seq.map (RpcValue.requireString name)

    let requireSuccess id child =
        let error, result = readFrame child |> response id

        match error with
        | None -> result
        | Some failure -> failwithf "%s: %s" failure.Code failure.Message

    let requireSuccessAfterWorkspaceNotifications id expectedRevision child =
        let mutable revision = expectedRevision
        let mutable result = None

        while result.IsNone do
            match readFrame child with
            | Notification("workspace/delta", parameters) ->
                let baseRevision =
                    field "baseRevision" parameters |> RpcValue.requireInteger "baseRevision"

                let nextRevision =
                    field "newRevision" parameters |> RpcValue.requireInteger "newRevision"

                (baseRevision) |> should equal (revision)
                (nextRevision > baseRevision) |> should equal true
                revision <- nextRevision
            | Notification("workspace/reset", parameters) ->
                let nextRevision = field "revision" parameters |> RpcValue.requireInteger "revision"
                (nextRevision > revision) |> should equal true
                revision <- nextRevision
            | Response(actual, error, value) when actual = id ->
                match error with
                | None -> result <- Some value
                | Some failure -> failwithf "%s: %s" failure.Code failure.Message
            | frame -> failwithf "Expected workspace notification or response %d, got %A" id frame

        result.Value, revision

    let requireSuccessAndWorkspaceReset id expectedRevision child =
        let mutable revision = expectedRevision
        let mutable result = None
        let mutable reset = None

        while result.IsNone || reset.IsNone do
            match readFrame child with
            | Notification("workspace/delta", parameters) ->
                let baseRevision =
                    field "baseRevision" parameters |> RpcValue.requireInteger "baseRevision"

                let nextRevision =
                    field "newRevision" parameters |> RpcValue.requireInteger "newRevision"

                (baseRevision) |> should equal (revision)
                (nextRevision > baseRevision) |> should equal true
                revision <- nextRevision
            | Notification("workspace/reset", parameters) ->
                let nextRevision = field "revision" parameters |> RpcValue.requireInteger "revision"
                (nextRevision > revision) |> should equal true
                revision <- nextRevision
                reset <- Some parameters
            | Response(actual, error, value) when actual = id ->
                match error with
                | None -> result <- Some value
                | Some failure -> failwithf "%s: %s" failure.Code failure.Message
            | frame -> failwithf "Expected workspace reset or response %d, got %A" id frame

        result.Value, reset.Value, revision

    let workerInitializeVersion major minor frameLimit =
        RpcValue.map
            [ "profile", RpcValue.String "dotnet-workspace-explorer/project-evaluation"
              "protocolVersion",
              RpcValue.map [ "major", RpcValue.Integer major; "minor", RpcValue.Integer minor ]
              "limits", RpcValue.map [ "maxFrameBytes", RpcValue.Integer(int64 frameLimit) ] ]

    let workerInitialize frameLimit =
        workerInitializeVersion 2L 0L frameLimit

    let initializeWorker child id =
        send
            child
            id
            "initialize"
            (workerInitialize MessagePackRpcCodec.secureLimits.MaximumValueBytes)

        requireSuccess id child |> ignore

    let pipeInitialize =
        RpcValue.map
            [ "protocolVersion",
              RpcValue.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
              "clientInfo", RpcValue.map [ "name", RpcValue.String "msbuild-contract-tests" ]
              "capabilities",
              RpcValue.array
                  [ RpcValue.String "workspace.root"
                    RpcValue.String "workspace.children"
                    RpcValue.String "workspace.delta"
                    RpcValue.String "workspace.refresh"
                    RpcValue.String "workspace.export.start"
                    RpcValue.String "workspace.operations.cancel" ]
              "limits",
              RpcValue.map
                  [ "maxFrameBytes", RpcValue.Integer 1048576L
                    "maxPageSize", RpcValue.Integer 100L ] ]

    let initializeWorkspaceRpc child id =
        send child id "initialize" pipeInitialize
        requireSuccess id child |> ignore

    let evaluate child id project =
        send
            child
            id
            "project-evaluation/evaluate"
            (RpcValue.map [ "projectPath", RpcValue.String project ])

        readFrame child |> response id

    let invalidate child id paths =
        let parameters =
            RpcValue.map [ "paths", paths |> Seq.map RpcValue.String |> RpcValue.array ]

        send child id "project-evaluation/invalidate" parameters
        requireSuccess id child

    let shutdown child id =
        send child id "shutdown" RpcValue.emptyMap
        let result = requireSuccess id child

        (RpcValue.tryField "accepted" result)
        |> should equal (Some(RpcValue.Boolean true))

        child.StandardInput.Close()
        (child.WaitForExit 5000) |> should equal true
        (child.StandardOutput.BaseStream.ReadByte()) |> should equal (-1)
        child.ExitCode |> should equal 0
        (child.StandardError.ReadToEnd()) |> should equal (String.Empty)

    let withWorker directory action =
        let worker = startWorker (currentToolsetPath directory)

        try
            initializeWorker worker 1u
            action worker |> shutdown worker
        finally
            disposeProcess worker

    let withWorkspaceRpc solution action =
        let app = startWorkspaceRpc solution

        try
            initializeWorkspaceRpc app 1u
            action app |> shutdown app
        finally
            disposeProcess app

    let hydrateProject child firstId =
        send child firstId "workspace/root" RpcValue.emptyMap
        let root = requireSuccess firstId child

        let rootRevision = field "revision" root |> RpcValue.requireInteger "revision"

        let workspaceRootId =
            values "nodes" root |> Seq.map (stringField "id") |> Seq.exactlyOne

        send
            child
            (firstId + 1u)
            "workspace/children"
            (RpcValue.map
                [ "parentNodeId", RpcValue.String workspaceRootId
                  "pageSize", RpcValue.Integer 100L ])

        let rootChildren = requireSuccess (firstId + 1u) child

        let projectId =
            values "nodes" rootChildren
            |> Seq.filter (fun node -> stringField "kind" node = "project")
            |> Seq.filter (fun node -> stringField "name" node = "Selected")
            |> Seq.map (stringField "id")
            |> Seq.exactlyOne

        let mutable continuation = None
        let mutable requestId = firstId + 2u
        let mutable hasMore = true
        let mutable projectFileFound = false
        let mutable hydratedRevision = None

        while hasMore && not projectFileFound do
            let parameters =
                [ "parentNodeId", RpcValue.String projectId; "pageSize", RpcValue.Integer 100L ]
                |> fun fields ->
                    continuation
                    |> Option.map (fun token ->
                        ("continuationToken", RpcValue.String token) :: fields)
                    |> Option.defaultValue fields
                |> RpcValue.map

            send child requestId "workspace/children" parameters
            let page = requireSuccess requestId child

            if hydratedRevision.IsNone then
                match readFrame child with
                | Notification("workspace/delta", parameters) ->
                    (field "baseRevision" parameters |> RpcValue.requireInteger "baseRevision")
                    |> should equal (rootRevision)

                    let revision =
                        field "newRevision" parameters |> RpcValue.requireInteger "newRevision"

                    (revision > rootRevision) |> should equal true
                    hydratedRevision <- Some revision
                | frame -> failwithf "Expected hydration delta, got %A" frame

            projectFileFound <-
                values "nodes" page
                |> Seq.exists (fun node -> stringField "kind" node = "projectFile")

            continuation <-
                match RpcValue.tryField "nextToken" page with
                | Some(RpcValue.String token) -> Some token
                | Some RpcValue.Nil
                | None -> None
                | Some value -> failwithf "Unexpected continuation token: %A" value

            hasMore <- continuation.IsSome
            requestId <- requestId + 1u

        (projectFileFound) |> should equal true

        hydratedRevision
        |> Option.defaultWith (fun () -> failwith "The hydration delta was not observed.")
