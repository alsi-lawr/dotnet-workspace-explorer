namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
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

                (probeRootError.IsNone) |> should equal true
                let probeRootChildren = WorkspaceRpcScenario.rootChildren probe 20u probeRoot

                let probeProjectIds = projectIds probeRootChildren
                (probeProjectIds.Length) |> should equal (2)

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

                    (probeChildrenError.IsNone) |> should equal true

                    match WorkspaceRpcScenario.readFrame probe with
                    | Notification("workspace/delta", _) as delta when index = 1 ->
                        let deltaSize = (MessagePackRpcCodec.encodeFrame delta).Length

                        (deltaSize > 1024) |> should equal true
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
                (initializeSize <= 1024) |> should equal true
                WorkspaceRpcScenario.response 10u initializeFrame |> ignore

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 11u "workspace/root" RpcValue.emptyMap)

                let rootFrame, rootSize = WorkspaceRpcScenario.readFrameWithSize child
                (rootSize <= 1024) |> should equal true
                let rootError, root = WorkspaceRpcScenario.response 11u rootFrame
                (rootError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "revision" root |> RpcValue.requireInteger "revision")
                |> should equal (0L)

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

                (rootChildrenSize <= 1024) |> should equal true

                let rootChildrenError, rootChildren =
                    WorkspaceRpcScenario.response 20u rootChildrenFrame

                (rootChildrenError.IsNone) |> should equal true
                let childProjectIds = projectIds rootChildren
                (childProjectIds.Length) |> should equal (2)

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
                (firstSize <= 1024) |> should equal true
                let firstError, firstPage = WorkspaceRpcScenario.response 12u firstFrame
                (firstError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "revision" firstPage
                 |> RpcValue.requireInteger "revision")
                |> should equal (1L)

                let firstDelta, firstDeltaSize = WorkspaceRpcScenario.readFrameWithSize child
                (firstDeltaSize <= 1024) |> should equal true

                match firstDelta with
                | Notification("workspace/delta", parameters) ->
                    (WorkspaceRpcScenario.field "baseRevision" parameters
                     |> RpcValue.requireInteger "baseRevision")
                    |> should equal (0L)

                    (WorkspaceRpcScenario.field "newRevision" parameters
                     |> RpcValue.requireInteger "newRevision")
                    |> should equal (1L)
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
                (childrenSize <= 1024) |> should equal true
                let childrenError, page = WorkspaceRpcScenario.response 13u childrenFrame
                (childrenError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "revision" page |> RpcValue.requireInteger "revision")
                |> should equal (2L)

                let resetFrame, resetSize = WorkspaceRpcScenario.readFrameWithSize child
                (resetSize <= 1024) |> should equal true

                match resetFrame with
                | Notification("workspace/reset", parameters) ->
                    (WorkspaceRpcScenario.field "revision" parameters
                     |> RpcValue.requireInteger "revision")
                    |> should equal (3L)

                    let diagnostic =
                        WorkspaceRpcScenario.field "diagnostics" parameters
                        |> RpcValue.requireArray "diagnostics"
                        |> Seq.exactlyOne

                    (WorkspaceRpcScenario.field "code" diagnostic)
                    |> should equal (RpcValue.String "workspace.delta_pressure")
                | frame -> failwithf "Expected bounded child-hydration reset, got %A" frame

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 14u "workspace/root" RpcValue.emptyMap)

                let freshFrame, freshSize = WorkspaceRpcScenario.readFrameWithSize child
                (freshSize <= 1024) |> should equal true
                let freshError, freshRoot = WorkspaceRpcScenario.response 14u freshFrame
                (freshError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "revision" freshRoot
                 |> RpcValue.requireInteger "revision")
                |> should equal (3L)

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
                (initializeSize <= 1024) |> should equal true
                WorkspaceRpcScenario.response 1u initializeFrame |> ignore

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let rootFrame, rootSize = WorkspaceRpcScenario.readFrameWithSize child
                (rootSize <= 1024) |> should equal true
                let rootError, root = WorkspaceRpcScenario.response 2u rootFrame
                (rootError.IsNone) |> should equal true

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
                (childrenSize <= 1024) |> should equal true
                let childrenError, _ = WorkspaceRpcScenario.response 20u childrenFrame
                (childrenError.IsNone) |> should equal true

                let unknownMethod = String('m', 3000)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 3u unknownMethod RpcValue.emptyMap)

                let errorFrame, errorSize = WorkspaceRpcScenario.readFrameWithSize child
                (errorSize <= 1024) |> should equal true
                let methodError, _ = WorkspaceRpcScenario.response 3u errorFrame
                (methodError.Value.Code) |> should equal ("response_too_large")

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 4u "workspace/export/start" RpcValue.emptyMap)

                let exportFrame, exportSize = WorkspaceRpcScenario.readFrameWithSize child
                (exportSize <= 1024) |> should equal true
                let exportError, exportResult = WorkspaceRpcScenario.response 4u exportFrame
                (exportError.IsNone) |> should equal true

                let operationId =
                    WorkspaceRpcScenario.field "operationId" exportResult
                    |> RpcValue.requireString "operationId"

                let mutable completed = false

                while not completed do
                    let frame, size = WorkspaceRpcScenario.readFrameWithSize child
                    (size <= 1024) |> should equal true

                    match frame with
                    | Notification("workspace/operations/completed", parameters) ->
                        (WorkspaceRpcScenario.field "operationId" parameters)
                        |> should equal (RpcValue.String operationId)

                        (WorkspaceRpcScenario.field "outcome" parameters)
                        |> should equal (RpcValue.String "succeeded")

                        completed <- true
                    | Notification("workspace/export/chunk", _) -> ()
                    | value -> failwithf "Unexpected globally bounded frame: %A" value

                WorkspaceRpcScenario.shutdown child 5u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
