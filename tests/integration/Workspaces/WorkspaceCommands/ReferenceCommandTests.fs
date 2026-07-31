namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace-command scenarios")>]
type ReferenceCommandTests() =
    [<Fact>]
    member _.``reference restore streams by default and delegates the exact dotnet arguments``() =
        let run noRestore =
            let capture = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.jsonl")

            let session =
                WorkspaceCommandScenario.startWithEnvironment
                    "workspace-command-reference"
                    [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CAPTURE_PATH", capture ]
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
                    WorkspaceCommandScenario.execute
                        session
                        3u
                        "reference.add"
                        session.ProjectId
                        (WorkspaceCommandScenario.argumentMap values)
                        0L

                completion.Outcome |> should equal "succeeded"
                completion.Revision |> should equal 1L
                completion.Notifications |> should contain "workspace/operations/progress"

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

                let invocations = WorkspaceCommandScenario.captured capture
                invocations[0] |> should equal expected

                if noRestore then
                    invocations.Length |> should equal 1
                else
                    invocations.Length |> should equal 2

                    invocations[1]
                    |> should equal [| "restore"; Path.Combine(session.Directory, "App.csproj") |]
            finally
                WorkspaceCommandScenario.stop session

                if File.Exists capture then
                    File.Delete capture

        run false
        run true
