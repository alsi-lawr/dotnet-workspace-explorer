namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open Xunit

[<Collection("Workspace scenarios")>]
type WorkspaceSessionTests() =
    [<Fact>]
    member _.``should accept only the reserved export worker startup grammar``() =
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

            Assert.True initializeError.IsNone
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
                Assert.True(invalid.WaitForExit 5000, "Invalid startup did not terminate.")
                Assert.Equal(64, invalid.ExitCode)
                Assert.Empty(WorkspaceRpcScenario.readRemaining invalid.StandardOutput.BaseStream)

                Assert.Equal(
                    "dotnet-workspace-explorer workspace RPC startup failure: invalid invocation.",
                    invalid.StandardError.ReadToEnd().Trim()
                )
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``should serve a framed workspace session for both writable solution formats``
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

                Assert.True initializeError.IsNone

                Assert.Equal(
                    0L,
                    WorkspaceRpcScenario.field
                        "minor"
                        (WorkspaceRpcScenario.field "protocolVersion" initializeResult)
                    |> RpcValue.requireInteger "minor"
                )

                Assert.Equal(
                    4,
                    (WorkspaceRpcScenario.field "capabilities" initializeResult
                     |> RpcValue.requireArray "capabilities")
                        .Length
                )

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let rootError, rootResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                Assert.True rootError.IsNone

                Assert.Equal(
                    0L,
                    WorkspaceRpcScenario.field "revision" rootResult
                    |> RpcValue.requireInteger "revision"
                )

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

                Assert.True exportError.IsNone

                let operationId =
                    WorkspaceRpcScenario.field "operationId" exportResult
                    |> RpcValue.requireString "operationId"

                let mutable sequence = 0L
                let mutable completed = false
                let mutable completions = 0

                while not completed do
                    let frame = WorkspaceRpcScenario.readFrame child
                    Assert.True(MessagePackRpcCodec.encodeFrame frame |> _.Length <= 1024)

                    match frame with
                    | Notification("workspace/export/chunk", parameters) ->
                        Assert.Equal(
                            RpcValue.String operationId,
                            WorkspaceRpcScenario.field "operationId" parameters
                        )

                        Assert.Equal(
                            sequence,
                            WorkspaceRpcScenario.field "sequence" parameters
                            |> RpcValue.requireInteger "sequence"
                        )

                        sequence <- sequence + 1L
                    | Notification("workspace/operations/completed", parameters) ->
                        Assert.Equal(
                            RpcValue.String operationId,
                            WorkspaceRpcScenario.field "operationId" parameters
                        )

                        Assert.Equal(
                            RpcValue.String "succeeded",
                            WorkspaceRpcScenario.field "outcome" parameters
                        )

                        completions <- completions + 1
                        completed <- true
                    | frame -> failwithf "Unexpected export frame: %A" frame

                Assert.Equal(1, completions)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 4u "workspace/refresh" RpcValue.emptyMap)

                let noOpError, noOpResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 4u

                Assert.True noOpError.IsNone

                Assert.Equal(
                    0L,
                    WorkspaceRpcScenario.field "revision" noOpResult
                    |> RpcValue.requireInteger "revision"
                )

                Assert.Equal(RpcValue.Boolean false, WorkspaceRpcScenario.field "reset" noOpResult)

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

                        Assert.True(changedRevision > observedRevision)

                        match WorkspaceRpcScenario.readFrame child with
                        | Notification("workspace/delta", parameters) ->
                            Assert.Equal(
                                changedRevision - 1L,
                                WorkspaceRpcScenario.field "baseRevision" parameters
                                |> RpcValue.requireInteger "baseRevision"
                            )

                            Assert.Equal(
                                changedRevision,
                                WorkspaceRpcScenario.field "newRevision" parameters
                                |> RpcValue.requireInteger "newRevision"
                            )

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
                                        Assert.True(
                                            parentNodeId = workspaceRootId
                                            || added.Contains parentNodeId
                                        )
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

                            Assert.True(
                                secondAdded,
                                "The refreshed delta did not add the Second project."
                            )

                            changedRevision
                        | frame -> failwithf "Expected refresh delta, got %A" frame
                    | Some error ->
                        Assert.Equal("workspace_conflict", error.Code)
                        Assert.True(observedRevision > 0L)

                        Assert.Contains(
                            observedNotifications,
                            fun frame ->
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
                                | _ -> false
                        )

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

                        Assert.True recoveredError.IsNone

                        Assert.Equal(
                            RpcValue.Boolean false,
                            WorkspaceRpcScenario.field "reset" recoveredResult
                        )

                        Assert.True(recoveredRevision >= observedRevision)
                        Assert.Empty recoveredNotifications
                        recoveredRevision

                Assert.True(finalRevision > 0L)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 6u "workspace/refresh" expected)

                let staleError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 6u

                Assert.Equal("workspace_conflict", staleError.Value.Code)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 7u "project-evaluation/evaluate" RpcValue.emptyMap)

                let workerError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 7u

                Assert.Equal("unknown_method", workerError.Value.Code)
                WorkspaceRpcScenario.shutdown child 8u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
