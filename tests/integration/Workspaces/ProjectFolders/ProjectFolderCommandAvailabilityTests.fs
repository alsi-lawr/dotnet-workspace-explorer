namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Rpc
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
            |> Seq.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "command id")
            |> Seq.toArray

        try
            WorkspaceRpcScenario.readAllProjectChildNames writable 3u 0L |> ignore

            let writableCommands = commands writable 5u

            writableCommands
            |> Array.filter (fun command -> command.StartsWith "project.")
            |> should
                equal
                [| "project.item.add"
                   "project.item.new"
                   "project.item.copy"
                   "project.item.rename"
                   "project.item.move"
                   "project.item.remove"
                   "project.item.delete"
                   "project.item.set-build-action"
                   "project.item.set-metadata"
                   "project.relocate"
                   "project.property.set"
                   "project.folder.new"
                   "project.folder.copy"
                   "project.folder.link"
                   "project.folder.rename"
                   "project.folder.move"
                   "project.folder.remove"
                   "project.folder.delete" |]

            let describe requestId commandId =
                WorkspaceRpcScenario.send
                    writable.Child
                    false
                    (WorkspaceRpcScenario.request
                        requestId
                        "workspace/commands/describe"
                        (WorkspaceRpcScenario.map
                            [ "commandId", RpcValue.String commandId
                              "targetNodeId", RpcValue.String writable.ProjectId ]))

                let error, result =
                    WorkspaceRpcScenario.readFrame writable.Child
                    |> WorkspaceRpcScenario.response requestId

                Assert.True error.IsNone

                let command = WorkspaceRpcScenario.field "command" result

                WorkspaceRpcScenario.field "id" command
                |> RpcValue.requireString "command id"
                |> should equal commandId

                WorkspaceRpcScenario.field "parameters" command
                |> RpcValue.requireArray "parameters"
                |> Seq.map (
                    WorkspaceRpcScenario.field "id" >> RpcValue.requireString "parameter id"
                )
                |> Seq.toArray

            describe 7u "project.relocate" |> should equal [| "destination"; "folder" |]

            describe 9u "project.property.set"
            |> should equal [| "name"; "value"; "scope"; "condition" |]

            commands unknown 3u |> Array.contains "project.folder.new" |> should equal false
        finally
            WorkspaceRpcScenario.closeProject writable
            WorkspaceRpcScenario.closeProject unknown
