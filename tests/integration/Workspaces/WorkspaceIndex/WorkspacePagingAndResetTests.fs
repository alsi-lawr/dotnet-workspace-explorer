namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type WorkspacePagingAndResetTests() =
    [<Fact>]
    member _.``should page hydrated children watch an edit and rebase commands after reset``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-children-watch"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.fsproj")
            let model = SolutionModel()
            model.AddProject("Demo.fsproj", "Demo", null) |> ignore
            File.WriteAllText(Path.Combine(directory, "Initial.fs"), "module Initial")

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                + "</PropertyGroup><ItemGroup>"
                + "<Compile Include=\"Initial.fs\" />"
                + "</ItemGroup></Project>"
            )

            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            try
                let initialize =
                    WorkspaceRpcScenario.map
                        [ "protocolVersion",
                          WorkspaceRpcScenario.map
                              [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                          "clientInfo",
                          WorkspaceRpcScenario.map [ "name", RpcValue.String "watch-test" ]
                          "capabilities",
                          RpcValue.array
                              [ RpcValue.String "workspace.root"
                                RpcValue.String "workspace.children"
                                RpcValue.String "workspace.delta"
                                RpcValue.String "workspace.commands.list" ]
                          "limits",
                          WorkspaceRpcScenario.map
                              [ "maxFrameBytes", RpcValue.Integer 65536L
                                "maxPageSize", RpcValue.Integer 100L ] ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" initialize)

                let initializeError, initializeResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

                (initializeError.IsNone) |> should equal true

                let workspaceId =
                    WorkspaceRpcScenario.field "workspace" initializeResult
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let _, root =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                let workspaceRootId =
                    WorkspaceRpcScenario.field "nodes" root
                    |> RpcValue.requireArray "nodes"
                    |> Seq.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "id")
                    |> Seq.exactlyOne

                let rootChildren =
                    WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String workspaceRootId
                          "pageSize", RpcValue.Integer 100L ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 3u "workspace/children" rootChildren)

                let rootChildrenError, rootPage =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 3u

                (rootChildrenError.IsNone) |> should equal true

                let projectId =
                    WorkspaceRpcScenario.field "nodes" rootPage
                    |> RpcValue.requireArray "nodes"
                    |> Seq.filter (fun node ->
                        WorkspaceRpcScenario.field "kind" node = RpcValue.String "project")
                    |> Seq.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "id")
                    |> Seq.exactlyOne

                let children =
                    WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String projectId
                          "pageSize", RpcValue.Integer 1L ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 4u "workspace/children" children)

                let childError, page =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 4u

                (childError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "nodes" page |> RpcValue.requireArray "nodes")
                |> Seq.exactlyOne
                |> ignore

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", parameters) ->
                    (WorkspaceRpcScenario.field "baseRevision" parameters
                     |> RpcValue.requireInteger "revision")
                    |> should equal (0L)

                    (WorkspaceRpcScenario.field "newRevision" parameters
                     |> RpcValue.requireInteger "revision")
                    |> should equal (1L)
                | frame -> failwithf "Expected hydration delta, got %A" frame

                let token =
                    WorkspaceRpcScenario.field "nextToken" page
                    |> RpcValue.requireString "nextToken"

                let forged =
                    token[.. token.Length - 2]
                    + if token.EndsWith("A", StringComparison.Ordinal) then
                          "B"
                      else
                          "A"

                let invalidPage =
                    WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String projectId
                          "pageSize", RpcValue.Integer 1L
                          "continuationToken", RpcValue.String forged ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 5u "workspace/children" invalidPage)

                let tokenError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 5u

                (tokenError.Value.Code) |> should equal ("invalid_params")

                File.WriteAllText(Path.Combine(directory, "Changed.fs"), "module Changed")

                File.WriteAllText(
                    project,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                    + "<TargetFramework>net10.0</TargetFramework>"
                    + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                    + "</PropertyGroup><ItemGroup>"
                    + "<Compile Include=\"Initial.fs\" />"
                    + "<Compile Include=\"Changed.fs\" />"
                    + "</ItemGroup></Project>"
                )

                let watching = Task.Run(fun () -> WorkspaceRpcScenario.readFrame child)

                (watching.Wait(TimeSpan.FromSeconds 10.0)) |> should equal true

                let mutable watchedRevision = 1L

                match watching.Result with
                | Notification("workspace/delta", parameters) ->
                    (WorkspaceRpcScenario.field "baseRevision" parameters
                     |> RpcValue.requireInteger "revision")
                    |> should equal (1L)

                    watchedRevision <-
                        WorkspaceRpcScenario.field "newRevision" parameters
                        |> RpcValue.requireInteger "revision"

                    (watchedRevision > 1L) |> should equal true
                | frame -> failwithf "Expected watcher delta, got %A" frame

                let mutable continuation = None
                let mutable requestId = 6u
                let mutable hasMore = true
                let mutable changedFileFound = false

                while hasMore && not changedFileFound do
                    let freshChildren =
                        [ "parentNodeId", RpcValue.String projectId
                          "pageSize", RpcValue.Integer 100L ]
                        |> fun fields ->
                            continuation
                            |> Option.map (fun token ->
                                ("continuationToken", RpcValue.String token) :: fields)
                            |> Option.defaultValue fields
                        |> WorkspaceRpcScenario.map

                    WorkspaceRpcScenario.send
                        child
                        false
                        (WorkspaceRpcScenario.request requestId "workspace/children" freshChildren)

                    let projectError, projectPage =
                        WorkspaceRpcScenario.readFrame child
                        |> WorkspaceRpcScenario.response requestId

                    (projectError.IsNone) |> should equal true

                    (WorkspaceRpcScenario.field "revision" projectPage
                     |> RpcValue.requireInteger "revision")
                    |> should equal (watchedRevision)

                    changedFileFound <-
                        WorkspaceRpcScenario.field "nodes" projectPage
                        |> RpcValue.requireArray "nodes"
                        |> Seq.exists (fun node ->
                            WorkspaceRpcScenario.field "kind" node = RpcValue.String "projectFile"
                            && WorkspaceRpcScenario.field "name" node = RpcValue.String
                                "Changed.fs")

                    continuation <-
                        match RpcValue.tryField "nextToken" projectPage with
                        | Some(RpcValue.String token) -> Some token
                        | Some RpcValue.Nil
                        | None -> None
                        | Some value -> failwithf "Unexpected continuation token: %A" value

                    hasMore <- continuation.IsSome
                    requestId <- requestId + 1u

                (changedFileFound) |> should equal true

                File.Copy(WorkspaceRpcScenario.globalJson, Path.Combine(directory, "global.json"))
                let selection = Task.Run(fun () -> WorkspaceRpcScenario.readFrame child)

                (selection.Wait(TimeSpan.FromSeconds 10.0)) |> should equal true

                match selection.Result with
                | Notification("workspace/reset", parameters) ->
                    let resetRevision =
                        WorkspaceRpcScenario.field "revision" parameters
                        |> RpcValue.requireInteger "revision"

                    (resetRevision > watchedRevision) |> should equal true

                    WorkspaceRpcScenario.send
                        child
                        false
                        (WorkspaceRpcScenario.request 100u "workspace/root" RpcValue.emptyMap)

                    let freshError, freshRoot =
                        WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 100u

                    (freshError.IsNone) |> should equal true

                    (WorkspaceRpcScenario.field "revision" freshRoot
                     |> RpcValue.requireInteger "revision")
                    |> should equal (resetRevision)

                    let workspaceTarget =
                        WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String workspaceId ]

                    WorkspaceRpcScenario.send
                        child
                        false
                        (WorkspaceRpcScenario.request 101u "workspace/commands/list" workspaceTarget)

                    let commandError, commands =
                        WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 101u

                    (commandError.IsNone) |> should equal true

                    WorkspaceRpcScenario.field "commands" commands
                    |> RpcValue.requireArray "commands"
                    |> Seq.exists (fun command ->
                        WorkspaceRpcScenario.field "id" command = RpcValue.String
                            "solution.project.add")
                    |> should equal true
                | frame -> failwithf "Expected a toolset reset, got %A" frame

                WorkspaceRpcScenario.shutdown child 102u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
