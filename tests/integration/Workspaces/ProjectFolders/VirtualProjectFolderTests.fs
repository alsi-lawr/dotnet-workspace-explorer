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
type VirtualProjectFolderTests() =
    [<Fact>]
    member _.``should link an external folder at a nested virtual path without creating directories``
        ()
        =
        let external = WorkspaceRpcScenario.temporaryDirectory "nested-virtual-link-source"
        File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

        let session =
            WorkspaceRpcScenario.openProject
                "nested-virtual-link"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.link"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "source", RpcValue.String external
                      "path", RpcValue.String "Virtual/Linked"
                      "itemType", RpcValue.String "Content" ])
                0L
                true

            let project = File.ReadAllText session.Project

            Assert.Contains(
                "<Link>Virtual/Linked/%(RecursiveDir)%(Filename)%(Extension)</Link>",
                project
            )

            Directory.Exists(Path.Combine(session.Directory, "Virtual"))
            |> should equal false
        finally
            WorkspaceRpcScenario.closeProject session
            Directory.Delete(external, true)
