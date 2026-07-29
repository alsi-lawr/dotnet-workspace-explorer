namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceCommands

#nowarn "3511"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open WorkspaceCommandEditing

type internal WorkspaceEditOperation =
    static member Start
        (
            context: DotnetCommandOperationContext,
            plan: PlannedWorkspaceCommand,
            confirmationToken: string,
            progress: string,
            verify: unit -> Result<unit, string>,
            requestCancellationToken: CancellationToken
        ) : Task<Result<RpcRequestResult, RpcError>> =
        task {
            let operationId = Guid.NewGuid().ToString "N"
            let operation = WorkspaceExportOperation requestCancellationToken

            if not (context.ActiveOperations.TryAdd(operationId, operation)) then
                operation.Complete()
                return Error RpcErrors.internalError
            else
                let background (sink: RpcNotificationSink) sessionToken =
                    task {
                        let mutable sequence = 0
                        let mutable publicationHeld = false
                        let mutable completionReserved = false
                        let mutable outcome = WorkspaceOperationCompletion.Succeeded

                        let nextSequence () = Interlocked.Increment(&sequence) - 1

                        let operationFailure failure =
                            match failure with
                            | Cancelled _ -> WorkspaceOperationCompletion.Cancelled
                            | _ ->
                                WorkspaceOperationCompletion.Failed(
                                    failure.Code.Value,
                                    failure.Diagnostic.Message
                                )

                        let complete () =
                            task {
                                let! completedOutcome =
                                    WorkspaceOperations.completedOutcome
                                        operation
                                        completionReserved
                                        outcome

                                do!
                                    sink.WriteAsync(
                                        WorkspaceRpcNotifications.operationCompleted
                                            context.State.Descriptor
                                            operationId
                                            (nextSequence ())
                                            context.State.Revision
                                            completedOutcome
                                    )
                            }

                        try
                            use linked =
                                CancellationTokenSource.CreateLinkedTokenSource(
                                    operation.Token,
                                    sessionToken
                                )

                            do! context.PublicationGate.WaitAsync linked.Token
                            publicationHeld <- true

                            do!
                                sink.WriteAsync(
                                    WorkspaceRpcNotifications.operationProgress
                                        context.State.Descriptor
                                        operationId
                                        (nextSequence ())
                                        context.State.Revision
                                        progress
                                )

                            let execution =
                                context.Coordinator.ExecuteOperation(
                                    plannedRequest plan,
                                    plannedActions plan,
                                    WorkspaceEditConfirmation.Create confirmationToken,
                                    linked.Token,
                                    fun () ->
                                        let reserved = operation.TryReserveCompletion()

                                        if reserved then
                                            completionReserved <- true

                                        reserved
                                )

                            match execution with
                            | Failure failure -> outcome <- operationFailure failure
                            | Success(RolledBack failure) -> outcome <- operationFailure failure
                            | Success Applied ->
                                match verify () with
                                | Error message ->
                                    outcome <-
                                        WorkspaceOperationCompletion.Failed(
                                            "invalid_input",
                                            message
                                        )
                                | Ok() ->
                                    let! invalidated =
                                        context.State.InvalidateFromTransactionAsync(
                                            plannedPaths plan,
                                            CancellationToken.None
                                        )

                                    let notifications = context.MutationNotifications invalidated

                                    let reset =
                                        notifications
                                        |> List.exists (fun notification ->
                                            MessagePackRpcCodec.encodeFrame notification
                                            |> fun frame ->
                                                frame.Length > context.MaximumFrameBytes())

                                    let! effectiveInvalidation =
                                        if reset then
                                            task {
                                                let! value =
                                                    WorkspaceNavigationRequests.resetForFramePressure
                                                        context.State
                                                        CancellationToken.None

                                                return
                                                    WorkspaceProjectInvalidationResult.Reset value
                                            }
                                        else
                                            Task.FromResult invalidated

                                    let requiresWatcherReset =
                                        match effectiveInvalidation with
                                        | WorkspaceProjectInvalidationResult.Reset _ -> true
                                        | _ -> false

                                    if requiresWatcherReset then
                                        context.Watcher.Pause()

                                    let! watcherNotifications =
                                        if requiresWatcherReset then
                                            Task.FromResult []
                                        else
                                            context.RebuildWatcher CancellationToken.None

                                    for notification in
                                        context.MutationNotifications effectiveInvalidation
                                        @ watcherNotifications do
                                        do! sink.WriteAsync notification

                        with
                        | :? OperationCanceledException ->
                            if operation.IsCancellationReserved then
                                do! operation.WaitForCancellationResponseAsync()

                            outcome <- WorkspaceOperationCompletion.Cancelled
                        | :? IOException as error ->
                            outcome <-
                                WorkspaceOperationCompletion.Failed("io_error", error.Message)
                        | error ->
                            outcome <-
                                WorkspaceOperationCompletion.Failed(
                                    "operation_failed",
                                    error.Message
                                )

                        try
                            do! complete ()
                        finally
                            if publicationHeld then
                                context.PublicationGate.Release() |> ignore

                            context.ActiveOperations.TryRemove operationId |> ignore
                            operation.Complete()
                    }

                return
                    Ok
                        { Result =
                            WorkspaceRpcResponses.commandOperationResult
                                operationId
                                context.State.Revision
                          Notifications = []
                          BackgroundWork = Some background
                          AfterResponse = None
                          StopAfterResponse = false }
        }
