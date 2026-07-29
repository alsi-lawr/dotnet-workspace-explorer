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
type ProjectFolderSafetyTests() =
    [<Fact>]
    member _.``should refuse project folder copy collisions and generated destinations``() =
        let external = WorkspaceRpcScenario.temporaryDirectory "folder-copy-refusal-source"
        File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

        let session =
            WorkspaceRpcScenario.openProject
                "folder-copy-refusal-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            let collision = Path.Combine(session.Directory, "Collision")
            Directory.CreateDirectory collision |> ignore

            WorkspaceRpcScenario.previewFailure
                session
                3u
                "project.folder.copy"
                (WorkspaceRpcScenario.map
                    [ "source", RpcValue.String external; "path", RpcValue.String collision ])
                0L

            WorkspaceRpcScenario.previewFailure
                session
                5u
                "project.folder.new"
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String(Path.Combine(session.Directory, ".generated")) ])
                0L

            File.Exists(Path.Combine(external, "Source.txt")) |> should equal true
            Directory.Exists collision |> should equal true
        finally
            WorkspaceRpcScenario.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should refuse terminal and intermediate symbolic folder operands``() =
        if not (OperatingSystem.IsWindows()) then
            let external = WorkspaceRpcScenario.temporaryDirectory "folder-symbolic-target"
            File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

            let session =
                WorkspaceRpcScenario.openProject
                    "folder-symbolic-scenario"
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

            try
                let terminal = Path.Combine(session.Directory, "Terminal")
                let intermediate = Path.Combine(session.Directory, "Intermediate")
                Directory.CreateSymbolicLink(terminal, external) |> ignore
                Directory.CreateSymbolicLink(intermediate, external) |> ignore

                WorkspaceRpcScenario.previewFailure
                    session
                    3u
                    "project.folder.remove"
                    (WorkspaceRpcScenario.map [ "path", RpcValue.String terminal ])
                    0L

                WorkspaceRpcScenario.previewFailure
                    session
                    5u
                    "project.folder.new"
                    (WorkspaceRpcScenario.map
                        [ "path", RpcValue.String(Path.Combine(intermediate, "Child")) ])
                    0L
            finally
                WorkspaceRpcScenario.closeProject session
                Directory.Delete(external, true)
