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

    let startPipeWithDataHome alias solution dataHome =
        let start = ProcessStartInfo(apphost)
        start.ArgumentList.Add alias
        start.ArgumentList.Add solution
        start.ArgumentList.Add "--pipe"
        start.UseShellExecute <- false
        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.CreateNoWindow <- true

        dataHome |> Option.iter (fun path -> start.Environment["XDG_DATA_HOME"] <- path)

        let child = Process.Start start

        if isNull child then
            failwith "Failed to start the built apphost."

        child

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

type WorkspaceAppHostTests() =
    [<Fact>]
    member _.``should mutate project items through the previewed public commands``() =
        let directory = PipeTest.temporaryDirectory "project-item-glob-command"
        let external = PipeTest.temporaryDirectory "project-item-external-command"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.csproj")
            let source = Path.Combine(directory, "Extra.cs")
            let content = Path.Combine(directory, "Extra.txt")
            let created = Path.Combine(directory, "New.cs")
            let copySource = Path.Combine(directory, "CopySource.txt")
            let copiedItem = Path.Combine(directory, "Copy.txt")
            let renamedItem = Path.Combine(directory, "Renamed.txt")
            let movedDirectory = Path.Combine(directory, "Moved")
            let movedItem = Path.Combine(movedDirectory, "Renamed.txt")
            let deleted = Path.Combine(directory, "Delete.txt")
            let generatedDirectory = Path.Combine(directory, "obj")
            let generated = Path.Combine(generatedDirectory, "Generated.cs")
            let trashHome = Path.Combine(directory, "data")
            let externalCopy = Path.Combine(external, "External.txt")
            let externalLink = Path.Combine(external, "Linked.txt")
            let externalDirectory = Path.Combine(external, "Directory")
            let model = SolutionModel()
            model.AddProject("Demo.csproj", "Demo", null) |> ignore

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            )

            File.WriteAllText(source, "class Extra { }")
            File.WriteAllText(content, "content")
            File.WriteAllText(copySource, "copy source")
            File.WriteAllText(deleted, "delete")
            Directory.CreateDirectory(movedDirectory) |> ignore
            Directory.CreateDirectory(generatedDirectory) |> ignore
            Directory.CreateDirectory(trashHome) |> ignore
            File.WriteAllText(generated, "class Generated { }")
            File.WriteAllText(externalCopy, "copy")
            File.WriteAllText(externalLink, "link")
            Directory.CreateDirectory(externalDirectory) |> ignore
            File.WriteAllText(Path.Combine(externalDirectory, "Nested.txt"), "nested")
            PipeTest.save solution model
            let before = File.ReadAllBytes project

            use child =
                if OperatingSystem.IsLinux() then
                    PipeTest.startPipeWithDataHome "solution" solution (Some trashHome)
                else
                    PipeTest.startPipe "solution" solution

            try
                PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore
                PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let rootError, root = PipeTest.readFrame child |> PipeTest.response 2u

                match rootError with
                | Some error -> failwithf "Workspace root failed: %s: %s" error.Code error.Message
                | None -> ()

                let projectId =
                    PipeTest.field "nodes" root
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node -> PipeTest.field "kind" node = RpcValue.String "project")
                    |> PipeTest.field "id"
                    |> RpcValue.requireString "id"

                PipeTest.previewAndExecute
                    child
                    3u
                    "project.item.add"
                    projectId
                    (PipeTest.map [ "path", RpcValue.String source; "itemType", RpcValue.String "Compile" ])
                    0L
                    true

                Assert.Equal<byte>(before, File.ReadAllBytes project)

                PipeTest.previewAndExecute
                    child
                    5u
                    "project.item.remove"
                    projectId
                    (PipeTest.map [ "path", RpcValue.String content ])
                    1L
                    true

                Assert.True(File.Exists content)
                Assert.Contains("<None Remove=\"Extra.txt\"", File.ReadAllText project)

                let projectBeforeExternalCopy = File.ReadAllBytes project

                PipeTest.previewAndExecute
                    child
                    7u
                    "project.item.add"
                    projectId
                    (PipeTest.map [ "path", RpcValue.String externalCopy; "itemType", RpcValue.String "None" ])
                    2L
                    true

                let copied = Path.Combine(directory, "External.txt")
                Assert.True(File.Exists copied)
                Assert.Equal("copy", File.ReadAllText copied)
                Assert.Equal<byte>(projectBeforeExternalCopy, File.ReadAllBytes project)

                PipeTest.previewAndExecute
                    child
                    9u
                    "project.item.add"
                    projectId
                    (PipeTest.map
                        [ "path", RpcValue.String externalLink
                          "itemType", RpcValue.String "Content"
                          "link", RpcValue.Boolean true ])
                    3L
                    true

                let linkedXml = File.ReadAllText project
                Assert.True(File.Exists externalLink)
                Assert.False(File.Exists(Path.Combine(directory, "Linked.txt")))
                Assert.Contains("<Content Include=", linkedXml)
                Assert.Contains("<Link>Linked.txt</Link>", linkedXml)

                let projectBeforeNew = File.ReadAllBytes project

                PipeTest.previewAndExecute
                    child
                    11u
                    "project.item.new"
                    projectId
                    (PipeTest.map
                        [ "path", RpcValue.String created
                          "itemType", RpcValue.String "Compile"
                          "contents", RpcValue.String "class New { }" ])
                    4L
                    true

                Assert.Equal("class New { }", File.ReadAllText created)
                Assert.Equal<byte>(projectBeforeNew, File.ReadAllBytes project)

                let projectBeforeCopy = File.ReadAllBytes project

                PipeTest.previewAndExecute
                    child
                    13u
                    "project.item.copy"
                    projectId
                    (PipeTest.map
                        [ "source", RpcValue.String copySource
                          "path", RpcValue.String copiedItem
                          "itemType", RpcValue.String "None" ])
                    5L
                    true

                Assert.Equal("copy source", File.ReadAllText copiedItem)
                Assert.Equal<byte>(projectBeforeCopy, File.ReadAllBytes project)

                PipeTest.previewAndExecute
                    child
                    15u
                    "project.item.set-metadata"
                    projectId
                    (PipeTest.map
                        [ "path", RpcValue.String source
                          "name", RpcValue.String "CopyToOutputDirectory"
                          "value", RpcValue.String "Always" ])
                    6L
                    true

                let metadataXml = File.ReadAllText project
                Assert.Contains("<Compile Update=\"Extra.cs\"", metadataXml)
                Assert.DoesNotContain("<Compile Include=\"Extra.cs\"", metadataXml)
                Assert.Contains("<CopyToOutputDirectory>Always</CopyToOutputDirectory>", metadataXml)

                PipeTest.previewAndExecute
                    child
                    17u
                    "project.item.set-build-action"
                    projectId
                    (PipeTest.map [ "path", RpcValue.String created; "itemType", RpcValue.String "Content" ])
                    7L
                    true

                let buildActionXml = File.ReadAllText project
                Assert.Contains("<Compile Remove=\"New.cs\"", buildActionXml)
                Assert.Contains("<Content Include=\"New.cs\"", buildActionXml)

                PipeTest.previewAndExecute
                    child
                    19u
                    "project.item.rename"
                    projectId
                    (PipeTest.map [ "path", RpcValue.String copiedItem; "name", RpcValue.String "Renamed.txt" ])
                    8L
                    true

                Assert.False(File.Exists copiedItem)
                Assert.True(File.Exists renamedItem)

                PipeTest.previewAndExecute
                    child
                    21u
                    "project.item.move"
                    projectId
                    (PipeTest.map
                        [ "path", RpcValue.String renamedItem
                          "destination", RpcValue.String movedItem ])
                    9L
                    true

                Assert.False(File.Exists renamedItem)
                Assert.True(File.Exists movedItem)

                PipeTest.previewAndExecute
                    child
                    23u
                    "project.item.remove"
                    projectId
                    (PipeTest.map [ "path", RpcValue.String movedItem ])
                    10L
                    true

                let explicitRemovedXml = File.ReadAllText project
                Assert.DoesNotContain("<Content Remove=\"Moved/Renamed.txt\"", explicitRemovedXml)
                Assert.True(File.Exists movedItem)

                PipeTest.previewAndExecute
                    child
                    25u
                    "project.item.remove"
                    projectId
                    (PipeTest.map [ "path", RpcValue.String source ])
                    11L
                    true

                let removedXml = File.ReadAllText project
                Assert.Contains("<Compile Remove=\"Extra.cs\"", removedXml)
                Assert.DoesNotContain("<Compile Update=\"Extra.cs\"", removedXml)
                Assert.True(File.Exists source)

                PipeTest.previewAndExecute
                    child
                    27u
                    "project.item.delete"
                    projectId
                    (PipeTest.map [ "path", RpcValue.String deleted ])
                    12L
                    true

                Assert.False(File.Exists deleted)
                Assert.Contains("<None Remove=\"Delete.txt\"", File.ReadAllText project)

                if OperatingSystem.IsLinux() then
                    let trashed =
                        Directory.EnumerateFiles(Path.Combine(trashHome, "Trash", "files"))
                        |> Seq.exactlyOne

                    Assert.Equal("delete", File.ReadAllText trashed)

                let xmlBeforeCollision = File.ReadAllBytes project

                let collision =
                    PipeTest.map
                        [ "commandId", RpcValue.String "project.item.add"
                          "targetId", RpcValue.String projectId
                          "arguments",
                          PipeTest.map [ "path", RpcValue.String externalCopy; "itemType", RpcValue.String "Content" ]
                          "expectedRevision", RpcValue.Integer 13L ]

                PipeTest.send child false (PipeTest.request 29u "command/preview" collision)
                let collisionError, _ = PipeTest.readFrame child |> PipeTest.response 29u
                Assert.Equal("invalid_input", collisionError.Value.Code)
                Assert.Equal<byte>(xmlBeforeCollision, File.ReadAllBytes project)
                Assert.Equal("copy", File.ReadAllText copied)

                let generatedRequest =
                    PipeTest.map
                        [ "commandId", RpcValue.String "project.item.new"
                          "targetId", RpcValue.String projectId
                          "arguments",
                          PipeTest.map [ "path", RpcValue.String generated; "itemType", RpcValue.String "Compile" ]
                          "expectedRevision", RpcValue.Integer 13L ]

                PipeTest.send child false (PipeTest.request 30u "command/preview" generatedRequest)
                let generatedError, _ = PipeTest.readFrame child |> PipeTest.response 30u
                Assert.Equal("invalid_input", generatedError.Value.Code)

                let externalDirectoryRequest =
                    PipeTest.map
                        [ "commandId", RpcValue.String "project.item.add"
                          "targetId", RpcValue.String projectId
                          "arguments",
                          PipeTest.map
                              [ "path", RpcValue.String externalDirectory
                                "itemType", RpcValue.String "Content" ]
                          "expectedRevision", RpcValue.Integer 13L ]

                PipeTest.send child false (PipeTest.request 31u "command/preview" externalDirectoryRequest)
                let externalDirectoryError, _ = PipeTest.readFrame child |> PipeTest.response 31u
                Assert.Equal("invalid_input", externalDirectoryError.Value.Code)

                let localLinkRequest =
                    PipeTest.map
                        [ "commandId", RpcValue.String "project.item.add"
                          "targetId", RpcValue.String projectId
                          "arguments",
                          PipeTest.map
                              [ "path", RpcValue.String source
                                "itemType", RpcValue.String "Compile"
                                "link", RpcValue.Boolean true ]
                          "expectedRevision", RpcValue.Integer 13L ]

                PipeTest.send child false (PipeTest.request 32u "command/preview" localLinkRequest)
                let localLinkError, _ = PipeTest.readFrame child |> PipeTest.response 32u
                Assert.Equal("invalid_input", localLinkError.Value.Code)
                PipeTest.shutdown child 33u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

            if Directory.Exists external then
                Directory.Delete(external, true)

    [<Fact>]
    member _.``should write a curated project property through the previewed public command``() =
        let directory = PipeTest.temporaryDirectory "project-property-command"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.fsproj")
            let props = Path.Combine(directory, "Directory.Build.props")
            let unknown = Path.Combine(directory, "Unknown.csproj")
            let model = SolutionModel()
            model.AddProject("Demo.fsproj", "Demo", null) |> ignore
            model.AddProject("Unknown.csproj", "Unknown", null) |> ignore

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <!-- retain -->\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>"
            )

            File.WriteAllText(
                props,
                "<Project>\r\n  <!-- shared -->\r\n  <PropertyGroup>\r\n    <AssemblyName>Shared.Unconditional</AssemblyName>\r\n    <Version Condition=\"'$(Configuration)' == 'Debug'\">1.0.0</Version>\r\n  </PropertyGroup>\r\n</Project>\r\n"
            )

            File.WriteAllText(unknown, "<Project><PropertyGroup><Value>readable</Value></PropertyGroup></Project>")

            PipeTest.save solution model

            let initialize =
                PipeTest.map
                    [ "protocolVersion", PipeTest.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
                      "clientInfo", PipeTest.map [ "name", RpcValue.String "test" ]
                      "capabilities",
                      RpcValue.array
                          [ RpcValue.String "workspace.root"
                            RpcValue.String "workspace.children"
                            RpcValue.String "workspace.delta"
                            RpcValue.String "workspace.export"
                            RpcValue.String "workspace.refresh"
                            RpcValue.String "operation.cancel"
                            RpcValue.String "unknown.claim" ]
                      "limits",
                      PipeTest.map
                          [ "maxFrameBytes", RpcValue.Integer 65536L
                            "maxPageSize", RpcValue.Integer 100L ] ]

            use child = PipeTest.startPipe "solution" solution

            try
                PipeTest.send child false (PipeTest.request 1u "initialize" initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore
                PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let rootError, root = PipeTest.readFrame child |> PipeTest.response 2u

                match rootError with
                | Some error -> failwithf "Workspace root failed: %s: %s" error.Code error.Message
                | None -> ()

                let projectId name =
                    PipeTest.field "nodes" root
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        PipeTest.field "kind" node = RpcValue.String "project"
                        && PipeTest.field "name" node = RpcValue.String name)
                    |> PipeTest.field "id"
                    |> RpcValue.requireString "id"

                let demoId = projectId "Demo"
                let unknownId = projectId "Unknown"

                let localArguments =
                    PipeTest.map
                        [ "name", RpcValue.String "RootNamespace"
                          "value", RpcValue.String "Demo.Root" ]

                PipeTest.previewAndExecute child 3u "project.property.set" demoId localArguments 0L true

                let localContents = File.ReadAllText project
                Assert.Contains("<!-- retain -->", localContents)
                Assert.Contains("<RootNamespace>Demo.Root</RootNamespace>", localContents)
                let projectBeforeImportedPreview = File.ReadAllBytes project
                let propsBeforeImportedPreview = File.ReadAllBytes props

                let importedWithoutScope =
                    PipeTest.map
                        [ "commandId", RpcValue.String "project.property.set"
                          "targetId", RpcValue.String demoId
                          "arguments",
                          PipeTest.map
                              [ "name", RpcValue.String "AssemblyName"
                                "value", RpcValue.String "Demo.Custom" ]
                          "expectedRevision", RpcValue.Integer 1L ]

                PipeTest.send child false (PipeTest.request 5u "command/preview" importedWithoutScope)
                let importedWithoutScopeError, _ = PipeTest.readFrame child |> PipeTest.response 5u
                Assert.Equal("invalid_input", importedWithoutScopeError.Value.Code)
                Assert.Equal<byte>(projectBeforeImportedPreview, File.ReadAllBytes project)
                Assert.Equal<byte>(propsBeforeImportedPreview, File.ReadAllBytes props)

                let propertyLevelWithoutScope =
                    PipeTest.map
                        [ "commandId", RpcValue.String "project.property.set"
                          "targetId", RpcValue.String demoId
                          "arguments",
                          PipeTest.map [ "name", RpcValue.String "Version"; "value", RpcValue.String "2.0.0" ]
                          "expectedRevision", RpcValue.Integer 1L ]

                PipeTest.send child false (PipeTest.request 50u "command/preview" propertyLevelWithoutScope)

                let propertyLevelWithoutScopeError, _ =
                    PipeTest.readFrame child |> PipeTest.response 50u

                Assert.Equal("invalid_input", propertyLevelWithoutScopeError.Value.Code)

                let propertyLevelWithScope =
                    PipeTest.map
                        [ "commandId", RpcValue.String "project.property.set"
                          "targetId", RpcValue.String demoId
                          "arguments",
                          PipeTest.map
                              [ "name", RpcValue.String "Version"
                                "value", RpcValue.String "2.0.0"
                                "scope", RpcValue.String props
                                "condition", RpcValue.String "'$(Configuration)' == 'Debug'" ]
                          "expectedRevision", RpcValue.Integer 1L ]

                PipeTest.send child false (PipeTest.request 51u "command/preview" propertyLevelWithScope)

                let propertyLevelWithScopeError, _ =
                    PipeTest.readFrame child |> PipeTest.response 51u

                Assert.Equal("invalid_input", propertyLevelWithScopeError.Value.Code)

                let importedWithScope =
                    PipeTest.map
                        [ "name", RpcValue.String "AssemblyName"
                          "value", RpcValue.String "Shared.After"
                          "scope", RpcValue.String props
                          "condition", RpcValue.String "'$(Configuration)' == 'Debug'" ]

                PipeTest.previewAndExecute child 6u "project.property.set" demoId importedWithScope 1L true

                Assert.Equal<byte>(projectBeforeImportedPreview, File.ReadAllBytes project)
                let propsContents = File.ReadAllText props
                Assert.Contains("\r\n", propsContents)
                Assert.Contains("<!-- shared -->", propsContents)
                Assert.Contains("<AssemblyName>Shared.Unconditional</AssemblyName>", propsContents)
                Assert.Contains("<AssemblyName>Shared.After</AssemblyName>", propsContents)

                let children =
                    PipeTest.map [ "parentId", RpcValue.String demoId; "pageSize", RpcValue.Integer 100L ]

                PipeTest.send child false (PipeTest.request 8u "workspace/children" children)
                let childrenError, childrenResult = PipeTest.readFrame child |> PipeTest.response 8u

                match childrenError with
                | Some error -> failwithf "Property children failed: %s: %s" error.Code error.Message
                | None -> ()

                let propertyNodes =
                    PipeTest.field "nodes" childrenResult |> RpcValue.requireArray "nodes"

                let declaredImportName =
                    RpcValue.String
                        "Declared AssemblyName = Shared.After [scope: Directory.Build.props; condition: '$(Configuration)' == 'Debug']"

                let declaredRootNamespaceName =
                    RpcValue.String "Declared RootNamespace = Demo.Root [scope: Demo.fsproj; condition: <none>]"

                let declaredImport =
                    propertyNodes
                    |> Seq.find (fun node -> PipeTest.field "name" node = declaredImportName)

                propertyNodes
                |> Seq.exists (fun node ->
                    PipeTest.field "name" node
                    |> RpcValue.requireString "name"
                    |> fun name -> name.StartsWith("Evaluated AssemblyName = ", StringComparison.Ordinal))
                |> Assert.True

                propertyNodes
                |> Seq.exists (fun node -> PipeTest.field "name" node = declaredRootNamespaceName)
                |> Assert.True

                match PipeTest.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        2L,
                        PipeTest.field "baseRevision" parameters
                        |> RpcValue.requireInteger "baseRevision"
                    )

                    Assert.Equal(3L, PipeTest.field "newRevision" parameters |> RpcValue.requireInteger "newRevision")
                | frame -> failwithf "Expected declared-property hydration delta, got %A" frame

                PipeTest.send child false (PipeTest.request 9u "workspace/children" children)

                let repeatedChildrenError, repeatedChildren =
                    PipeTest.readFrame child |> PipeTest.response 9u

                Assert.True(repeatedChildrenError.IsNone)

                let repeatedImport =
                    PipeTest.field "nodes" repeatedChildren
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node -> PipeTest.field "name" node = declaredImportName)

                Assert.Equal(PipeTest.field "id" declaredImport, PipeTest.field "id" repeatedImport)
                let unknownBefore = File.ReadAllBytes unknown

                let unknownPreview =
                    PipeTest.map
                        [ "commandId", RpcValue.String "project.property.set"
                          "targetId", RpcValue.String unknownId
                          "arguments", localArguments
                          "expectedRevision", RpcValue.Integer 3L ]

                PipeTest.send child false (PipeTest.request 10u "command/preview" unknownPreview)
                let unknownError, _ = PipeTest.readFrame child |> PipeTest.response 10u
                Assert.Equal("unsupported_capability", unknownError.Value.Code)
                Assert.Equal<byte>(unknownBefore, File.ReadAllBytes unknown)
                PipeTest.shutdown child 11u
            finally
                PipeTest.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

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
                        [ "protocolVersion", PipeTest.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
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

                Assert.True(initializeError.IsNone)

                let workspaceId =
                    PipeTest.field "workspace" initializeResult
                    |> PipeTest.field "id"
                    |> RpcValue.requireString "id"

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
                            && PipeTest.field "name" node = RpcValue.String "Evaluated WatchedValue = changed")

                    continuation <-
                        match PipeTest.field "nextToken" projectPage with
                        | RpcValue.String token -> Some token
                        | RpcValue.Nil -> None
                        | value -> failwithf "Unexpected continuation token: %A" value

                    hasMore <- continuation.IsSome
                    requestId <- requestId + 1u

                Assert.True(watchedValueFound, "Fresh project paging did not expose Evaluated WatchedValue = changed.")

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

                    let workspaceTarget = PipeTest.map [ "targetId", RpcValue.String workspaceId ]
                    PipeTest.send child false (PipeTest.request 101u "command/list" workspaceTarget)
                    let commandError, commands = PipeTest.readFrame child |> PipeTest.response 101u
                    Assert.True(commandError.IsNone)

                    PipeTest.field "commands" commands
                    |> RpcValue.requireArray "commands"
                    |> Seq.exists (fun command -> PipeTest.field "id" command = RpcValue.String "solution.project.add")
                    |> Assert.True
                | frame -> failwithf "Expected a toolset reset, got %A" frame

                PipeTest.shutdown child 102u
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

    [<Fact>]
    member _.``should hydrate preview and execute a project rename before publishing its delta``() =
        let directory = PipeTest.temporaryDirectory "pipe-command"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let source = Path.Combine(directory, "One.fsproj")
            let destination = Path.Combine(directory, "Renamed.fsproj")
            let model = SolutionModel()
            model.AddProject("One.fsproj", null, null) |> ignore
            PipeTest.writeProject source
            PipeTest.save solution model
            use child = PipeTest.startPipe "solution" solution

            try
                let initialize =
                    PipeTest.map
                        [ "protocolVersion", PipeTest.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
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

                Assert.True(initializeError.IsNone)

                let workspaceId =
                    PipeTest.field "workspace" initializeResult
                    |> PipeTest.field "id"
                    |> RpcValue.requireString "id"

                let workspaceTarget = PipeTest.map [ "targetId", RpcValue.String workspaceId ]
                PipeTest.send child false (PipeTest.request 30u "command/list" workspaceTarget)

                let workspaceListError, workspaceList =
                    PipeTest.readFrame child |> PipeTest.response 30u

                Assert.True(workspaceListError.IsNone)

                PipeTest.field "commands" workspaceList
                |> RpcValue.requireArray "commands"
                |> Seq.exists (fun command -> PipeTest.field "id" command = RpcValue.String "solution.project.add")
                |> Assert.True

                PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
                let rootError, rootResult = PipeTest.readFrame child |> PipeTest.response 2u
                Assert.True(rootError.IsNone)

                let projectId =
                    PipeTest.field "nodes" rootResult
                    |> RpcValue.requireArray "nodes"
                    |> Seq.filter (fun node -> PipeTest.field "kind" node = RpcValue.String "project")
                    |> Seq.map (PipeTest.field "id" >> RpcValue.requireString "id")
                    |> Seq.exactlyOne

                let children =
                    PipeTest.map [ "parentId", RpcValue.String projectId; "pageSize", RpcValue.Integer 50L ]

                PipeTest.send child false (PipeTest.request 3u "workspace/children" children)
                let hydrationError, _ = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.True(hydrationError.IsNone)

                match PipeTest.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(0L, PipeTest.field "baseRevision" parameters |> RpcValue.requireInteger "revision")
                    Assert.Equal(1L, PipeTest.field "newRevision" parameters |> RpcValue.requireInteger "revision")
                | frame -> failwithf "Expected the hydration delta, got %A" frame

                let target = PipeTest.map [ "targetId", RpcValue.String projectId ]
                PipeTest.send child false (PipeTest.request 4u "command/list" target)
                let listError, listResult = PipeTest.readFrame child |> PipeTest.response 4u
                Assert.True(listError.IsNone)

                PipeTest.field "commands" listResult
                |> RpcValue.requireArray "commands"
                |> Seq.exists (fun command -> PipeTest.field "id" command = RpcValue.String "solution.project.rename")
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
                Assert.True(previewError.IsNone)

                let previewId =
                    PipeTest.field "previewId" previewResult |> RpcValue.requireString "previewId"

                let execute =
                    PipeTest.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 1L
                          "previewId", RpcValue.String previewId ]

                PipeTest.send child false (PipeTest.request 6u "command/execute" execute)
                let executeError, executeResult = PipeTest.readFrame child |> PipeTest.response 6u
                Assert.True(executeError.IsNone)
                Assert.Equal(2L, PipeTest.field "revision" executeResult |> RpcValue.requireInteger "revision")

                match PipeTest.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        1L,
                        PipeTest.field "baseRevision" parameters
                        |> RpcValue.requireInteger "baseRevision"
                    )

                    Assert.Equal(2L, PipeTest.field "newRevision" parameters |> RpcValue.requireInteger "newRevision")
                | frame -> failwithf "Expected the transaction delta after the execute response, got %A" frame

                Assert.False(File.Exists source)
                Assert.True(File.Exists destination)

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
    member _.``should expose no write commands and refuse mutation requests for a solution filter``() =
        let directory = PipeTest.temporaryDirectory "pipe-command-slnf"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let filter = Path.Combine(directory, "Demo.slnf")
            PipeTest.save solution (SolutionModel())
            File.WriteAllText(filter, """{ "solution": { "path": "Demo.slnx" } }""")
            let before = File.ReadAllBytes solution
            use child = PipeTest.startPipe "solution" filter

            try
                PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
                PipeTest.readFrame child |> PipeTest.response 1u |> ignore
                PipeTest.send child false (PipeTest.request 2u "command/list" RpcValue.emptyMap)
                let listError, listResult = PipeTest.readFrame child |> PipeTest.response 2u
                Assert.True(listError.IsNone)
                Assert.Empty(PipeTest.field "commands" listResult |> RpcValue.requireArray "commands")

                let describe = PipeTest.map [ "commandId", RpcValue.String "solution.folder.add" ]

                PipeTest.send child false (PipeTest.request 3u "command/describe" describe)
                let describeError, _ = PipeTest.readFrame child |> PipeTest.response 3u
                Assert.Equal("unsupported_capability", describeError.Value.Code)

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
