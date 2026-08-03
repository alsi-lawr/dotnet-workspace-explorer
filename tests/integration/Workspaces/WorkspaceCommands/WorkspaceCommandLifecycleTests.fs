namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace-command scenarios")>]
type WorkspaceCommandLifecycleTests() =
    [<Fact>]
    member _.``a child failure after package mutation restores package files and the workspace revision``
        ()
        =
        let session =
            WorkspaceCommandScenario.startWithEnvironment
                "workspace-command-package-failure"
                [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_FAIL_AFTER_EDIT", "true" ]
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
                WorkspaceCommandScenario.argumentMap
                    [ "id", RpcValue.String "Example.Package"; "version", RpcValue.String "2.0.0" ]

            let completion =
                WorkspaceCommandScenario.execute
                    session
                    3u
                    "package.update"
                    session.ProjectId
                    arguments
                    0L

            completion.Outcome |> should equal "failed"
            completion.Revision |> should equal 0L
            File.ReadAllBytes project |> should equal before
            File.Exists owner |> should equal false
        finally
            WorkspaceCommandScenario.stop session

    [<Fact>]
    member _.``template reads and typed or passthrough dry runs leave workspace files unchanged``
        ()
        =
        let capture = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.jsonl")

        let session =
            WorkspaceCommandScenario.startWithEnvironment
                "workspace-command-template-read"
                [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CAPTURE_PATH", capture ]
                (fun _ _ -> ())

        try
            let empty = WorkspaceCommandScenario.argumentMap []

            let listed =
                WorkspaceCommandScenario.executeRead session 3u "template.list" None empty 0L

            listed.Outcome |> should equal "succeeded"

            let shown =
                WorkspaceCommandScenario.executeRead
                    session
                    4u
                    "template.describe"
                    None
                    (WorkspaceCommandScenario.argumentMap
                        [ "template", RpcValue.String "console"
                          "arguments", RpcValue.array [ RpcValue.String "--language" ] ])
                    0L

            shown.Outcome |> should equal "succeeded"

            let typedOutput = Path.Combine(session.Directory, "typed-dry-run")

            let typed =
                WorkspaceCommandScenario.execute
                    session
                    5u
                    "template.create"
                    None
                    (WorkspaceCommandScenario.argumentMap
                        [ "template", RpcValue.String "console"
                          "output", RpcValue.String typedOutput
                          "dryRun", RpcValue.Boolean true ])
                    0L

            typed.Outcome |> should equal "succeeded"
            typed.Revision |> should equal 0L
            Directory.Exists typedOutput |> should equal false

            let passedOutput = Path.Combine(session.Directory, "passed-dry-run")

            let passed =
                WorkspaceCommandScenario.execute
                    session
                    7u
                    "template.create"
                    None
                    (WorkspaceCommandScenario.argumentMap
                        [ "template", RpcValue.String "console"
                          "output", RpcValue.String passedOutput
                          "arguments", RpcValue.array [ RpcValue.String "--check-only=true" ] ])
                    0L

            passed.Outcome |> should equal "succeeded"
            passed.Revision |> should equal 0L
            Directory.Exists passedOutput |> should equal false

            let invocations = WorkspaceCommandScenario.captured capture
            invocations.Length |> should equal 4
            invocations[0] |> should equal [| "new"; "list" |]
            invocations[1] |> should equal [| "new"; "details"; "console"; "--language" |]
            invocations[2] |> should contain "--dry-run"
            invocations[3] |> should contain "--check-only=true"
        finally
            WorkspaceCommandScenario.stop session

            if File.Exists capture then
                File.Delete capture

    [<Fact>]
    member _.``streamed workspace command output is fragmented within the negotiated frame limit``
        ()
        =
        let outputLength = 4096

        let session =
            WorkspaceCommandScenario.startWithFrameBytes
                "workspace-command-output-frames"
                1024L
                [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_OUTPUT_LENGTH", string outputLength ]
                (fun _ _ -> ())

        try
            let completion =
                WorkspaceCommandScenario.executeRead
                    session
                    3u
                    "template.list"
                    None
                    (WorkspaceCommandScenario.argumentMap [])
                    0L

            completion.Outcome |> should equal "succeeded"
            completion.Output.Length |> should be (greaterThan 1)

            completion.Output
            |> String.concat String.Empty
            |> should equal (String('x', outputLength))
        finally
            WorkspaceCommandScenario.stop session

    [<Fact>]
    member _.``cancelling a workspace command reaps the child and forgets the operation``() =
        let marker =
            Path.Combine(Path.GetTempPath(), $"workspace-command-{Guid.NewGuid():N}.pid")

        let release =
            Path.Combine(Path.GetTempPath(), $"workspace-command-{Guid.NewGuid():N}.release")

        let session =
            WorkspaceCommandScenario.startWithEnvironment
                "workspace-command-cancel"
                [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH", marker
                  "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CONTINUE_PATH", release ]
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
                WorkspaceCommandScenario.beginMutation
                    session
                    3u
                    "reference.add"
                    session.ProjectId
                    (WorkspaceCommandScenario.argumentMap
                        [ "path", RpcValue.String(Path.Combine(session.Directory, "Library.csproj"))
                          "noRestore", RpcValue.Boolean true ])
                    0L

            DirectCommandProcess.waitForFile marker
            let childPid = File.ReadAllText marker |> Int32.Parse

            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request
                    5u
                    "workspace/operations/cancel"
                    (WorkspaceRpcScenario.map [ "operationId", RpcValue.String operationId ]))

            let mutable accepted = false
            let mutable completions = 0

            while not accepted || completions = 0 do
                match WorkspaceRpcScenario.readFrame session.Child with
                | Response(5u, Ok result) ->
                    WorkspaceRpcScenario.field "accepted" result
                    |> should equal (RpcValue.Boolean true)

                    accepted <- true
                | Notification("workspace/operations/progress", parameters) ->
                    WorkspaceRpcScenario.field "operationId" parameters
                    |> should equal (RpcValue.String operationId)
                | Notification("workspace/operations/completed", parameters) ->
                    WorkspaceRpcScenario.field "operationId" parameters
                    |> should equal (RpcValue.String operationId)

                    WorkspaceRpcScenario.field "outcome" parameters
                    |> should equal (RpcValue.String "cancelled")

                    completions <- completions + 1
                | frame -> failwithf "Unexpected workspace-command cancellation frame: %A" frame

            completions |> should equal 1
            File.ReadAllBytes project |> should equal before

            (fun () -> Process.GetProcessById childPid |> ignore)
            |> should throw typeof<ArgumentException>
            |> ignore

            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request
                    6u
                    "workspace/operations/cancel"
                    (WorkspaceRpcScenario.map [ "operationId", RpcValue.String operationId ]))

            let secondError, second =
                WorkspaceRpcScenario.readFrame session.Child |> WorkspaceRpcScenario.response 6u

            secondError |> should equal None

            WorkspaceRpcScenario.field "accepted" second
            |> should equal (RpcValue.Boolean false)
        finally
            WorkspaceCommandScenario.stop session

            for path in [ marker; release ] do
                if File.Exists path then
                    File.Delete path
