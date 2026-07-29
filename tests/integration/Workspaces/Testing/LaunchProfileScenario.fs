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

module private LaunchProfileScenario =
    let run directory arguments =
        DirectCommandProcess.run directory "capture" ("--json" :: arguments) []

    let createSolution directory extension =
        let first = Path.Combine(directory, "First.fsproj")
        let second = Path.Combine(directory, "Second.fsproj")
        let solution = Path.Combine(directory, $"Demo{extension}")
        File.WriteAllText(first, "<Project />")
        File.WriteAllText(second, "<Project />")
        DirectCommandProcess.saveSolution solution [ first; second ]
        solution, first, second

    let output result =
        use document = DirectCommandProcess.json result
        document.RootElement.GetProperty("result").GetProperty("standardOutput").GetString()
