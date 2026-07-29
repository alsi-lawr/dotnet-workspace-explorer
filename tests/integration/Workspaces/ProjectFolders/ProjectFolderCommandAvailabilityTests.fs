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

[<Collection("Project-folder scenarios")>]
type ProjectFolderCommandAvailabilityTests() =
    [<Fact>]
    member _.``should advertise folder commands only for writable project targets``() =
        let writable =
            WorkspaceRpcScenario.openProject
                "folder-command-discovery-full"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        let unknown =
            WorkspaceRpcScenario.openProject "folder-command-discovery-unknown" "<Project />"

        let commands (session: WorkspaceRpcScenario.ProjectSession) requestId =
            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request
                    requestId
                    "workspace/commands/list"
                    (WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String session.ProjectId ]))

            let error, result =
                WorkspaceRpcScenario.readFrame session.Child
                |> WorkspaceRpcScenario.response requestId

            Assert.True error.IsNone

            WorkspaceRpcScenario.field "commands" result
            |> RpcValue.requireArray "commands"
            |> Seq.map (WorkspaceRpcScenario.field "id")
            |> Seq.toArray

        try
            WorkspaceRpcScenario.readAllProjectChildNames writable 3u 0L |> ignore

            commands writable 5u
            |> Array.contains (RpcValue.String "project.folder.new")
            |> should equal true

            commands unknown 3u
            |> Array.contains (RpcValue.String "project.folder.new")
            |> should equal false
        finally
            WorkspaceRpcScenario.closeProject writable
            WorkspaceRpcScenario.closeProject unknown
