namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open Xunit

[<Collection("Workspace scenarios")>]
type WorkspaceEndpointLimitTests() =
    [<Fact>]
    member _.``should reset the built executable when a child hydration delta exceeds its frame limit``
        ()
        =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "pipe-children-delta-pressure"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()

            for name in [ "A"; "B" ] do
                model.AddProject($"{name}.fsproj", name, null) |> ignore
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, $"{name}.fsproj"))

            model.AddBuildType "D"
            WorkspaceRpcScenario.save solution model

            let initialize maximumFrameBytes =
                WorkspaceRpcScenario.map
                    [ "protocolVersion",
                      WorkspaceRpcScenario.map
                          [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                      "clientInfo",
                      WorkspaceRpcScenario.map [ "name", RpcValue.String "child-pressure-test" ]
                      "capabilities",
                      RpcValue.array
                          [ RpcValue.String "workspace.root"
                            RpcValue.String "workspace.children"
                            RpcValue.String "workspace.delta" ]
                      "limits",
                      WorkspaceRpcScenario.map
                          [ "maxFrameBytes", RpcValue.Integer maximumFrameBytes
                            "maxPageSize", RpcValue.Integer 2L ] ]

            let projectIds root =
                WorkspaceRpcScenario.field "nodes" root
                |> RpcValue.requireArray "nodes"
                |> Seq.filter (fun node ->
                    WorkspaceRpcScenario.field "kind" node = RpcValue.String "project")
                |> Seq.sortBy (WorkspaceRpcScenario.field "name" >> RpcValue.requireString "name")
                |> Seq.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "id")
                |> Seq.toArray

            use probe = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            try
                WorkspaceRpcScenario.send
                    probe
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" (initialize 65536L))

                WorkspaceRpcScenario.readFrame probe
                |> WorkspaceRpcScenario.response 1u
                |> ignore

                WorkspaceRpcScenario.send
                    probe
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let probeRootError, probeRoot =
                    WorkspaceRpcScenario.readFrame probe |> WorkspaceRpcScenario.response 2u

                Assert.True probeRootError.IsNone
                let probeRootChildren = WorkspaceRpcScenario.rootChildren probe 20u probeRoot

                let probeProjectIds = projectIds probeRootChildren
                Assert.Equal(2, probeProjectIds.Length)

                for index in 0..1 do
                    WorkspaceRpcScenario.send
                        probe
                        false
                        (WorkspaceRpcScenario.request
                            (uint32 (3 + index))
                            "workspace/children"
                            (WorkspaceRpcScenario.map
                                [ "parentNodeId", RpcValue.String probeProjectIds[index]
                                  "pageSize", RpcValue.Integer 1L ]))

                    let probeChildrenError, _ =
                        WorkspaceRpcScenario.readFrame probe
                        |> WorkspaceRpcScenario.response (uint32 (3 + index))

                    Assert.True probeChildrenError.IsNone

                    match WorkspaceRpcScenario.readFrame probe with
                    | Notification("workspace/delta", _) as delta when index = 1 ->
                        let deltaSize = (MessagePackRpcCodec.encodeFrame delta).Length

                        Assert.True(
                            deltaSize > 1024,
                            $"Expected a delta above 1024 bytes, got {deltaSize}."
                        )
                    | Notification("workspace/delta", _) -> ()
                    | frame -> failwithf "Expected child-hydration delta, got %A" frame

                WorkspaceRpcScenario.shutdown probe 5u
            finally
                WorkspaceRpcScenario.disposeProcess probe

            use child = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 10u "initialize" (initialize 1024L))

                let initializeFrame, initializeSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(initializeSize <= 1024)
                WorkspaceRpcScenario.response 10u initializeFrame |> ignore

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 11u "workspace/root" RpcValue.emptyMap)

                let rootFrame, rootSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(rootSize <= 1024)
                let rootError, root = WorkspaceRpcScenario.response 11u rootFrame
                Assert.True(rootError.IsNone, $"Expected bounded root, got {rootError}.")

                Assert.Equal(
                    0L,
                    WorkspaceRpcScenario.field "revision" root |> RpcValue.requireInteger "revision"
                )

                let workspaceRootId =
                    WorkspaceRpcScenario.field "nodes" root
                    |> RpcValue.requireArray "nodes"
                    |> Seq.exactlyOne
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        20u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String workspaceRootId
                              "pageSize", RpcValue.Integer 2L ]))

                let rootChildrenFrame, rootChildrenSize =
                    WorkspaceRpcScenario.readFrameWithSize child

                Assert.True(rootChildrenSize <= 1024)

                let rootChildrenError, rootChildren =
                    WorkspaceRpcScenario.response 20u rootChildrenFrame

                Assert.True rootChildrenError.IsNone
                let childProjectIds = projectIds rootChildren
                Assert.Equal(2, childProjectIds.Length)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        12u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String childProjectIds[0]
                              "pageSize", RpcValue.Integer 1L ]))

                let firstFrame, firstSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(firstSize <= 1024)
                let firstError, firstPage = WorkspaceRpcScenario.response 12u firstFrame
                Assert.True firstError.IsNone

                Assert.Equal(
                    1L,
                    WorkspaceRpcScenario.field "revision" firstPage
                    |> RpcValue.requireInteger "revision"
                )

                let firstDelta, firstDeltaSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(firstDeltaSize <= 1024)

                match firstDelta with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        0L,
                        WorkspaceRpcScenario.field "baseRevision" parameters
                        |> RpcValue.requireInteger "baseRevision"
                    )

                    Assert.Equal(
                        1L,
                        WorkspaceRpcScenario.field "newRevision" parameters
                        |> RpcValue.requireInteger "newRevision"
                    )
                | frame -> failwithf "Expected in-limit child-hydration delta, got %A" frame

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        13u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String childProjectIds[1]
                              "pageSize", RpcValue.Integer 1L ]))

                let childrenFrame, childrenSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(childrenSize <= 1024)
                let childrenError, page = WorkspaceRpcScenario.response 13u childrenFrame
                Assert.True childrenError.IsNone

                Assert.Equal(
                    2L,
                    WorkspaceRpcScenario.field "revision" page |> RpcValue.requireInteger "revision"
                )

                let resetFrame, resetSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(resetSize <= 1024)

                match resetFrame with
                | Notification("workspace/reset", parameters) ->
                    Assert.Equal(
                        3L,
                        WorkspaceRpcScenario.field "revision" parameters
                        |> RpcValue.requireInteger "revision"
                    )

                    let diagnostic =
                        WorkspaceRpcScenario.field "diagnostics" parameters
                        |> RpcValue.requireArray "diagnostics"
                        |> Seq.exactlyOne

                    Assert.Equal(
                        RpcValue.String "workspace.delta_pressure",
                        WorkspaceRpcScenario.field "code" diagnostic
                    )
                | frame -> failwithf "Expected bounded child-hydration reset, got %A" frame

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 14u "workspace/root" RpcValue.emptyMap)

                let freshFrame, freshSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(freshSize <= 1024)
                let freshError, freshRoot = WorkspaceRpcScenario.response 14u freshFrame
                Assert.True freshError.IsNone

                Assert.Equal(
                    3L,
                    WorkspaceRpcScenario.field "revision" freshRoot
                    |> RpcValue.requireInteger "revision"
                )

                WorkspaceRpcScenario.shutdown child 15u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should apply negotiated frame limits to all outbound frames``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-global-limit"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()

            for index in 1..2 do
                model.AddProject($"Project{index}.fsproj", $"Project{index}", null) |> ignore

                WorkspaceRpcScenario.writeProject (
                    Path.Combine(directory, $"Project{index}.fsproj")
                )

            model.AddProject("Oversized.fsproj", "Oversized", null) |> ignore
            WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Oversized.fsproj"))
            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                let initializeFrame, initializeSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(initializeSize <= 1024)
                WorkspaceRpcScenario.response 1u initializeFrame |> ignore

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let rootFrame, rootSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(rootSize <= 1024)
                let rootError, root = WorkspaceRpcScenario.response 2u rootFrame
                Assert.True rootError.IsNone

                let workspaceRootId =
                    WorkspaceRpcScenario.field "nodes" root
                    |> RpcValue.requireArray "nodes"
                    |> Seq.exactlyOne
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        20u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String workspaceRootId
                              "pageSize", RpcValue.Integer 50L ]))

                let childrenFrame, childrenSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(childrenSize <= 1024)
                let childrenError, _ = WorkspaceRpcScenario.response 20u childrenFrame
                Assert.True childrenError.IsNone

                let unknownMethod = String('m', 3000)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 3u unknownMethod RpcValue.emptyMap)

                let errorFrame, errorSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(errorSize <= 1024)
                let methodError, _ = WorkspaceRpcScenario.response 3u errorFrame
                Assert.Equal("response_too_large", methodError.Value.Code)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 4u "workspace/export/start" RpcValue.emptyMap)

                let exportFrame, exportSize = WorkspaceRpcScenario.readFrameWithSize child
                Assert.True(exportSize <= 1024)
                let exportError, exportResult = WorkspaceRpcScenario.response 4u exportFrame
                Assert.True exportError.IsNone

                let operationId =
                    WorkspaceRpcScenario.field "operationId" exportResult
                    |> RpcValue.requireString "operationId"

                let mutable completed = false

                while not completed do
                    let frame, size = WorkspaceRpcScenario.readFrameWithSize child
                    Assert.True(size <= 1024)

                    match frame with
                    | Notification("workspace/operations/completed", parameters) ->
                        Assert.Equal(
                            RpcValue.String operationId,
                            WorkspaceRpcScenario.field "operationId" parameters
                        )

                        Assert.Equal(
                            RpcValue.String "succeeded",
                            WorkspaceRpcScenario.field "outcome" parameters
                        )

                        completed <- true
                    | Notification("workspace/export/chunk", _) -> ()
                    | value -> failwithf "Unexpected globally bounded frame: %A" value

                WorkspaceRpcScenario.shutdown child 5u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
