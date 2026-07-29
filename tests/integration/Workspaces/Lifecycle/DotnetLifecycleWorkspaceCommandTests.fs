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

[<Collection("Workspace-command scenarios")>]
type DotnetLifecycleWorkspaceCommandTests() =
    [<Fact>]
    member _.``should pass each workspace RPC lifecycle mapping to one ordinary dotnet child``() =
        let capture =
            Path.Combine(
                DirectCommandProcess.root,
                ".agent-workspace",
                "mtp",
                $"lifecycle-{Guid.NewGuid():N}.jsonl"
            )

        Directory.CreateDirectory(Path.GetDirectoryName capture) |> ignore

        let session =
            WorkspaceCommandScenario.startWithEnvironment
                "lifecycle-pipe"
                [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CAPTURE_PATH", capture ]
                (fun directory model ->
                    File.WriteAllText(Path.Combine(directory, "App.csproj"), "<Project />")
                    model.AddProject("App.csproj", "App", null) |> ignore)

        try
            let empty = WorkspaceCommandScenario.argumentMap []

            let restore =
                WorkspaceCommandScenario.executeRead
                    session
                    3u
                    "dotnet.restore"
                    session.ProjectId
                    empty
                    0L

            let build =
                WorkspaceCommandScenario.executeRead
                    session
                    4u
                    "dotnet.build"
                    None
                    (WorkspaceCommandScenario.argumentMap
                        [ "noRestore", RpcValue.Boolean true
                          "arguments",
                          RpcValue.array [ RpcValue.String "--verbosity"; RpcValue.String "quiet" ] ])
                    0L

            let workspaceTest =
                WorkspaceCommandScenario.executeRead
                    session
                    5u
                    "dotnet.test"
                    None
                    (WorkspaceCommandScenario.argumentMap
                        [ "arguments",
                          RpcValue.array
                              [ RpcValue.String "--no-restore"
                                RpcValue.String "--filter"
                                RpcValue.String "Category=Fast"
                                RpcValue.String "--logger"
                                RpcValue.String String.Empty ] ])
                    0L

            let projectTest =
                WorkspaceCommandScenario.executeRead
                    session
                    6u
                    "dotnet.test"
                    session.ProjectId
                    (WorkspaceCommandScenario.argumentMap
                        [ "arguments",
                          RpcValue.array
                              [ RpcValue.String "--logger"
                                RpcValue.String "console"
                                RpcValue.String "--logger"
                                RpcValue.String "console" ] ])
                    0L

            let run =
                WorkspaceCommandScenario.executeRead
                    session
                    7u
                    "dotnet.run"
                    session.ProjectId
                    empty
                    0L

            restore.Outcome |> should equal "succeeded"
            build.Outcome |> should equal "succeeded"
            workspaceTest.Outcome |> should equal "succeeded"
            projectTest.Outcome |> should equal "succeeded"
            run.Outcome |> should equal "succeeded"

            WorkspaceCommandScenario.captured capture
            |> should
                equal
                [| [| "restore"; Path.Combine(session.Directory, "App.csproj") |]
                   [| "build"; session.Solution; "--no-restore"; "--verbosity"; "quiet" |]
                   [| "test"
                      session.Solution
                      "--no-restore"
                      "--filter"
                      "Category=Fast"
                      "--logger"
                      String.Empty |]
                   [| "test"
                      Path.Combine(session.Directory, "App.csproj")
                      "--logger"
                      "console"
                      "--logger"
                      "console" |]
                   [| "run"; "--project"; Path.Combine(session.Directory, "App.csproj") |] |]
        finally
            WorkspaceCommandScenario.stop session

            if File.Exists capture then
                File.Delete capture
