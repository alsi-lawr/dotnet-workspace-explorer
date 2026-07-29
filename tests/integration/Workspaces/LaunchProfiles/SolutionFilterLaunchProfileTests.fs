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
type SolutionFilterLaunchProfileTests() =
    [<Fact>]
    member _.``should list the backing launch profile for slnf and refuse its edits``() =
        let directory = DirectCommandProcess.temporaryDirectory "launch-profile-filter"

        try
            let backing, project, _ = LaunchProfileScenario.createSolution directory ".slnx"
            let profile = Path.ChangeExtension(backing, ".slnLaunch")
            let filter = Path.Combine(directory, "Filtered.slnf")
            File.WriteAllText(profile, "[{\"Name\":\"Start\",\"Projects\":[]}]")

            File.WriteAllText(
                filter,
                $"{{\"solution\":{{\"path\":\"{Path.GetFileName backing}\",\"projects\":[\"{Path.GetFileName project}\"]}}}}"
            )

            let listed =
                LaunchProfileScenario.run directory [ "solution"; filter; "launch"; "list" ]

            DirectCommandProcess.success listed |> should equal true
            LaunchProfileScenario.output listed |> should equal "Start\n"

            let edit =
                LaunchProfileScenario.run
                    directory
                    [ "solution"; filter; "launch"; "set"; "Start"; project ]

            DirectCommandProcess.success edit |> should equal false

            File.ReadAllText profile
            |> should equal "[{\"Name\":\"Start\",\"Projects\":[]}]"
        finally
            DirectCommandProcess.delete directory
