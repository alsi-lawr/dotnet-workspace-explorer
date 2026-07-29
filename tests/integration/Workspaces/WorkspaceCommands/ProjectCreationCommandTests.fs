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
    member _.``should add one project to a logical folder at the requested physical path``() =
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
    member _.``should compensate failed template creation without changing solution or output``() =
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
