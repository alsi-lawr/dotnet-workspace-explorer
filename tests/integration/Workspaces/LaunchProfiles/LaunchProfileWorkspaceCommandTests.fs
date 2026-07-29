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

[<Collection("Launch-profile scenarios")>]
type LaunchProfileWorkspaceCommandTests() =
    [<Fact>]
    member _.``should publish a workspace RPC launch-profile mutation after preview and verification``
        ()
        =
        let session =
            WorkspaceCommandScenario.start "launch-profile-pipe" (fun directory model ->
                File.WriteAllText(Path.Combine(directory, "App.csproj"), "<Project />")
                model.AddProject("App.csproj", "App", null) |> ignore)

        try
            let completion =
                WorkspaceCommandScenario.execute
                    session
                    3u
                    "solution.launch-profile.set"
                    None
                    (WorkspaceCommandScenario.argumentMap
                        [ "name", RpcValue.String "Start"
                          "projects",
                          RpcValue.array
                              [ RpcValue.String(Path.Combine(session.Directory, "App.csproj")) ] ])
                    0L

            completion.Outcome |> should equal "succeeded"

            completion.Notifications
            |> should equal [ "workspace/operations/progress"; "workspace/operations/completed" ]

            let profile = Path.ChangeExtension(session.Solution, ".slnLaunch")
            use document = JsonDocument.Parse(File.ReadAllText profile)
            let project = document.RootElement[0].GetProperty("Projects")[0]
            project.GetProperty("Path").GetString() |> should equal "App.csproj"

            project.GetProperty("Action").GetString()
            |> should equal "StartWithoutDebugging"

            let listed =
                WorkspaceCommandScenario.executeRead
                    session
                    5u
                    "solution.launch-profile.list"
                    None
                    (WorkspaceCommandScenario.argumentMap [])
                    completion.Revision

            listed.Output |> should equal [ "Start\n" ]

            listed.Notifications
            |> should
                equal
                [ "workspace/operations/progress"
                  "workspace/operations/output"
                  "workspace/operations/completed" ]

            let removed =
                WorkspaceCommandScenario.execute
                    session
                    7u
                    "solution.launch-profile.remove"
                    None
                    (WorkspaceCommandScenario.argumentMap [ "name", RpcValue.String "Start" ])
                    listed.Revision

            removed.Outcome |> should equal "succeeded"

            removed.Notifications
            |> should equal [ "workspace/operations/progress"; "workspace/operations/completed" ]

            File.ReadAllText(profile).Trim() |> should equal "[]"
        finally
            WorkspaceCommandScenario.stop session
