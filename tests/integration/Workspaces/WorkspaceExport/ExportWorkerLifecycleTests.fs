namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type ExportWorkerLifecycleTests() =
    [<Fact>]
    member _.``cancelling a bounded export releases admission and allows queued project evaluation to continue``
        ()
        =
        let runScenario capacity projectCount =
            let directory = WorkspaceRpcScenario.temporaryDirectory "export-scheduler"

            try
                let solution = Path.Combine(directory, "Demo.slnx")
                let model = SolutionModel()

                let projects =
                    [| for index in 1..projectCount do
                           let name = $"{char (int 'A' + index - 1)}"
                           let path = Path.Combine(directory, $"{name}.fsproj")
                           WorkspaceRpcScenario.writeProject path
                           model.AddProject(Path.GetFileName path, name, null) |> ignore
                           yield path |]

                WorkspaceRpcScenario.save solution model

                let workspace =
                    match SolutionWorkspaceReader.OpenAsync(solution).Result with
                    | Success value -> value
                    | Failure failure -> failwithf "Could not open scheduler fixture: %A" failure

                let gates =
                    projects
                    |> Seq.map (fun path ->
                        path,
                        TaskCompletionSource<unit>
                            TaskCreationOptions.RunContinuationsAsynchronously)
                    |> dict

                use started = new SemaphoreSlim 0
                use completed = new SemaphoreSlim 0
                let metricsGate = obj ()
                let mutable active = 0
                let mutable maximumActive = 0
                let mutable disposedSessions = 0
                let emitted = ResizeArray<string>()

                let snapshot path =
                    ProjectEvaluationSnapshot(
                        WorkspaceArtifactPath.Create path,
                        ImmutableArray<ProjectEvaluationDimension>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        WorkspaceCapabilityProfile.Full,
                        ImmutableArray<WorkspaceCapabilityId>.Empty,
                        ImmutableArray<WorkspaceDiagnostic>.Empty
                    )

                let services =
                    { OpenAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<SolutionWorkspace>>(Success workspace)
                      EvaluateAsync =
                        fun _ _ _ -> failwith "Interactive evaluation was not expected."
                      InvalidateAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<ProjectEvaluationInvalidationKind>>(
                                Success ProjectEvaluationInvalidationKind.None
                            )
                      OpenExportSessionAsync =
                        fun _ observedCapacity _ ->
                            (observedCapacity) |> should equal (capacity)

                            Task.FromResult<WorkspaceOutcome<WorkspaceIndexExportSession>>(
                                Success
                                    { EvaluateAsync =
                                        fun projectPath _ ->
                                            task {
                                                lock metricsGate (fun () ->
                                                    active <- active + 1
                                                    maximumActive <- max maximumActive active)

                                                started.Release() |> ignore
                                                do! gates[projectPath.Value].Task

                                                lock metricsGate (fun () -> active <- active - 1)
                                                completed.Release() |> ignore
                                                return Success(snapshot projectPath.Value)
                                            }
                                      DisposeAsync =
                                        fun () ->
                                            Interlocked.Increment(&disposedSessions) |> ignore
                                            Task.CompletedTask }
                            )
                      RefreshAsync = fun () -> Task.CompletedTask
                      DisposeAsync = fun () -> Task.CompletedTask }

                let state =
                    WorkspaceIndex.Create(
                        solution,
                        workspace,
                        services,
                        { HydrationLimit = 32
                          ExportCapacity = capacity
                          TokenSecret = Array.create 32 1uy }
                    )

                let writeBatch (batch: WorkspaceExportBatch) =
                    lock emitted (fun () ->
                        batch.Nodes
                        |> Seq.filter (fun node -> node.Kind = WorkspaceNodeKind.Project)
                        |> Seq.iter (fun node -> emitted.Add node.Name))

                    Task.FromResult()

                let export =
                    state.ExportAsync(
                        workspace.Descriptor.Revision.Value,
                        writeBatch,
                        CancellationToken.None
                    )

                let initiallyAdmitted = min capacity projectCount

                for _ in 1..initiallyAdmitted do
                    (started.Wait 5000) |> should equal true

                (maximumActive) |> should equal (initiallyAdmitted)

                if capacity = 2 && projectCount = 4 then
                    gates[projects[1]].SetResult()
                    (completed.Wait 5000) |> should equal true
                    (emitted) |> should be Empty
                    gates[projects[0]].SetResult()

                    for _ in 1..2 do
                        (started.Wait 5000) |> should equal true

                    gates[projects[3]].SetResult()
                    gates[projects[2]].SetResult()
                else
                    projects |> Array.rev |> Array.iter (fun path -> gates[path].SetResult())

                match export.GetAwaiter().GetResult() with
                | Ok() -> ()
                | Error error -> failwithf "Export scheduler failed: %s" error.Message

                emitted
                |> Seq.toArray
                |> should equal (projects |> Array.map Path.GetFileNameWithoutExtension)

                (disposedSessions) |> should equal (1)
                (maximumActive <= min capacity projectCount) |> should equal true
                state.DisposeAsync().GetAwaiter().GetResult()
            finally
                if Directory.Exists directory then
                    Directory.Delete(directory, true)

        runScenario 2 4
        runScenario Int32.MaxValue 2

        let runCancellationScenario () =
            let directory =
                WorkspaceRpcScenario.temporaryDirectory "export-scheduler-cancellation"

            try
                let solution = Path.Combine(directory, "Demo.slnx")
                let model = SolutionModel()

                let projects =
                    [| for name in [ "Alpha"; "Middle"; "Zulu" ] do
                           let path = Path.Combine(directory, $"{name}.fsproj")
                           WorkspaceRpcScenario.writeProject path
                           model.AddProject(Path.GetFileName path, name, null) |> ignore
                           yield path |]

                WorkspaceRpcScenario.save solution model

                let workspace =
                    match SolutionWorkspaceReader.OpenAsync(solution).Result with
                    | Success value -> value
                    | Failure failure -> failwithf "Could not open cancellation fixture: %A" failure

                let snapshot path =
                    ProjectEvaluationSnapshot(
                        WorkspaceArtifactPath.Create path,
                        ImmutableArray<ProjectEvaluationDimension>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        WorkspaceCapabilityProfile.Full,
                        ImmutableArray<WorkspaceCapabilityId>.Empty,
                        ImmutableArray<WorkspaceDiagnostic>.Empty
                    )

                let cancelled () =
                    Failure(
                        Cancelled(
                            WorkspaceOperationId.New(),
                            WorkspaceDiagnostic.CreateSimple(
                                WorkspaceDiagnosticSeverity.Error,
                                WorkspaceDiagnosticCode.Create "workspace.test_cancelled",
                                "The test export was cancelled.",
                                false,
                                CorrelationId.New()
                            )
                        )
                    )

                use started = new SemaphoreSlim 0
                let mutable openedSessions = 0
                let mutable settledEvaluations = 0
                let mutable disposedSessions = 0

                let services =
                    { OpenAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<SolutionWorkspace>>(Success workspace)
                      EvaluateAsync =
                        fun _ _ _ -> failwith "Interactive evaluation was not expected."
                      InvalidateAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<ProjectEvaluationInvalidationKind>>(
                                Success ProjectEvaluationInvalidationKind.None
                            )
                      OpenExportSessionAsync =
                        fun _ observedCapacity _ ->
                            (observedCapacity) |> should equal (3)
                            let sessionNumber = Interlocked.Increment(&openedSessions)

                            Task.FromResult<WorkspaceOutcome<WorkspaceIndexExportSession>>(
                                Success
                                    { EvaluateAsync =
                                        if sessionNumber = 1 then
                                            fun _ cancellationToken ->
                                                task {
                                                    let cancelledSignal =
                                                        TaskCompletionSource<unit>
                                                            TaskCreationOptions.RunContinuationsAsynchronously

                                                    use _registration =
                                                        cancellationToken.Register(fun () ->
                                                            cancelledSignal.TrySetResult()
                                                            |> ignore)

                                                    started.Release() |> ignore
                                                    do! cancelledSignal.Task

                                                    Interlocked.Increment(&settledEvaluations)
                                                    |> ignore

                                                    return cancelled ()
                                                }
                                        else
                                            fun projectPath _ ->
                                                Task.FromResult<
                                                    WorkspaceOutcome<ProjectEvaluationSnapshot>
                                                 >(
                                                    Success(snapshot projectPath.Value)
                                                )
                                      DisposeAsync =
                                        fun () ->
                                            if sessionNumber = 1 then
                                                (settledEvaluations)
                                                |> should equal (projects.Length)

                                            Interlocked.Increment(&disposedSessions) |> ignore
                                            Task.CompletedTask }
                            )
                      RefreshAsync = fun () -> Task.CompletedTask
                      DisposeAsync = fun () -> Task.CompletedTask }

                let state =
                    WorkspaceIndex.Create(
                        solution,
                        workspace,
                        services,
                        { HydrationLimit = 32
                          ExportCapacity = 3
                          TokenSecret = Array.create 32 1uy }
                    )

                use cancellation = new CancellationTokenSource()
                let firstLastValues = ResizeArray<bool>()

                let firstExport =
                    state.ExportAsync(
                        workspace.Descriptor.Revision.Value,
                        (fun batch ->
                            firstLastValues.Add batch.IsFinal
                            Task.FromResult()),
                        cancellation.Token
                    )

                for _ in projects do
                    (started.Wait 5000) |> should equal true

                (firstExport.IsCompleted) |> should equal false
                (settledEvaluations) |> should equal (0)
                cancellation.Cancel()

                match firstExport.GetAwaiter().GetResult() with
                | Error error -> (error.Code) |> should equal ("cancelled")
                | Ok() -> failwith "The blocked export unexpectedly succeeded."

                (settledEvaluations) |> should equal (3)
                (openedSessions) |> should equal (1)
                (disposedSessions) |> should equal (1)
                (firstLastValues) |> should not' (contain (true))

                let secondLastValues = ResizeArray<bool>()

                let secondExport =
                    state.ExportAsync(
                        workspace.Descriptor.Revision.Value,
                        (fun batch ->
                            secondLastValues.Add batch.IsFinal
                            Task.FromResult()),
                        CancellationToken.None
                    )

                match secondExport.GetAwaiter().GetResult() with
                | Ok() -> ()
                | Error error -> failwithf "The fresh export failed: %s" error.Message

                (openedSessions) |> should equal (2)
                (disposedSessions) |> should equal (2)

                (secondLastValues |> Seq.filter id |> Seq.toArray) |> should equal ([| true |])

                state.DisposeAsync().GetAwaiter().GetResult()
            finally
                if Directory.Exists directory then
                    Directory.Delete(directory, true)

        runCancellationScenario ()

    [<Fact>]
    member _.``cancelling a streaming export before its final chunk releases the export operation``
        ()
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-bounded-export-cancel"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.csproj")
            let model = SolutionModel()
            model.AddProject("Demo.csproj", "Demo", null) |> ignore

            let items =
                [ for index in 1..2500 ->
                      $"<Compile Include=\"generated/{String('y', 48)}-{index:D4}.cs\" />" ]
                |> String.concat String.Empty

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                + $"</PropertyGroup><ItemGroup>{items}</ItemGroup></Project>"
            )

            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> ignore

                let operationId, revision = WorkspaceRpcScenario.startExport child 2u

                let firstFrame, firstSize = WorkspaceRpcScenario.readFrameWithSize child
                (firstSize <= 1024) |> should equal true

                match firstFrame with
                | Notification("workspace/export/chunk", parameters) ->
                    (WorkspaceRpcScenario.field "last" parameters)
                    |> should equal (RpcValue.Boolean false)

                    (WorkspaceRpcScenario.field "sequence" parameters
                     |> RpcValue.requireInteger "sequence")
                    |> should equal (0L)
                | frame -> failwithf "Expected the first non-final export chunk, got %A" frame

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        3u
                        "workspace/operations/cancel"
                        (WorkspaceRpcScenario.map [ "operationId", RpcValue.String operationId ]))

                let mutable sequence = 1L
                let mutable cancelResult = None

                while cancelResult.IsNone do
                    match WorkspaceRpcScenario.readFrame child with
                    | Notification("workspace/export/chunk", parameters) ->
                        (WorkspaceRpcScenario.field "last" parameters)
                        |> should equal (RpcValue.Boolean false)

                        (WorkspaceRpcScenario.field "sequence" parameters
                         |> RpcValue.requireInteger "sequence")
                        |> should equal (sequence)

                        sequence <- sequence + 1L
                    | Response(3u, error, result) ->
                        (error.IsNone) |> should equal true
                        cancelResult <- Some result
                    | frame -> failwithf "Unexpected frame before cancellation response: %A" frame

                (WorkspaceRpcScenario.field "accepted" cancelResult.Value)
                |> should equal (RpcValue.Boolean true)

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/operations/completed", parameters) ->
                    (WorkspaceRpcScenario.field "operationId" parameters)
                    |> should equal (RpcValue.String operationId)

                    (WorkspaceRpcScenario.field "outcome" parameters)
                    |> should equal (RpcValue.String "cancelled")

                    (WorkspaceRpcScenario.field "revision" parameters
                     |> RpcValue.requireInteger "revision")
                    |> should equal (revision)

                    (WorkspaceRpcScenario.field "sequence" parameters
                     |> RpcValue.requireInteger "sequence")
                    |> should equal (sequence)
                | frame -> failwithf "Expected one cancelled completion, got %A" frame

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        4u
                        "workspace/operations/cancel"
                        (WorkspaceRpcScenario.map [ "operationId", RpcValue.String operationId ]))

                let retryError, retryResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 4u

                (retryError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "accepted" retryResult)
                |> should equal (RpcValue.Boolean false)

                WorkspaceRpcScenario.shutdown child 5u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
