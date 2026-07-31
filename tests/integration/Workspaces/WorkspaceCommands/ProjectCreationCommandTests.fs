namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace-command scenarios")>]
type ProjectCreationCommandTests() =
    [<Fact>]
    member _.``the workspace root target lists project creation and adds a generated project at the root``
        ()
        =
        let session =
            WorkspaceCommandScenario.start "workspace-command-semantic-root" (fun _ _ -> ())

        try
            let output = Path.Combine(session.Directory, "root-target")

            let target =
                WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String session.RootId ]

            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request 30u "workspace/commands/list" target)

            let listError, list =
                WorkspaceRpcScenario.readFrame session.Child
                |> WorkspaceRpcScenario.response 30u

            (listError.IsNone) |> should equal true

            (WorkspaceRpcScenario.field "commands" list |> RpcValue.requireArray "commands")
            |> Seq.exists (fun command ->
                WorkspaceRpcScenario.field "id" command = RpcValue.String "solution.project.add")
            |> should equal true

            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request
                    31u
                    "workspace/commands/describe"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "solution.project.add"
                          "targetNodeId", RpcValue.String session.RootId ]))

            let describeError, _ =
                WorkspaceRpcScenario.readFrame session.Child
                |> WorkspaceRpcScenario.response 31u

            (describeError.IsNone) |> should equal true

            let arguments =
                WorkspaceCommandScenario.argumentMap
                    [ "template", RpcValue.String "console"; "output", RpcValue.String output ]

            let completion =
                WorkspaceCommandScenario.execute
                    session
                    4u
                    "template.create"
                    (Some session.RootId)
                    arguments
                    0L

            completion.Outcome |> should equal "succeeded"
            completion.Revision |> should equal 1L

            Directory.GetFiles(output, "*.*proj", SearchOption.AllDirectories).Length
            |> should equal 1
        finally
            WorkspaceCommandScenario.stop session

    [<Fact>]
    member _.``a logical folder target adds one generated project at the requested physical path``
        ()
        =
        let session =
            WorkspaceCommandScenario.start "workspace-command-template" (fun _ model ->
                model.AddFolder "/tools/" |> ignore)

        try
            let output = Path.Combine(session.Directory, "generated", "tool")

            let arguments =
                WorkspaceCommandScenario.argumentMap
                    [ "template", RpcValue.String "console"; "output", RpcValue.String output ]

            let completion =
                WorkspaceCommandScenario.execute
                    session
                    3u
                    "template.create"
                    session.FolderId
                    arguments
                    0L

            completion.Outcome |> should equal "succeeded"
            completion.Revision |> should equal 1L

            Directory.GetFiles(output, "*.*proj", SearchOption.AllDirectories).Length
            |> should equal 1

            let reopened = WorkspaceCommandScenario.openSolution session.Solution
            reopened.SolutionProjects.Count |> should equal 1
            let project = reopened.SolutionProjects |> Seq.exactlyOne
            project.Parent.Path |> should equal "/tools/"

            Path.GetFullPath(Path.Combine(session.Directory, project.FilePath))
            |> should equal (Path.Combine(output, "Template.fsproj"))
        finally
            WorkspaceCommandScenario.stop session

    [<Fact>]
    member _.``a failed template creation compensates solution and output mutations``() =
        let session =
            WorkspaceCommandScenario.startWithEnvironment
                "workspace-command-template-failure"
                [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_FAIL_AFTER_EDIT", "true" ]
                (fun _ _ -> ())

        try
            let output = Path.Combine(session.Directory, "failed-output")
            let before = File.ReadAllBytes session.Solution

            let arguments =
                WorkspaceCommandScenario.argumentMap
                    [ "template", RpcValue.String "console"; "output", RpcValue.String output ]

            let completion =
                WorkspaceCommandScenario.execute session 3u "template.create" None arguments 0L

            completion.Outcome |> should equal "failed"
            completion.Revision |> should equal 0L
            completion.Notifications |> should contain "workspace/operations/progress"
            completion.Notifications |> should contain "workspace/operations/output"

            completion.Output
            |> String.concat String.Empty
            |> should equal "scripted dotnet failure after mutation"

            File.ReadAllBytes session.Solution |> should equal before
            Directory.Exists output |> should equal false
        finally
            WorkspaceCommandScenario.stop session
