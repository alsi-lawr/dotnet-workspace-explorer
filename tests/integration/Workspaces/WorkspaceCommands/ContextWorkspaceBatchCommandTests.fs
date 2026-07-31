namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Microsoft.VisualStudio.SolutionPersistence.Model
open Xunit

module private ContextWorkspaceBatchScenario =
    let nodeId kind response =
        response
        |> WorkspaceRpcScenario.field "nodes"
        |> RpcValue.requireArray "nodes"
        |> Seq.find (fun node -> WorkspaceRpcScenario.field "kind" node = RpcValue.String kind)
        |> WorkspaceRpcScenario.field "id"
        |> RpcValue.requireString "id"

    let nodeIdNamed name response =
        response
        |> WorkspaceRpcScenario.field "nodes"
        |> RpcValue.requireArray "nodes"
        |> Seq.find (fun node -> WorkspaceRpcScenario.field "name" node = RpcValue.String name)
        |> WorkspaceRpcScenario.field "id"
        |> RpcValue.requireString "id"

    let commandRequest commandId targetNodeId arguments revision =
        WorkspaceRpcScenario.map
            [ "commandId", RpcValue.String commandId
              "targetNodeId", RpcValue.String targetNodeId
              "arguments", arguments
              "expectedRevision", RpcValue.Integer revision ]

    let largeInitialize =
        WorkspaceRpcScenario.map
            [ "protocolVersion",
              WorkspaceRpcScenario.map
                  [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
              "clientInfo", WorkspaceRpcScenario.map [ "name", RpcValue.String "batch-test" ]
              "capabilities", RpcValue.array []
              "limits",
              WorkspaceRpcScenario.map
                  [ "maxFrameBytes", RpcValue.Integer 65536L
                    "maxPageSize", RpcValue.Integer 100L ] ]

    let withProjectedFile action =
        let directory = WorkspaceRpcScenario.temporaryDirectory "context-batch"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.csproj")
            let source = Path.Combine(directory, "Old.cs")
            let model = SolutionModel()
            model.AddProject(Path.GetFileName project, "Demo", null) |> ignore

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework>"
                + "</PropertyGroup><ItemGroup><Compile Update=\"Old.cs\">"
                + "<Visible>true</Visible></Compile></ItemGroup></Project>"
            )

            File.WriteAllText(source, "class Old {}")
            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "context-batch" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let _, root =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                let rootId = nodeId "workspace" root

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        3u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String rootId
                              "pageSize", RpcValue.Integer 100L ]))

                let _, rootChildren =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 3u

                let projectId = nodeId "project" rootChildren

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        4u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String projectId
                              "pageSize", RpcValue.Integer 100L ]))

                let (childrenError, projectChildren), _, _ =
                    WorkspaceRpcScenario.responseAfterWorkspaceNotifications child 4u 0L

                childrenError |> should equal None

                let revision =
                    WorkspaceRpcScenario.field "revision" projectChildren
                    |> RpcValue.requireInteger "revision"

                let sourceId = nodeId "projectFile" projectChildren

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _) -> ()
                | frame -> failwithf "Expected hydration delta, got %A" frame

                action child revision sourceId source project
                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            Directory.Delete(directory, true)

