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
type WorkspacePagingAndResetTests() =
    [<Fact>]
    member _.``should page hydrated children watch an edit and rebase commands after reset``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-children-watch"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.fsproj")
            let model = SolutionModel()
            model.AddProject("Demo.fsproj", "Demo", null) |> ignore
            WorkspaceRpcScenario.writeProject project
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

                Assert.True initializeError.IsNone

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

                let projectId =
                    WorkspaceRpcScenario.field "nodes" root
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
                    (WorkspaceRpcScenario.request 3u "workspace/children" children)

                let childError, page =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 3u

                Assert.True childError.IsNone

                Assert.Single(
                    WorkspaceRpcScenario.field "nodes" page |> RpcValue.requireArray "nodes"
                )
                |> ignore

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
                    (WorkspaceRpcScenario.request 4u "workspace/children" invalidPage)

                let tokenError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 4u

                Assert.Equal("invalid_params", tokenError.Value.Code)

                File.WriteAllText(
                    project,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                    + "<TargetFramework>net10.0</TargetFramework>"
                    + "<WatchedValue>changed</WatchedValue>"
                    + "</PropertyGroup></Project>"
                )

                let watching = Task.Run(fun () -> WorkspaceRpcScenario.readFrame child)

                Assert.True(
                    watching.Wait(TimeSpan.FromSeconds 10.0),
                    "The watcher did not publish a transition."
                )

                let mutable watchedRevision = 1L

                match watching.Result with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        1L,
                        WorkspaceRpcScenario.field "baseRevision" parameters
                        |> RpcValue.requireInteger "revision"
                    )

                    watchedRevision <-
                        WorkspaceRpcScenario.field "newRevision" parameters
                        |> RpcValue.requireInteger "revision"

                    Assert.True(watchedRevision > 1L)
                | frame -> failwithf "Expected watcher delta, got %A" frame

                let mutable continuation = None
                let mutable requestId = 5u
                let mutable hasMore = true
                let mutable watchedValueFound = false

                while hasMore && not watchedValueFound do
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

                    Assert.True projectError.IsNone

                    Assert.Equal(
                        watchedRevision,
                        WorkspaceRpcScenario.field "revision" projectPage
                        |> RpcValue.requireInteger "revision"
                    )

                    watchedValueFound <-
                        WorkspaceRpcScenario.field "nodes" projectPage
                        |> RpcValue.requireArray "nodes"
                        |> Seq.exists (fun node ->
                            WorkspaceRpcScenario.field "kind" node = RpcValue.String "projectItem"
                            && WorkspaceRpcScenario.field "name" node = RpcValue.String
                                "Evaluated WatchedValue = changed")

                    continuation <-
                        match WorkspaceRpcScenario.field "nextToken" projectPage with
                        | RpcValue.String token -> Some token
                        | RpcValue.Nil -> None
                        | value -> failwithf "Unexpected continuation token: %A" value

                    hasMore <- continuation.IsSome
                    requestId <- requestId + 1u

                Assert.True(
                    watchedValueFound,
                    "Fresh project paging did not expose Evaluated WatchedValue = changed."
                )

                File.Copy(WorkspaceRpcScenario.globalJson, Path.Combine(directory, "global.json"))
                let selection = Task.Run(fun () -> WorkspaceRpcScenario.readFrame child)

                Assert.True(
                    selection.Wait(TimeSpan.FromSeconds 10.0),
                    "global.json creation was not observed."
                )

                match selection.Result with
                | Notification("workspace/reset", parameters) ->
                    let resetRevision =
                        WorkspaceRpcScenario.field "revision" parameters
                        |> RpcValue.requireInteger "revision"

                    Assert.True(resetRevision > watchedRevision)

                    WorkspaceRpcScenario.send
                        child
                        false
                        (WorkspaceRpcScenario.request 100u "workspace/root" RpcValue.emptyMap)

                    let freshError, freshRoot =
                        WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 100u

                    Assert.True freshError.IsNone

                    Assert.Equal(
                        resetRevision,
                        WorkspaceRpcScenario.field "revision" freshRoot
                        |> RpcValue.requireInteger "revision"
                    )

                    let workspaceTarget =
                        WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String workspaceId ]

                    WorkspaceRpcScenario.send
                        child
                        false
                        (WorkspaceRpcScenario.request 101u "workspace/commands/list" workspaceTarget)

                    let commandError, commands =
                        WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 101u

                    Assert.True commandError.IsNone

                    WorkspaceRpcScenario.field "commands" commands
                    |> RpcValue.requireArray "commands"
                    |> Seq.exists (fun command ->
                        WorkspaceRpcScenario.field "id" command = RpcValue.String
                            "solution.project.add")
                    |> Assert.True
                | frame -> failwithf "Expected a toolset reset, got %A" frame

                WorkspaceRpcScenario.shutdown child 102u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
