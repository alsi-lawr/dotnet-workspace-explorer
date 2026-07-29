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

[<Collection("Workspace scenarios")>]
type ProjectPropertyCommandTests() =
    [<Fact>]
    member _.``should reject unsupported conditional property mutation``() =
        let session =
            WorkspaceRpcScenario.openProject
                "conditional-property-scenario"
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                 + "<Version Condition=\"'$(MSBuildProjectName)' == 'Demo'\">1.0</Version>"
                 + "</PropertyGroup></Project>")

        try
            WorkspaceRpcScenario.previewFailure
                session
                3u
                "project.property.set"
                (WorkspaceRpcScenario.map
                    [ "name", RpcValue.String "Version"; "value", RpcValue.String "2.0" ])
                0L
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should refuse project mutations for unknown project systems``() =
        let session =
            WorkspaceRpcScenario.openProject
                "unknown-project-system-scenario"
                "<Project><PropertyGroup><Value>readable</Value></PropertyGroup></Project>"

        try
            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request
                    3u
                    "workspace/commands/preview"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "project.property.set"
                          "targetNodeId", RpcValue.String session.ProjectId
                          "arguments",
                          WorkspaceRpcScenario.map
                              [ "name", RpcValue.String "RootNamespace"
                                "value", RpcValue.String "Demo.Root" ]
                          "expectedRevision", RpcValue.Integer 0L ]))

            let error, _ =
                WorkspaceRpcScenario.readFrame session.Child |> WorkspaceRpcScenario.response 3u

            Assert.Equal("unsupported_capability", error.Value.Code)
        finally
            WorkspaceRpcScenario.closeProject session
