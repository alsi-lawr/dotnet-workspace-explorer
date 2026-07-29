namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Xml.Linq
open Dotnet.CLI.Plus.Transport
open FsUnit.Xunit
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module internal CanonicalAppHost =
    type Session =
        { Directory: string
          Solution: string
          Child: Process
          WorkspaceId: string
          ProjectId: string option
          FolderId: string option }

    type Completion =
        { Outcome: string
          Revision: int64
          Notifications: string list
          Output: string list
          WorkspaceNotifications: string list }

    let private initialize maximumFrameBytes =
        PipeTest.map
            [ "protocolVersion",
              PipeTest.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
              "clientInfo", PipeTest.map [ "name", RpcValue.String "canonical-test" ]
              "capabilities",
              RpcValue.array
                  [ RpcValue.String "workspace.root"
                    RpcValue.String "workspace.delta"
                    RpcValue.String "command.preview"
                    RpcValue.String "command.execute" ]
              "limits",
              PipeTest.map
                  [ "maxFrameBytes", RpcValue.Integer maximumFrameBytes
                    "maxPageSize", RpcValue.Integer 100L ] ]

    let private nodeId kind nodes =
        nodes
        |> Seq.tryFind (fun node -> PipeTest.field "kind" node = RpcValue.String kind)
        |> Option.map (PipeTest.field "id" >> RpcValue.requireString "id")

    let startWithFrameBytes name maximumFrameBytes environment setup =
        let directory = BrokerProcess.temporaryDirectory name
        let solution = Path.Combine(directory, "Demo.slnx")
        let model = SolutionModel()
        setup directory model
        PipeTest.save solution model

        let fakeHost = BrokerProcess.copyFakeHost directory

        let child =
            PipeTest.startPipeWithEnvironment
                "solution"
                solution
                [ "DOTNET_HOST_PATH", fakeHost
                  "DOTNET_PLUS_FAKE_HOST_MODE", "canonical"
                  yield! environment ]

        PipeTest.send child false (PipeTest.request 1u "initialize" (initialize maximumFrameBytes))
        let initializeError, initialized = PipeTest.readFrame child |> PipeTest.response 1u
        initializeError |> should equal None

        let workspaceId =
            PipeTest.field "workspace" initialized
            |> PipeTest.field "id"
            |> RpcValue.requireString "id"

        PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
        let rootError, root = PipeTest.readFrame child |> PipeTest.response 2u
        rootError |> should equal None
        let nodes = PipeTest.field "nodes" root |> RpcValue.requireArray "nodes"

        { Directory = directory
          Solution = solution
          Child = child
          WorkspaceId = workspaceId
          ProjectId = nodeId "project" nodes
          FolderId = nodeId "solutionFolder" nodes }

    let startWithEnvironment name environment setup =
        startWithFrameBytes name 4194304L environment setup

    let start name setup = startWithEnvironment name [] setup

    let stop session =
        try
            PipeTest.shutdown session.Child 99u
        finally
            PipeTest.disposeProcess session.Child
            BrokerProcess.delete session.Directory

    let argumentMap values = PipeTest.map values

    let private common commandId target arguments expectedRevision =
        let targetField =
            target
            |> Option.map (fun value -> [ "targetId", RpcValue.String value ])
            |> Option.defaultValue []

        [ "commandId", RpcValue.String commandId
          "arguments", arguments
          "expectedRevision", RpcValue.Integer expectedRevision ]
        @ targetField

    let private startOperation session requestId fields =
        PipeTest.send
            session.Child
            false
            (PipeTest.request requestId "command/execute" (PipeTest.map fields))

        let executeError, result =
            PipeTest.readFrame session.Child |> PipeTest.response requestId

        match executeError with
        | None -> ()
        | Some error -> failwithf "Canonical execute failed: %s: %s" error.Code error.Message

        PipeTest.field "operationId" result |> RpcValue.requireString "operationId"

    let beginMutation session requestId commandId target arguments expectedRevision =
        let fields = common commandId target arguments expectedRevision

        PipeTest.send
            session.Child
            false
            (PipeTest.request requestId "command/preview" (PipeTest.map fields))

        let previewError, preview =
            PipeTest.readFrame session.Child |> PipeTest.response requestId

        match previewError with
        | None -> ()
        | Some error -> failwithf "Canonical preview failed: %s: %s" error.Code error.Message

        let previewId = PipeTest.field "previewId" preview
        startOperation session (requestId + 1u) (("previewId", previewId) :: fields)

    let complete session operationId =
        let mutable completed = None
        let mutable nextSequence = 0L
        let notifications = ResizeArray<string>()
        let output = ResizeArray<string>()
        let workspaceNotifications = ResizeArray<string>()

        while completed.IsNone do
            match PipeTest.readFrame session.Child with
            | Notification(name, parameters) when
                name.StartsWith("operation/", StringComparison.Ordinal)
                ->
                PipeTest.field "operationId" parameters
                |> RpcValue.requireString "operationId"
                |> should equal operationId

                PipeTest.field "sequence" parameters
                |> RpcValue.requireInteger "sequence"
                |> should equal nextSequence

                nextSequence <- nextSequence + 1L
                notifications.Add name

                match name with
                | "operation/output" ->
                    output.Add(PipeTest.field "text" parameters |> RpcValue.requireString "text")
                | "operation/completed" ->
                    completed <-
                        Some
                            { Outcome =
                                PipeTest.field "outcome" parameters
                                |> RpcValue.requireString "outcome"
                              Revision =
                                PipeTest.field "revision" parameters
                                |> RpcValue.requireInteger "revision"
                              Notifications = notifications |> Seq.toList
                              Output = output |> Seq.toList
                              WorkspaceNotifications = workspaceNotifications |> Seq.toList }
                | _ -> ()
            | Notification(name, _) when name = "workspace/delta" || name = "workspace/reset" ->
                workspaceNotifications.Add name
            | frame -> failwithf "Unexpected canonical operation frame: %A" frame

        completed.Value

    let execute session requestId commandId target arguments expectedRevision =
        beginMutation session requestId commandId target arguments expectedRevision
        |> complete session

    let executeRead session requestId commandId target arguments expectedRevision =
        common commandId target arguments expectedRevision
        |> startOperation session requestId
        |> complete session

    let captured path =
        File.ReadAllLines path
        |> Array.map (fun line -> JsonSerializer.Deserialize<string array> line)

    let openSolution path =
        SolutionSerializers
            .GetSerializerByMoniker(path)
            .OpenAsync(path, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

type CanonicalCommandAppHostTests() =
    [<Fact>]
    member _.``should stream reference restore by default and pass exact canonical arguments``() =
        let run noRestore =
            let capture = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.jsonl")

            let session =
                CanonicalAppHost.startWithEnvironment
                    "canonical-reference"
                    [ "DOTNET_PLUS_FAKE_HOST_CAPTURE", capture ]
                    (fun directory model ->
                        File.WriteAllText(
                            Path.Combine(directory, "App.csproj"),
                            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
                        )

                        File.WriteAllText(
                            Path.Combine(directory, "Library.csproj"),
                            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
                        )

                        model.AddProject("App.csproj", "App", null) |> ignore)

            try
                let reference = Path.Combine(session.Directory, "Library.csproj")

                let values =
                    [ "path", RpcValue.String reference
                      "framework", RpcValue.String "net10.0"
                      "arguments",
                      RpcValue.array
                          [ RpcValue.String "--interactive"; RpcValue.String "--interactive" ] ]
                    |> fun values ->
                        if noRestore then
                            ("noRestore", RpcValue.Boolean true) :: values
                        else
                            values

                let completion =
                    CanonicalAppHost.execute
                        session
                        3u
                        "reference.add"
                        session.ProjectId
                        (CanonicalAppHost.argumentMap values)
                        0L

                completion.Outcome |> should equal "succeeded"
                completion.Revision |> should equal 1L
                completion.Notifications |> should contain "operation/progress"

                let expected =
                    [| "reference"
                       "add"
                       "--project"
                       Path.Combine(session.Directory, "App.csproj")
                       "--framework"
                       "net10.0"
                       reference
                       "--interactive"
                       "--interactive" |]

                let invocations = CanonicalAppHost.captured capture
                invocations[0] |> should equal expected

                if noRestore then
                    invocations.Length |> should equal 1
                else
                    invocations.Length |> should equal 2

                    invocations[1]
                    |> should equal [| "restore"; Path.Combine(session.Directory, "App.csproj") |]
            finally
                CanonicalAppHost.stop session

                if File.Exists capture then
                    File.Delete capture

        run false
        run true

    [<Fact>]
    member _.``should centralize a conditional package version and enable central management``() =
        let condition = "'$(TargetFramework)' == 'net10.0'"

        let session =
            CanonicalAppHost.start "canonical-package" (fun directory model ->
                let project = Path.Combine(directory, "App.csproj")
                let otherCondition = "'$(TargetFramework)' == 'net9.0'"

                File.WriteAllText(
                    project,
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                    + "<TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                    + $"<ItemGroup Condition=\"{condition}\">"
                    + "<PackageReference Include=\"Example.Package\" Version=\"1.0.0\" />"
                    + "</ItemGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(directory, "Directory.Packages.props"),
                    "<Project><PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">"
                    + "<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>"
                    + $"</PropertyGroup><ItemGroup Condition=\"{condition}\">"
                    + "<PackageVersion Include=\"example.package\" Version=\"2.0.0\" />"
                    + $"</ItemGroup><ItemGroup Condition=\"{otherCondition}\">"
                    + "<PackageVersion Include=\"Example.Package\" Version=\"9.0.0\" />"
                    + "</ItemGroup></Project>"
                )

                model.AddProject("App.csproj", "App", null) |> ignore)

        try
            let arguments =
                CanonicalAppHost.argumentMap
                    [ "id", RpcValue.String "Example.Package"; "version", RpcValue.String "2.0.0" ]

            let completion =
                CanonicalAppHost.execute session 3u "package.update" session.ProjectId arguments 0L

            completion.Outcome |> should equal "succeeded"
            completion.Revision |> should equal 1L
            let project = XDocument.Load(Path.Combine(session.Directory, "App.csproj"))

            project.Descendants(XName.Get "PackageReference")
            |> Seq.exactlyOne
            |> _.Attribute(XName.Get "Version")
            |> isNull
            |> should equal true

            let owner =
                XDocument.Load(Path.Combine(session.Directory, "Directory.Packages.props"))

            let centralProperties =
                owner.Descendants(XName.Get "ManagePackageVersionsCentrally") |> Seq.toArray

            centralProperties.Length |> should equal 2

            centralProperties
            |> Seq.find (fun property -> isNull (property.Parent.Attribute(XName.Get "Condition")))
            |> _.Value
            |> should equal "true"

            centralProperties
            |> Seq.find (fun property ->
                not (isNull (property.Parent.Attribute(XName.Get "Condition"))))
            |> _.Value
            |> should equal "false"

            let versions = owner.Descendants(XName.Get "PackageVersion") |> Seq.toArray
            versions.Length |> should equal 2

            let version =
                versions
                |> Seq.find (fun item ->
                    item.Parent.Attribute(XName.Get "Condition").Value = condition)

            version.Attribute(XName.Get "Include").Value |> should equal "example.package"
            version.Attribute(XName.Get "Version").Value |> should equal "2.0.0"

            versions
            |> Seq.find (fun item ->
                item.Parent.Attribute(XName.Get "Condition").Value <> condition)
            |> _.Attribute(XName.Get "Version")
            |> _.Value
            |> should equal "9.0.0"
        finally
            CanonicalAppHost.stop session

    [<Fact>]
    member _.``should reject a package mutation owned below the selected workspace root``() =
        let session =
            CanonicalAppHost.start "canonical-nested-package-owner" (fun directory model ->
                let projectDirectory = Path.Combine(directory, "src")
                Directory.CreateDirectory projectDirectory |> ignore

                File.WriteAllText(
                    Path.Combine(projectDirectory, "App.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                    + "<TargetFramework>net10.0</TargetFramework>"
                    + "</PropertyGroup><ItemGroup>"
                    + "<PackageReference Include=\"Example.Package\" />"
                    + "</ItemGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(projectDirectory, "Directory.Packages.props"),
                    "<Project><PropertyGroup>"
                    + "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
                    + "</PropertyGroup><ItemGroup>"
                    + "<PackageVersion Include=\"Example.Package\" Version=\"1.0.0\" />"
                    + "</ItemGroup></Project>"
                )

                model.AddProject("src/App.csproj", "App", null) |> ignore)

        try
            let project = Path.Combine(session.Directory, "src", "App.csproj")
            let owner = Path.Combine(session.Directory, "src", "Directory.Packages.props")
            let projectBefore = File.ReadAllBytes project
            let ownerBefore = File.ReadAllBytes owner

            PipeTest.send
                session.Child
                false
                (PipeTest.request
                    3u
                    "command/preview"
                    (PipeTest.map
                        [ "commandId", RpcValue.String "package.update"
                          "targetId", RpcValue.String session.ProjectId.Value
                          "arguments",
                          CanonicalAppHost.argumentMap
                              [ "id", RpcValue.String "Example.Package"
                                "version", RpcValue.String "2.0.0" ]
                          "expectedRevision", RpcValue.Integer 0L ]))

            let previewError, _ = PipeTest.readFrame session.Child |> PipeTest.response 3u
            previewError.Value.Code |> should equal "invalid_input"

            previewError.Value.Message
            |> should equal "A nested Directory.Packages.props owns package versions."

            PipeTest.send
                session.Child
                false
                (PipeTest.request 4u "workspace/root" RpcValue.emptyMap)

            let rootError, root = PipeTest.readFrame session.Child |> PipeTest.response 4u
            rootError |> should equal None

            PipeTest.field "revision" root
            |> RpcValue.requireInteger "revision"
            |> should equal 0L

            File.ReadAllBytes project |> should equal projectBefore
            File.ReadAllBytes owner |> should equal ownerBefore
        finally
            CanonicalAppHost.stop session

    [<Fact>]
    member _.``should add one project to a logical folder at the requested physical path``() =
        let session =
            CanonicalAppHost.start "canonical-template" (fun _ model ->
                model.AddFolder "/tools/" |> ignore)

        try
            let output = Path.Combine(session.Directory, "generated", "tool")

            let arguments =
                CanonicalAppHost.argumentMap
                    [ "template", RpcValue.String "console"; "output", RpcValue.String output ]

            let completion =
                CanonicalAppHost.execute session 3u "template.create" session.FolderId arguments 0L

            completion.Outcome |> should equal "succeeded"
            completion.Revision |> should equal 1L

            Directory.GetFiles(output, "*.*proj", SearchOption.AllDirectories).Length
            |> should equal 1

            let reopened = CanonicalAppHost.openSolution session.Solution
            reopened.SolutionProjects.Count |> should equal 1
            let project = reopened.SolutionProjects |> Seq.exactlyOne
            project.Parent.Path |> should equal "/tools/"

            Path.GetFullPath(Path.Combine(session.Directory, project.FilePath))
            |> should equal (Path.Combine(output, "Template.fsproj"))
        finally
            CanonicalAppHost.stop session

    [<Fact>]
    member _.``should compensate failed template creation without changing solution or output``() =
        let session =
            CanonicalAppHost.startWithEnvironment
                "canonical-template-failure"
                [ "DOTNET_PLUS_FAKE_HOST_FAIL_AFTER_MUTATION", "true" ]
                (fun _ _ -> ())

        try
            let output = Path.Combine(session.Directory, "failed-output")
            let before = File.ReadAllBytes session.Solution

            let arguments =
                CanonicalAppHost.argumentMap
                    [ "template", RpcValue.String "console"; "output", RpcValue.String output ]

            let completion =
                CanonicalAppHost.execute session 3u "template.create" None arguments 0L

            completion.Outcome |> should equal "failed"
            completion.Revision |> should equal 0L
            completion.Notifications |> should contain "operation/progress"
            completion.Notifications |> should contain "operation/output"

            completion.Output
            |> String.concat String.Empty
            |> should equal "fake host failure after mutation"

            File.ReadAllBytes session.Solution |> should equal before
            Directory.Exists output |> should equal false
        finally
            CanonicalAppHost.stop session

    [<Fact>]
    member _.``should restore package files when the child fails after mutation``() =
        let session =
            CanonicalAppHost.startWithEnvironment
                "canonical-package-failure"
                [ "DOTNET_PLUS_FAKE_HOST_FAIL_AFTER_MUTATION", "true" ]
                (fun directory model ->
                    File.WriteAllText(
                        Path.Combine(directory, "App.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
                        + "<PackageReference Include=\"Example.Package\" Version=\"1.0.0\" />"
                        + "</ItemGroup></Project>"
                    )

                    model.AddProject("App.csproj", "App", null) |> ignore)

        try
            let project = Path.Combine(session.Directory, "App.csproj")
            let owner = Path.Combine(session.Directory, "Directory.Packages.props")
            let before = File.ReadAllBytes project

            let arguments =
                CanonicalAppHost.argumentMap
                    [ "id", RpcValue.String "Example.Package"; "version", RpcValue.String "2.0.0" ]

            let completion =
                CanonicalAppHost.execute session 3u "package.update" session.ProjectId arguments 0L

            completion.Outcome |> should equal "failed"
            completion.Revision |> should equal 0L
            File.ReadAllBytes project |> should equal before
            File.Exists owner |> should equal false
        finally
            CanonicalAppHost.stop session

    [<Fact>]
    member _.``should run template reads and either typed or passed through dry runs without edits``
        ()
        =
        let capture = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.jsonl")

        let session =
            CanonicalAppHost.startWithEnvironment
                "canonical-template-read"
                [ "DOTNET_PLUS_FAKE_HOST_CAPTURE", capture ]
                (fun _ _ -> ())

        try
            let empty = CanonicalAppHost.argumentMap []
            let listed = CanonicalAppHost.executeRead session 3u "template.list" None empty 0L
            listed.Outcome |> should equal "succeeded"

            let shown =
                CanonicalAppHost.executeRead
                    session
                    4u
                    "template.show"
                    None
                    (CanonicalAppHost.argumentMap
                        [ "template", RpcValue.String "console"
                          "arguments", RpcValue.array [ RpcValue.String "--language" ] ])
                    0L

            shown.Outcome |> should equal "succeeded"

            let typedOutput = Path.Combine(session.Directory, "typed-dry-run")

            let typed =
                CanonicalAppHost.execute
                    session
                    5u
                    "template.create"
                    None
                    (CanonicalAppHost.argumentMap
                        [ "template", RpcValue.String "console"
                          "output", RpcValue.String typedOutput
                          "dryRun", RpcValue.Boolean true ])
                    0L

            typed.Outcome |> should equal "succeeded"
            typed.Revision |> should equal 0L
            Directory.Exists typedOutput |> should equal false

            let passedOutput = Path.Combine(session.Directory, "passed-dry-run")

            let passed =
                CanonicalAppHost.execute
                    session
                    7u
                    "template.create"
                    None
                    (CanonicalAppHost.argumentMap
                        [ "template", RpcValue.String "console"
                          "output", RpcValue.String passedOutput
                          "arguments", RpcValue.array [ RpcValue.String "--check-only=true" ] ])
                    0L

            passed.Outcome |> should equal "succeeded"
            passed.Revision |> should equal 0L
            Directory.Exists passedOutput |> should equal false

            let invocations = CanonicalAppHost.captured capture
            invocations.Length |> should equal 4
            invocations[0] |> should equal [| "new"; "list" |]
            invocations[1] |> should equal [| "new"; "details"; "console"; "--language" |]
            invocations[2] |> should contain "--dry-run"
            invocations[3] |> should contain "--check-only=true"
        finally
            CanonicalAppHost.stop session

            if File.Exists capture then
                File.Delete capture

    [<Fact>]
    member _.``should fragment streamed canonical output within the negotiated frame limit``() =
        let outputLength = 4096

        let session =
            CanonicalAppHost.startWithFrameBytes
                "canonical-output-frames"
                1024L
                [ "DOTNET_PLUS_FAKE_HOST_OUTPUT_LENGTH", string outputLength ]
                (fun _ _ -> ())

        try
            let completion =
                CanonicalAppHost.executeRead
                    session
                    3u
                    "template.list"
                    None
                    (CanonicalAppHost.argumentMap [])
                    0L

            completion.Outcome |> should equal "succeeded"
            completion.Output.Length |> should be (greaterThan 1)

            completion.Output
            |> String.concat String.Empty
            |> should equal (String('x', outputLength))
        finally
            CanonicalAppHost.stop session

    [<Fact>]
    member _.``should cancel one canonical operation once then reap and forget its child``() =
        let marker = Path.Combine(Path.GetTempPath(), $"canonical-{Guid.NewGuid():N}.pid")

        let release =
            Path.Combine(Path.GetTempPath(), $"canonical-{Guid.NewGuid():N}.release")

        let session =
            CanonicalAppHost.startWithEnvironment
                "canonical-cancel"
                [ "DOTNET_PLUS_FAKE_HOST_MARKER", marker
                  "DOTNET_PLUS_FAKE_HOST_RELEASE", release ]
                (fun directory model ->
                    File.WriteAllText(
                        Path.Combine(directory, "App.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\" />"
                    )

                    File.WriteAllText(
                        Path.Combine(directory, "Library.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\" />"
                    )

                    model.AddProject("App.csproj", "App", null) |> ignore)

        try
            let project = Path.Combine(session.Directory, "App.csproj")
            let before = File.ReadAllBytes project

            let operationId =
                CanonicalAppHost.beginMutation
                    session
                    3u
                    "reference.add"
                    session.ProjectId
                    (CanonicalAppHost.argumentMap
                        [ "path", RpcValue.String(Path.Combine(session.Directory, "Library.csproj"))
                          "noRestore", RpcValue.Boolean true ])
                    0L

            BrokerProcess.waitForFile marker
            let childPid = File.ReadAllText marker |> Int32.Parse

            PipeTest.send
                session.Child
                false
                (PipeTest.request
                    5u
                    "operation/cancel"
                    (PipeTest.map [ "operationId", RpcValue.String operationId ]))

            let mutable accepted = false
            let mutable completions = 0

            while not accepted || completions = 0 do
                match PipeTest.readFrame session.Child with
                | Response(5u, error, result) ->
                    error |> should equal None
                    PipeTest.field "accepted" result |> should equal (RpcValue.Boolean true)
                    accepted <- true
                | Notification("operation/progress", parameters) ->
                    PipeTest.field "operationId" parameters
                    |> should equal (RpcValue.String operationId)
                | Notification("operation/completed", parameters) ->
                    PipeTest.field "operationId" parameters
                    |> should equal (RpcValue.String operationId)

                    PipeTest.field "outcome" parameters
                    |> should equal (RpcValue.String "cancelled")

                    completions <- completions + 1
                | frame -> failwithf "Unexpected canonical cancellation frame: %A" frame

            completions |> should equal 1
            File.ReadAllBytes project |> should equal before

            Assert.Throws<ArgumentException>(fun () -> Process.GetProcessById childPid |> ignore)
            |> ignore

            PipeTest.send
                session.Child
                false
                (PipeTest.request
                    6u
                    "operation/cancel"
                    (PipeTest.map [ "operationId", RpcValue.String operationId ]))

            let secondError, second = PipeTest.readFrame session.Child |> PipeTest.response 6u
            secondError |> should equal None
            PipeTest.field "accepted" second |> should equal (RpcValue.Boolean false)
        finally
            CanonicalAppHost.stop session

            for path in [ marker; release ] do
                if File.Exists path then
                    File.Delete path

    [<Fact>]
    member _.``should move a project tree through one completed public operation``() =
        let session =
            CanonicalAppHost.start "physical-project-move" (fun directory model ->
                let source = Path.Combine(directory, "src", "One")
                let incoming = Path.Combine(directory, "src", "Ref")
                Directory.CreateDirectory(Path.Combine(source, "nested")) |> ignore
                Directory.CreateDirectory incoming |> ignore
                Directory.CreateDirectory(Path.Combine(directory, "moved")) |> ignore
                File.WriteAllText(Path.Combine(source, "nested", "keep.txt"), "keep")

                File.WriteAllText(
                    Path.Combine(source, "One.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(incoming, "Ref.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../One/One.fsproj\" Condition=\"'$(Configuration)' == 'Debug'\" /></ItemGroup><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                model.AddFolder "/moved/" |> ignore
                model.AddProject("src/One/One.fsproj", null, null) |> ignore
                model.AddProject("src/Ref/Ref.fsproj", null, null) |> ignore)

        try
            let completion =
                CanonicalAppHost.execute
                    session
                    3u
                    "project.physical-move"
                    session.ProjectId
                    (CanonicalAppHost.argumentMap
                        [ "destination", RpcValue.String "moved/One"
                          "folder", RpcValue.String session.FolderId.Value ])
                    0L

            completion.Outcome |> should equal "succeeded"

            completion.Notifications
            |> should equal [ "operation/progress"; "operation/completed" ]

            completion.WorkspaceNotifications |> should contain "workspace/delta"

            Directory.Exists(Path.Combine(session.Directory, "src", "One"))
            |> should equal false

            File.Exists(Path.Combine(session.Directory, "moved", "One", "One.fsproj"))
            |> should equal true

            File.ReadAllText(Path.Combine(session.Directory, "moved", "One", "nested", "keep.txt"))
            |> should equal "keep"

            File.ReadAllText(Path.Combine(session.Directory, "src", "Ref", "Ref.fsproj"))
            |> fun contents -> contents.Contains "moved/One/One.fsproj"
            |> should equal true

            File.ReadAllText(Path.Combine(session.Directory, "src", "Ref", "Ref.fsproj"))
            |> fun contents -> contents.Contains "Condition=\"'$(Configuration)' == 'Debug'\""
            |> should equal true

            CanonicalAppHost.openSolution session.Solution
            |> fun reopened ->
                reopened.SolutionProjects
                |> Seq.find (fun project ->
                    project.FilePath.Replace('\\', '/') = "moved/One/One.fsproj")
                |> fun project -> project.Parent.Path
                |> should equal "/moved/"
        finally
            CanonicalAppHost.stop session

    [<Fact>]
    member _.``should refuse a relocation when any direct project reference uses a macro``() =
        let session =
            CanonicalAppHost.start "physical-project-move-macro" (fun directory model ->
                let source = Path.Combine(directory, "src", "One")
                let incoming = Path.Combine(directory, "src", "Ref")
                Directory.CreateDirectory source |> ignore
                Directory.CreateDirectory incoming |> ignore
                Directory.CreateDirectory(Path.Combine(directory, "moved")) |> ignore

                File.WriteAllText(
                    Path.Combine(source, "One.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(incoming, "Ref.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"$(MSBuildProjectDirectory)/NoSuch.fsproj\" Condition=\"'$(Configuration)' == 'Never'\" /></ItemGroup><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                model.AddProject("src/One/One.fsproj", null, null) |> ignore
                model.AddProject("src/Ref/Ref.fsproj", null, null) |> ignore)

        try
            Assert.Throws<Exception>(fun () ->
                CanonicalAppHost.beginMutation
                    session
                    3u
                    "project.physical-move"
                    session.ProjectId
                    (CanonicalAppHost.argumentMap [ "destination", RpcValue.String "moved/One" ])
                    0L
                |> ignore)
            |> fun error -> error.Message.Contains "macro"
            |> should equal true

            Directory.Exists(Path.Combine(session.Directory, "src", "One"))
            |> should equal true

            Directory.Exists(Path.Combine(session.Directory, "moved", "One"))
            |> should equal false
        finally
            CanonicalAppHost.stop session

    [<Fact>]
    member _.``should refuse a relocation when an import declares an inactive project reference``
        ()
        =
        let session =
            CanonicalAppHost.start "physical-project-move-import" (fun directory model ->
                let source = Path.Combine(directory, "src", "One")
                let incoming = Path.Combine(directory, "src", "Ref")
                Directory.CreateDirectory source |> ignore
                Directory.CreateDirectory incoming |> ignore
                Directory.CreateDirectory(Path.Combine(directory, "moved")) |> ignore

                File.WriteAllText(
                    Path.Combine(source, "One.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(incoming, "Ref.props"),
                    "<Project><ItemGroup><ProjectReference Include=\"../One/One.fsproj\" Condition=\"'$(Configuration)' == 'Never'\" /></ItemGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(incoming, "Ref.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Ref.props\" /><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                model.AddProject("src/One/One.fsproj", null, null) |> ignore
                model.AddProject("src/Ref/Ref.fsproj", null, null) |> ignore)

        try
            Assert.Throws<Exception>(fun () ->
                CanonicalAppHost.beginMutation
                    session
                    3u
                    "project.physical-move"
                    session.ProjectId
                    (CanonicalAppHost.argumentMap [ "destination", RpcValue.String "moved/One" ])
                    0L
                |> ignore)
            |> fun error -> error.Message.Contains "declared by an import"
            |> should equal true

            Directory.Exists(Path.Combine(session.Directory, "src", "One"))
            |> should equal true

            Directory.Exists(Path.Combine(session.Directory, "moved", "One"))
            |> should equal false
        finally
            CanonicalAppHost.stop session
