namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type FilteredWorkspaceCommandTests() =
    [<Fact>]
    member _.``should bind incoming conditional references into a confirmed project rename``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-command"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let source = Path.Combine(directory, "One.fsproj")
            let destination = Path.Combine(directory, "Renamed.fsproj")
            let incoming = Path.Combine(directory, "Ref.fsproj")
            let model = SolutionModel()
            model.AddProject("One.fsproj", null, null) |> ignore
            model.AddProject("Ref.fsproj", null, null) |> ignore
            WorkspaceRpcScenario.writeProject source

            File.WriteAllText(
                incoming,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
                + "<ProjectReference Include=\"One.fsproj\" Condition=\"'$(Configuration)' == 'Never'\" />"
                + "</ItemGroup><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
                + "</PropertyGroup></Project>"
            )

            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            try
                let initialize =
                    WorkspaceRpcScenario.map
                        [ "protocolVersion",
                          WorkspaceRpcScenario.map
                              [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
                          "clientInfo",
                          WorkspaceRpcScenario.map [ "name", RpcValue.String "command-test" ]
                          "capabilities",
                          RpcValue.array
                              [ RpcValue.String "workspace.root"
                                RpcValue.String "workspace.children"
                                RpcValue.String "workspace.delta"
                                RpcValue.String "workspace.commands.list"
                                RpcValue.String "workspace.commands.preview"
                                RpcValue.String "workspace.commands.execute" ]
                          "limits",
                          WorkspaceRpcScenario.map
                              [ "maxFrameBytes", RpcValue.Integer 4194304L
                                "maxPageSize", RpcValue.Integer 50L ] ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" initialize)

                let initializeError, initializeResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

                Assert.True initializeError.IsNone

                let workspaceId =
                    WorkspaceRpcScenario.field "workspace" initializeResult
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                let workspaceTarget =
                    WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String workspaceId ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 30u "workspace/commands/list" workspaceTarget)

                let workspaceListError, workspaceList =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 30u

                Assert.True workspaceListError.IsNone

                WorkspaceRpcScenario.field "commands" workspaceList
                |> RpcValue.requireArray "commands"
                |> Seq.exists (fun command ->
                    WorkspaceRpcScenario.field "id" command = RpcValue.String
                        "solution.project.add")
                |> Assert.True

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let rootError, rootResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                Assert.True rootError.IsNone
                let rootChildren = WorkspaceRpcScenario.rootChildren child 20u rootResult

                let projectId =
                    WorkspaceRpcScenario.field "nodes" rootChildren
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        WorkspaceRpcScenario.field "kind" node = RpcValue.String "project"
                        && WorkspaceRpcScenario.field "name" node = RpcValue.String "One")
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                let children =
                    WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String projectId
                          "pageSize", RpcValue.Integer 50L ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 3u "workspace/children" children)

                let hydrationError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 3u

                Assert.True hydrationError.IsNone

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        0L,
                        WorkspaceRpcScenario.field "baseRevision" parameters
                        |> RpcValue.requireInteger "revision"
                    )

                    Assert.Equal(
                        1L,
                        WorkspaceRpcScenario.field "newRevision" parameters
                        |> RpcValue.requireInteger "revision"
                    )
                | frame -> failwithf "Expected the hydration delta, got %A" frame

                let target = WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String projectId ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 4u "workspace/commands/list" target)

                let listError, listResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 4u

                Assert.True listError.IsNone

                WorkspaceRpcScenario.field "commands" listResult
                |> RpcValue.requireArray "commands"
                |> Seq.exists (fun command ->
                    WorkspaceRpcScenario.field "id" command = RpcValue.String
                        "solution.project.rename")
                |> Assert.True

                let arguments = WorkspaceRpcScenario.map [ "name", RpcValue.String "Renamed" ]

                let invalidRevision =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetNodeId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer -1L ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 20u "workspace/commands/preview" invalidRevision)

                let revisionError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 20u

                Assert.Equal("invalid_params", revisionError.Value.Code)

                let malformedPreview =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetNodeId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 1L
                          "confirmationToken", RpcValue.String "bad" ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 21u "workspace/commands/execute" malformedPreview)

                let confirmationTokenError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 21u

                Assert.Equal("invalid_params", confirmationTokenError.Value.Code)

                let preview =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetNodeId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 1L ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 5u "workspace/commands/preview" preview)

                let previewError, previewResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 5u

                Assert.True previewError.IsNone

                let confirmationToken =
                    WorkspaceRpcScenario.field "confirmationToken" previewResult
                    |> RpcValue.requireString "confirmationToken"

                Assert.True(File.Exists source)

                File.ReadAllText incoming
                |> fun contents -> contents.Contains "One.fsproj"
                |> should equal true

                let execute =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "solution.project.rename"
                          "targetNodeId", RpcValue.String projectId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 1L
                          "confirmationToken", RpcValue.String confirmationToken ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 6u "workspace/commands/execute" execute)

                let executeError, executeResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 6u

                Assert.True executeError.IsNone

                Assert.Equal(
                    2L,
                    WorkspaceRpcScenario.field "revision" executeResult
                    |> RpcValue.requireInteger "revision"
                )

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        1L,
                        WorkspaceRpcScenario.field "baseRevision" parameters
                        |> RpcValue.requireInteger "baseRevision"
                    )

                    Assert.Equal(
                        2L,
                        WorkspaceRpcScenario.field "newRevision" parameters
                        |> RpcValue.requireInteger "newRevision"
                    )
                | frame ->
                    failwithf
                        "Expected the transaction delta after the execute response, got %A"
                        frame

                Assert.False(File.Exists source)
                Assert.True(File.Exists destination)

                File.ReadAllText incoming
                |> fun contents -> contents.Contains "Renamed.fsproj"
                |> should equal true

                File.ReadAllText incoming
                |> fun contents -> contents.Contains "Condition=\"'$(Configuration)' == 'Never'\""
                |> should equal true

                let reopened =
                    SolutionSerializers
                        .GetSerializerByMoniker(solution)
                        .OpenAsync(solution, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()

                reopened.SolutionProjects
                |> Seq.exists (fun project -> project.FilePath = "Renamed.fsproj")
                |> Assert.True

                reopened.SolutionProjects
                |> Seq.exists (fun project -> project.FilePath = "One.fsproj")
                |> Assert.False

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 7u "workspace/commands/execute" execute)

                let duplicateError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 7u

                Assert.Equal("not_found", duplicateError.Value.Code)
                WorkspaceRpcScenario.shutdown child 8u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should expose only read commands and refuse mutation requests for a solution filter``
        ()
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-command-slnf"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let filter = Path.Combine(directory, "Demo.slnf")
            WorkspaceRpcScenario.save solution (SolutionModel())
            File.WriteAllText(filter, """{ "solution": { "path": "Demo.slnx" } }""")
            let before = File.ReadAllBytes solution
            use child = WorkspaceRpcScenario.startWorkspaceRpc "solution" filter

            try
                let initialize =
                    WorkspaceRpcScenario.map
                        [ "protocolVersion",
                          WorkspaceRpcScenario.map
                              [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
                          "clientInfo", WorkspaceRpcScenario.map [ "name", RpcValue.String "test" ]
                          "capabilities",
                          RpcValue.array
                              [ RpcValue.String "workspace.root"
                                RpcValue.String "workspace.create.options"
                                RpcValue.String "workspace.commands.list"
                                RpcValue.String "workspace.commands.describe"
                                RpcValue.String "workspace.commands.preview"
                                RpcValue.String "workspace.commands.execute"
                                RpcValue.String "workspace.export.start"
                                RpcValue.String "workspace.refresh"
                                RpcValue.String "workspace.operations.cancel"
                                RpcValue.String "unknown.claim" ]
                          "limits",
                          WorkspaceRpcScenario.map
                              [ "maxFrameBytes", RpcValue.Integer 4194304L
                                "maxPageSize", RpcValue.Integer 50L ] ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" initialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> ignore

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 20u "workspace/root" RpcValue.emptyMap)

                let rootError, rootResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 20u

                Assert.True rootError.IsNone

                let rootId =
                    WorkspaceRpcScenario.field "nodes" rootResult
                    |> RpcValue.requireArray "nodes"
                    |> Seq.exactlyOne
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        21u
                        "workspace/create/options"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String rootId
                              "expectedRevision", RpcValue.Integer 0L ]))

                let optionsError, optionsResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 21u

                match optionsError with
                | Some error ->
                    failwithf "Read-only create options failed: %s: %s" error.Code error.Message
                | None -> ()

                let options =
                    WorkspaceRpcScenario.field "options" optionsResult
                    |> RpcValue.requireArray "options"

                Assert.NotEmpty options

                options
                |> Seq.forall (fun option ->
                    WorkspaceRpcScenario.field "kind" option = RpcValue.String "projectTemplate")
                |> Assert.True

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/commands/list" RpcValue.emptyMap)

                let listError, listResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                Assert.True listError.IsNone

                WorkspaceRpcScenario.field "commands" listResult
                |> RpcValue.requireArray "commands"
                |> Seq.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "id")
                |> Seq.toArray
                |> should
                    equal
                    [| "solution.launch-profile.list"
                       "dotnet.restore"
                       "dotnet.build"
                       "dotnet.test"
                       "template.list"
                       "template.describe" |]

                let describe =
                    WorkspaceRpcScenario.map [ "commandId", RpcValue.String "solution.folder.add" ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 3u "workspace/commands/describe" describe)

                let describeError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 3u

                Assert.Equal("not_found", describeError.Value.Code)

                let arguments = WorkspaceRpcScenario.map [ "name", RpcValue.String "src" ]

                let preview =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "solution.folder.add"
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 0L ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 4u "workspace/commands/preview" preview)

                let previewError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 4u

                Assert.Equal("unsupported_capability", previewError.Value.Code)

                let contextPreview =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String rootId
                          "arguments",
                          WorkspaceRpcScenario.map
                              [ "selectionId", RpcValue.String "unavailable"
                                "name", RpcValue.String "Generated" ]
                          "expectedRevision", RpcValue.Integer 0L ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 22u "workspace/commands/preview" contextPreview)

                let contextPreviewError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 22u

                Assert.Equal("unsupported_capability", contextPreviewError.Value.Code)

                let execute =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "solution.folder.add"
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer 0L
                          "confirmationToken", RpcValue.String(String('A', 64)) ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 5u "workspace/commands/execute" execute)

                let executeError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 5u

                Assert.Equal("unsupported_capability", executeError.Value.Code)
                Assert.Equal<byte>(before, File.ReadAllBytes solution)
                WorkspaceRpcScenario.shutdown child 6u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
