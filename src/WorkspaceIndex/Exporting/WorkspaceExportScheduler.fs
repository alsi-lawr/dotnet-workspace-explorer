namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Workspaces

type private WorkspaceExportAdmission =
    { Cancellation: CancellationTokenSource
      Completion: Task<Result<EvaluatedWorkspaceProject option, RpcError>> }

module internal WorkspaceExportScheduler =
    let run
        (workspace: SolutionWorkspace)
        insensitive
        exportCapacity
        pathKey
        (openSession: CancellationToken -> Task<Result<WorkspaceIndexExportSession, RpcError>>)
        (evaluateProject:
            WorkspaceIndexExportSession
                -> SolutionProject
                -> CancellationToken
                -> Task<Result<EvaluatedWorkspaceProject, RpcError>>)
        (cancelledError: RpcError)
        (writeBatch: WorkspaceExportBatch -> Task<unit>)
        (cancellationToken: CancellationToken)
        =
        task {
            let projects =
                workspace.Contents.Projects
                |> Seq.sortBy (fun project -> pathKey project.Path.AbsolutePath.Value)
                |> Seq.toArray

            let staticBatchSize = 256
            let staticBatch = ResizeArray<WorkspaceNode> staticBatchSize

            for node in WorkspaceIndexPure.exportStaticNodes workspace do
                cancellationToken.ThrowIfCancellationRequested()

                if staticBatch.Count = staticBatchSize then
                    do!
                        writeBatch
                            { Nodes = staticBatch.ToArray()
                              IsFinal = false }

                    staticBatch.Clear()

                staticBatch.Add node

            if staticBatch.Count > 0 then
                do!
                    writeBatch
                        { Nodes = staticBatch.ToArray()
                          IsFinal = projects.Length = 0 }

            if staticBatch.Count = 0 && projects.Length = 0 then
                do! writeBatch { Nodes = Array.empty; IsFinal = true }

            let firstEvaluable =
                projects
                |> Array.tryFindIndex (fun project ->
                    not project.IsFilteredOut && File.Exists project.Path.AbsolutePath.Value)

            let firstMissing =
                projects
                |> Array.tryFindIndex (fun project ->
                    not project.IsFilteredOut && not (File.Exists project.Path.AbsolutePath.Value))

            let needsSession =
                match firstEvaluable, firstMissing with
                | Some evaluated, Some missing -> evaluated < missing
                | Some _, None -> true
                | None, _ -> false

            let! openedSession =
                task {
                    if needsSession then
                        let! opened = openSession cancellationToken
                        return opened |> Result.map Some
                    else
                        return Ok None
                }

            match openedSession with
            | Error error -> return Error error
            | Ok session ->
                let admissions = Dictionary<int, WorkspaceExportAdmission>()
                let admissionGate = obj ()
                let mutable earliestFailure = Int32.MaxValue
                let mutable nextAdmission = 0
                let mutable nextEmission = 0

                let recordFailure ordinal =
                    let later =
                        lock admissionGate (fun () ->
                            if ordinal < earliestFailure then
                                earliestFailure <- ordinal

                                admissions
                                |> Seq.choose (fun (KeyValue(index, admission)) ->
                                    if index > ordinal then
                                        Some admission.Cancellation
                                    else
                                        None)
                                |> Seq.toArray
                            else
                                Array.empty)

                    for cancellation in later do
                        cancellation.Cancel()

                let cancelAll () =
                    let cancellations =
                        lock admissionGate (fun () ->
                            admissions.Values |> Seq.map _.Cancellation |> Seq.toArray)

                    for cancellation in cancellations do
                        cancellation.Cancel()

                let admit ordinal =
                    let project = projects[ordinal]

                    let projectCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                    let completion =
                        if project.IsFilteredOut then
                            Task.FromResult(Ok None)
                        elif not (File.Exists project.Path.AbsolutePath.Value) then
                            let message =
                                $"Project '{project.Path.AbsolutePath.Value}' was not found."

                            Task.FromResult(Error(RpcErrors.create "not_found" message None))
                        else
                            task {
                                let! evaluated =
                                    evaluateProject session.Value project projectCancellation.Token

                                return evaluated |> Result.map Some
                            }

                    let admission =
                        { Cancellation = projectCancellation
                          Completion = completion }

                    lock admissionGate (fun () -> admissions.Add(ordinal, admission))

                    completion.ContinueWith(
                        (fun (completed: Task<Result<EvaluatedWorkspaceProject option, RpcError>>) ->
                            match completed.Result with
                            | Error _ -> recordFailure ordinal
                            | Ok _ -> ()),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    )
                    |> ignore

                let fillWindow () =
                    let admissionOpen () =
                        lock admissionGate (fun () -> earliestFailure = Int32.MaxValue)

                    let canAdmit () =
                        nextAdmission < projects.Length
                        && admissions.Count < exportCapacity
                        && admissionOpen ()
                        && not cancellationToken.IsCancellationRequested

                    while canAdmit () do
                        admit nextAdmission
                        nextAdmission <- nextAdmission + 1

                let runScheduler =
                    task {
                        fillWindow ()
                        let mutable result = Ok()

                        while nextEmission < projects.Length && result.IsOk do
                            if cancellationToken.IsCancellationRequested then
                                result <- Error cancelledError
                            else
                                let admission = admissions[nextEmission]
                                let! completed = admission.Completion

                                match completed with
                                | Error error -> result <- Error error
                                | Ok snapshot ->
                                    cancellationToken.ThrowIfCancellationRequested()

                                    do!
                                        writeBatch
                                            { Nodes =
                                                WorkspaceIndexPure.exportProjectNodes
                                                    insensitive
                                                    workspace
                                                    projects[nextEmission]
                                                    snapshot
                                              IsFinal = nextEmission = projects.Length - 1 }

                                    lock admissionGate (fun () ->
                                        admissions.Remove nextEmission |> ignore)

                                    admission.Cancellation.Dispose()
                                    nextEmission <- nextEmission + 1
                                    fillWindow ()

                        return result
                    }

                let! schedulerAttempt =
                    task {
                        try
                            let! result = runScheduler
                            return Choice1Of2 result
                        with exceptionValue ->
                            return Choice2Of2 exceptionValue
                    }

                cancelAll ()

                let pending =
                    lock admissionGate (fun () ->
                        admissions.Values |> Seq.map _.Completion |> Seq.toArray)

                let! settlementException =
                    task {
                        try
                            if pending.Length > 0 then
                                do! Task.WhenAll pending :> Task

                            return None
                        with exceptionValue ->
                            return Some exceptionValue
                    }

                for admission in admissions.Values do
                    admission.Cancellation.Dispose()

                admissions.Clear()

                let! disposalException =
                    task {
                        try
                            match session with
                            | Some active -> do! active.DisposeAsync()
                            | None -> ()

                            return None
                        with exceptionValue ->
                            return Some exceptionValue
                    }

                match schedulerAttempt, settlementException, disposalException with
                | Choice1Of2 result, None, None -> return result
                | Choice2Of2(:? OperationCanceledException), _, _ -> return Error cancelledError
                | Choice2Of2 exceptionValue, _, _ -> return raise exceptionValue
                | _, Some exceptionValue, _ -> return raise exceptionValue
                | _, _, Some exceptionValue -> return raise exceptionValue
        }
