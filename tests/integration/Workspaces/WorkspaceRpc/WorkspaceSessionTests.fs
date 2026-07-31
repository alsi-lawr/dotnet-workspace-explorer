namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type WorkspaceSessionTests() =
    [<Fact>]
    member _.``workspace RPC startup accepts only the reserved export-worker argument grammar``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "export-worker-cli"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            WorkspaceRpcScenario.save solution (SolutionModel())

            use valid =
                WorkspaceRpcScenario.startApphost
                    [ "workspace"; solution; "--pipe"; "--export-workers"; "1" ]
                    []

            WorkspaceRpcScenario.send
                valid
                false
                (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

            let initializeError, _ =
                WorkspaceRpcScenario.readFrame valid |> WorkspaceRpcScenario.response 1u

            (initializeError.IsNone) |> should equal true
            WorkspaceRpcScenario.shutdown valid 2u

            let invalidForms =
                [ [ "solution"; solution; "--pipe" ]
                  [ "sln"; solution; "--pipe" ]
                  [ "workspace"; solution; "--export-workers"; "1" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers"; "0" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers"; "-1" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers"; "+1" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers"; "1.0" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers"; "one" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers"; "" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers"; "2147483648" ]
                  [ "workspace"; solution; "--export-workers"; "1"; "--pipe" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers=1" ]
                  [ "workspace"; solution; "--pipe=true" ]
                  [ "workspace"
                    solution
                    "--pipe"
                    "--export-workers"
                    "1"
                    "--export-workers"
                    "2" ]
                  [ "workspace"; solution; "--pipe"; "--export-workers"; "1"; "extra" ]
                  [ "workspace"; solution; "--pipe"; "--pipe" ]
                  [ "--json"; "workspace"; solution; "--pipe" ] ]

            for arguments in invalidForms do
                use invalid = WorkspaceRpcScenario.startApphost arguments []
                invalid.StandardInput.Close()
                (invalid.WaitForExit 5000) |> should equal true
                (invalid.ExitCode) |> should equal (64)

                (WorkspaceRpcScenario.readRemaining invalid.StandardOutput.BaseStream)
                |> should be Empty

                (invalid.StandardError.ReadToEnd().Trim())
                |> should
                    equal
                    ("dotnet-workspace-explorer workspace RPC startup failure: invalid invocation.")
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``writable .sln and .slnx solutions each establish a framed workspace RPC session``
        (extension: string)
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-executable"

        try
            let solution = Path.Combine(directory, "Demo" + extension)
            let model = SolutionModel()
            model.AddProject("Demo.fsproj", "Demo", null) |> ignore
            WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Demo.fsproj"))
            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "workspace" solution

            try
                WorkspaceRpcScenario.send
                    child
                    true
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                let initializeError, initializeResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

                (initializeError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field
                    "minor"
                    (WorkspaceRpcScenario.field "protocolVersion" initializeResult)
                 |> RpcValue.requireInteger "minor")
                |> should equal (0L)

                ((WorkspaceRpcScenario.field "capabilities" initializeResult
                  |> RpcValue.requireArray "capabilities")
                    .Length)
                |> should equal (5)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let rootError, rootResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                (rootError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "revision" rootResult
                 |> RpcValue.requireInteger "revision")
                |> should equal (0L)

                let workspaceRootId =
                    WorkspaceRpcScenario.field "nodes" rootResult
                    |> RpcValue.requireArray "nodes"
                    |> Seq.exactlyOne
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 3u "workspace/export/start" RpcValue.emptyMap)

                let exportError, exportResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 3u

                (exportError.IsNone) |> should equal true

                let operationId =
                    WorkspaceRpcScenario.field "operationId" exportResult
                    |> RpcValue.requireString "operationId"

                let mutable sequence = 0L
                let mutable completed = false
                let mutable completions = 0

                while not completed do
                    let frame = WorkspaceRpcScenario.readFrame child
                    (MessagePackRpcCodec.encodeFrame frame |> _.Length <= 1024) |> should equal true

                    match frame with
                    | Notification("workspace/export/chunk", parameters) ->
                        (WorkspaceRpcScenario.field "operationId" parameters)
                        |> should equal (RpcValue.String operationId)

                        (WorkspaceRpcScenario.field "sequence" parameters
                         |> RpcValue.requireInteger "sequence")
                        |> should equal (sequence)

                        sequence <- sequence + 1L
                    | Notification("workspace/operations/completed", parameters) ->
                        (WorkspaceRpcScenario.field "operationId" parameters)
                        |> should equal (RpcValue.String operationId)

                        (WorkspaceRpcScenario.field "outcome" parameters)
                        |> should equal (RpcValue.String "succeeded")

                        completions <- completions + 1
                        completed <- true
                    | frame -> failwithf "Unexpected export frame: %A" frame

                (completions) |> should equal (1)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 4u "workspace/refresh" RpcValue.emptyMap)

                let noOpError, noOpResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 4u

                (noOpError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "revision" noOpResult
                 |> RpcValue.requireInteger "revision")
                |> should equal (0L)

                (WorkspaceRpcScenario.field "reset" noOpResult)
                |> should equal (RpcValue.Boolean false)

                let folder = model.AddFolder "/nested/"
                model.AddProject("Second.fsproj", "Second", folder) |> ignore
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Second.fsproj"))
                WorkspaceRpcScenario.save solution model

                let expected = WorkspaceRpcScenario.map [ "expectedRevision", RpcValue.Integer 0L ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 5u "workspace/refresh" expected)

                let (changedError, changedResult), observedRevision, observedNotifications =
                    WorkspaceRpcScenario.responseAfterWorkspaceNotifications child 5u 0L

                let finalRevision =
                    match changedError with
                    | None ->
                        let changedRevision =
                            WorkspaceRpcScenario.field "revision" changedResult
                            |> RpcValue.requireInteger "revision"

                        (changedRevision > observedRevision) |> should equal true

                        match WorkspaceRpcScenario.readFrame child with
                        | Notification("workspace/delta", parameters) ->
                            (WorkspaceRpcScenario.field "baseRevision" parameters
                             |> RpcValue.requireInteger "baseRevision")
                            |> should equal (changedRevision - 1L)

                            (WorkspaceRpcScenario.field "newRevision" parameters
                             |> RpcValue.requireInteger "newRevision")
                            |> should equal (changedRevision)

                            let added = HashSet<string> StringComparer.Ordinal
                            let mutable secondAdded = false

                            for change in
                                WorkspaceRpcScenario.field "changes" parameters
                                |> RpcValue.requireArray "changes" do
                                if
                                    WorkspaceRpcScenario.field "kind" change = RpcValue.String "add"
                                then
                                    match WorkspaceRpcScenario.field "parentNodeId" change with
                                    | RpcValue.String parentNodeId ->
                                        (parentNodeId = workspaceRootId
                                         || added.Contains parentNodeId)
                                        |> should equal true
                                    | RpcValue.Nil -> ()
                                    | value -> failwithf "Unexpected parent ID: %A" value

                                    WorkspaceRpcScenario.field "node" change
                                    |> WorkspaceRpcScenario.field "id"
                                    |> RpcValue.requireString "id"
                                    |> added.Add
                                    |> ignore

                                    let name =
                                        WorkspaceRpcScenario.field
                                            "name"
                                            (WorkspaceRpcScenario.field "node" change)

                                    if name = RpcValue.String "Second" then
                                        secondAdded <- true

                            (secondAdded) |> should equal true

                            changedRevision
                        | frame -> failwithf "Expected refresh delta, got %A" frame
                    | Some error ->
                        (error.Code) |> should equal ("workspace_conflict")
                        (observedRevision > 0L) |> should equal true

                        (observedNotifications)
                        |> Seq.exists (fun frame ->
                            match frame with
                            | Notification("workspace/delta", parameters) ->
                                WorkspaceRpcScenario.field "changes" parameters
                                |> RpcValue.requireArray "changes"
                                |> Seq.exists (fun change ->
                                    let node = WorkspaceRpcScenario.field "node" change

                                    WorkspaceRpcScenario.field "kind" change = RpcValue.String
                                        "add"
                                    && WorkspaceRpcScenario.field "name" node = RpcValue.String
                                        "Second")
                            | _ -> false)
                        |> should equal true

                        WorkspaceRpcScenario.send
                            child
                            false
                            (WorkspaceRpcScenario.request 9u "workspace/refresh" RpcValue.emptyMap)

                        let ((recoveredError, recoveredResult),
                             recoveredRevision,
                             recoveredNotifications) =
                            WorkspaceRpcScenario.responseAfterWorkspaceNotifications
                                child
                                9u
                                observedRevision

                        (recoveredError.IsNone) |> should equal true

                        (WorkspaceRpcScenario.field "reset" recoveredResult)
                        |> should equal (RpcValue.Boolean false)

                        (recoveredRevision >= observedRevision) |> should equal true
                        (recoveredNotifications) |> should be Empty
                        recoveredRevision

                (finalRevision > 0L) |> should equal true

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 6u "workspace/refresh" expected)

                let staleError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 6u

                (staleError.Value.Code) |> should equal ("workspace_conflict")

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 7u "project-evaluation/evaluate" RpcValue.emptyMap)

                let workerError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 7u

                (workerError.Value.Code) |> should equal ("unknown_method")
                WorkspaceRpcScenario.shutdown child 8u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