[<Collection("Workspace scenarios")>]
type ContextWorkspaceBatchCommandTests() =
    [<Fact>]
    member _.``a file rename uses the exact generic preview and execute envelope``() =
        ContextWorkspaceBatchScenario.withProjectedFile
            (fun child revision sourceId source project ->
                let arguments = WorkspaceRpcScenario.map [ "name", RpcValue.String "Renamed.cs" ]

                let preview =
                    ContextWorkspaceBatchScenario.commandRequest
                        "workspace.rename"
                        sourceId
                        arguments
                        revision

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 10u "workspace/commands/preview" preview)

                let previewError, previewResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 10u

                previewError |> should equal None

                let previewFields = RpcValue.requireMap "preview" previewResult

                previewFields.Keys
                |> Seq.sort
                |> Seq.toList
                |> should equal [ "confirmationToken"; "effects"; "expiresAtUtc"; "summary" ]

                previewFields["effects"]
                |> RpcValue.requireArray "effects"
                |> Seq.map (WorkspaceRpcScenario.field "operation")
                |> Seq.toList
                |> should equal [ RpcValue.String "rename"; RpcValue.String "create" ]

                let token =
                    previewFields["confirmationToken"]
                    |> RpcValue.requireString "confirmationToken"

                let executeFields =
                    preview
                    |> RpcValue.requireMap "preview"
                    |> Seq.map (fun pair -> pair.Key, pair.Value)
                    |> Seq.append [ "confirmationToken", RpcValue.String token ]
                    |> WorkspaceRpcScenario.map

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 11u "workspace/commands/execute" executeFields)

                let executeError, executeResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 11u

                executeError |> should equal None

                WorkspaceRpcScenario.field "applied" executeResult
                |> should equal (RpcValue.Boolean true)

                File.Exists source |> should equal false
                let destination = Path.Combine(Path.GetDirectoryName source, "Renamed.cs")
                File.Exists destination |> should equal true
                File.ReadAllText(project).Contains("Renamed.cs") |> should equal true)

    [<Fact>]
    member _.``a physical copy composes every selected file into one project edit``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "context-copy-batch"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.csproj")
            let destinationDirectory = Path.Combine(directory, "Destination")
            let model = SolutionModel()
            model.AddProject(Path.GetFileName project, "Demo", null) |> ignore

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                + "</PropertyGroup><ItemGroup><Compile Include=\"First.cs\" />"
                + "<Compile Include=\"Second.cs\" />"
                + "<Compile Include=\"Destination/Keep.cs\" /></ItemGroup></Project>"
            )

            File.WriteAllText(Path.Combine(directory, "First.cs"), "class First {}")
            File.WriteAllText(Path.Combine(directory, "Second.cs"), "class Second {}")
            Directory.CreateDirectory destinationDirectory |> ignore
            File.WriteAllText(Path.Combine(destinationDirectory, "Keep.cs"), "class Keep {}")
            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "context-copy-batch" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        1u
                        "initialize"
                        ContextWorkspaceBatchScenario.largeInitialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let _, root =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                let projectId =
                    WorkspaceRpcScenario.rootChildren child 3u root
                    |> ContextWorkspaceBatchScenario.nodeIdNamed "Demo"

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        4u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String projectId
                              "pageSize", RpcValue.Integer 100L ]))

                let (childrenError, projectChildren), _, _ =
                    WorkspaceRpcScenario.responseAfterWorkspaceNotifications child 4u 0L

                childrenError |> should equal None

                let revision =
                    WorkspaceRpcScenario.field "revision" projectChildren
                    |> RpcValue.requireInteger "revision"

                let firstId = ContextWorkspaceBatchScenario.nodeIdNamed "First.cs" projectChildren

                let secondId = ContextWorkspaceBatchScenario.nodeIdNamed "Second.cs" projectChildren

                let destinationId =
                    ContextWorkspaceBatchScenario.nodeIdNamed "Destination" projectChildren

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _) -> ()
                | frame -> failwithf "Expected hydration delta, got %A" frame

                let arguments =
                    WorkspaceRpcScenario.map
                        [ "sourceNodeIds",
                          RpcValue.array [ RpcValue.String firstId; RpcValue.String secondId ] ]

                let preview =
                    ContextWorkspaceBatchScenario.commandRequest
                        "workspace.copy"
                        destinationId
                        arguments
                        revision

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 10u "workspace/commands/preview" preview)

                let previewError, previewResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 10u

                previewError |> should equal None

                let previewFields = RpcValue.requireMap "preview" previewResult

                previewFields["effects"]
                |> RpcValue.requireArray "effects"
                |> Seq.length
                |> should equal 4

                let token =
                    previewFields["confirmationToken"] |> RpcValue.requireString "confirmationToken"

                let execute =
                    preview
                    |> RpcValue.requireMap "preview"
                    |> Seq.map (fun pair -> pair.Key, pair.Value)
                    |> Seq.append [ "confirmationToken", RpcValue.String token ]
                    |> WorkspaceRpcScenario.map

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 11u "workspace/commands/execute" execute)

                let executeError, executeResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 11u

                executeError |> should equal None

                WorkspaceRpcScenario.field "applied" executeResult
                |> should equal (RpcValue.Boolean true)

                File.ReadAllText(Path.Combine(destinationDirectory, "First.cs"))
                |> should equal "class First {}"

                File.ReadAllText(Path.Combine(destinationDirectory, "Second.cs"))
                |> should equal "class Second {}"

                let projectContents = File.ReadAllText project
                projectContents.Contains("Destination/First.cs") |> should equal true
                projectContents.Contains("Destination/Second.cs") |> should equal true
                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``a cross-project move composes source and destination memberships atomically``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "context-cross-project-move"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let sourceDirectory = Path.Combine(directory, "Source")
            let destinationDirectory = Path.Combine(directory, "Destination")
            let sourceProject = Path.Combine(sourceDirectory, "Source.csproj")
            let destinationProject = Path.Combine(destinationDirectory, "Destination.csproj")
            let sourceFile = Path.Combine(sourceDirectory, "MoveMe.cs")
            let destinationFile = Path.Combine(destinationDirectory, "MoveMe.cs")
            Directory.CreateDirectory sourceDirectory |> ignore
            Directory.CreateDirectory destinationDirectory |> ignore
            let model = SolutionModel()
            model.AddProject("Source/Source.csproj", "Source", null) |> ignore

            model.AddProject("Destination/Destination.csproj", "Destination", null)
            |> ignore

            File.WriteAllText(
                sourceProject,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                + "</PropertyGroup><ItemGroup><Compile Include=\"MoveMe.cs\" />"
                + "</ItemGroup></Project>"
            )

            File.WriteAllText(
                destinationProject,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                + "</PropertyGroup></Project>"
            )

            File.WriteAllText(sourceFile, "class MoveMe {}")
            WorkspaceRpcScenario.save solution model

            use child =
                WorkspaceRpcScenario.startWorkspaceRpc "context-cross-project-move" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        1u
                        "initialize"
                        ContextWorkspaceBatchScenario.largeInitialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let _, root =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                let rootChildren = WorkspaceRpcScenario.rootChildren child 3u root

                let sourceProjectId =
                    ContextWorkspaceBatchScenario.nodeIdNamed "Source" rootChildren

                let destinationProjectId =
                    ContextWorkspaceBatchScenario.nodeIdNamed "Destination" rootChildren

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        4u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String sourceProjectId
                              "pageSize", RpcValue.Integer 100L ]))

                let (sourceError, sourceChildren), _, _ =
                    WorkspaceRpcScenario.responseAfterWorkspaceNotifications child 4u 0L

                sourceError |> should equal None

                let sourceId = ContextWorkspaceBatchScenario.nodeIdNamed "MoveMe.cs" sourceChildren

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _) -> ()
                | frame -> failwithf "Expected source hydration delta, got %A" frame

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        5u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String destinationProjectId
                              "pageSize", RpcValue.Integer 100L ]))

                let (destinationError, destinationChildren), _, _ =
                    WorkspaceRpcScenario.responseAfterWorkspaceNotifications child 5u 1L

                destinationError |> should equal None

                let revision =
                    WorkspaceRpcScenario.field "revision" destinationChildren
                    |> RpcValue.requireInteger "revision"

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _) -> ()
                | frame -> failwithf "Expected destination hydration delta, got %A" frame

                let arguments =
                    WorkspaceRpcScenario.map
                        [ "sourceNodeIds", RpcValue.array [ RpcValue.String sourceId ] ]

                let preview =
                    ContextWorkspaceBatchScenario.commandRequest
                        "workspace.move"
                        destinationProjectId
                        arguments
                        revision

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 10u "workspace/commands/preview" preview)

                let previewError, previewResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 10u

                previewError |> should equal None

                let token =
                    previewResult
                    |> WorkspaceRpcScenario.field "confirmationToken"
                    |> RpcValue.requireString "confirmationToken"

                let execute =
                    preview
                    |> RpcValue.requireMap "preview"
                    |> Seq.map (fun pair -> pair.Key, pair.Value)
                    |> Seq.append [ "confirmationToken", RpcValue.String token ]
                    |> WorkspaceRpcScenario.map

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 11u "workspace/commands/execute" execute)

                let executeError, executeResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 11u

                executeError |> should equal None

                WorkspaceRpcScenario.field "applied" executeResult
                |> should equal (RpcValue.Boolean true)

                File.Exists sourceFile |> should equal false
                File.ReadAllText(destinationFile) |> should equal "class MoveMe {}"
                File.ReadAllText(sourceProject).Contains("MoveMe.cs") |> should equal false
                File.ReadAllText(destinationProject).Contains("MoveMe.cs") |> should equal true
                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``a project rename updates both its physical path and solution membership``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "context-project-rename"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.csproj")
            let destination = Path.Combine(directory, "Renamed.csproj")
            let model = SolutionModel()
            model.AddProject(Path.GetFileName project, "Demo", null) |> ignore
            WorkspaceRpcScenario.writeProject project
            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "context-project-rename" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let _, root =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                let rootChildren = WorkspaceRpcScenario.rootChildren child 3u root

                let revision =
                    WorkspaceRpcScenario.field "revision" rootChildren
                    |> RpcValue.requireInteger "revision"

                let projectId = ContextWorkspaceBatchScenario.nodeIdNamed "Demo" rootChildren

                let preview =
                    ContextWorkspaceBatchScenario.commandRequest
                        "workspace.rename"
                        projectId
                        (WorkspaceRpcScenario.map [ "name", RpcValue.String "Renamed" ])
                        revision

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 10u "workspace/commands/preview" preview)

                let previewError, previewResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 10u

                previewError |> should equal None

                let token =
                    previewResult
                    |> WorkspaceRpcScenario.field "confirmationToken"
                    |> RpcValue.requireString "confirmationToken"

                let execute =
                    preview
                    |> RpcValue.requireMap "preview"
                    |> Seq.map (fun pair -> pair.Key, pair.Value)
                    |> Seq.append [ "confirmationToken", RpcValue.String token ]
                    |> WorkspaceRpcScenario.map

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 11u "workspace/commands/execute" execute)

                let executeError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 11u

                executeError |> should equal None
                File.Exists project |> should equal false
                File.Exists destination |> should equal true
                File.ReadAllText(solution).Contains("Renamed.csproj") |> should equal true
                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            Directory.Delete(directory, true)
