namespace Dotnet.CLI.Plus

#nowarn "3511"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Transport
open CanonicalMutationPlanning

type internal ProjectRelocationOperation =
    static member Start
        (
            context: CanonicalCommandOperationContext,
            plan: PlannedMutation,
            previewId: string,
            requestCancellationToken: CancellationToken
        ) : Task<Result<RpcDispatchResult, RpcError>> =
        task {
            let operationId = Guid.NewGuid().ToString "N"
            let operation = ExportOperationState requestCancellationToken

            if not (context.ActiveOperations.TryAdd(operationId, operation)) then
                operation.Complete()
                return Error RpcErrors.internalError
            else
                let background (sink: RpcNotificationSink) sessionToken =
                    task {
                        let mutable sequence = 0
                        let mutable publicationHeld = false
                        let mutable completionReserved = false
                        let mutable outcome = PublicOperationOutcome.Succeeded

                        let nextSequence () = Interlocked.Increment(&sequence) - 1

                        let operationFailure failure =
                            match failure with
                            | Cancelled _ -> PublicOperationOutcome.Cancelled
                            | _ ->
                                PublicOperationOutcome.Failed(
                                    failure.Code.Value,
                                    failure.Diagnostic.Message
                                )

                        let complete () =
                            task {
                                let! completedOutcome =
                                    PipeOperations.completedOutcome
                                        operation
                                        completionReserved
                                        outcome

                                do!
                                    sink.WriteAsync(
                                        PublicProtocol.operationCompleted
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
                                    PublicProtocol.operationProgress
                                        context.State.Descriptor
                                        operationId
                                        (nextSequence ())
                                        context.State.Revision
                                        "Starting project relocation."
                                )

                            let execution =
                                context.Coordinator.ExecuteOperation(
                                    plannedRequest plan,
                                    plannedActions plan,
                                    MutationConfirmationToken.Create previewId,
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
                                let! invalidated =
                                    context.State.InvalidateFromTransactionAsync(
                                        plannedPaths plan,
                                        CancellationToken.None
                                    )

                                let notifications = context.MutationNotifications invalidated

                                let reset =
                                    notifications
                                    |> List.exists (fun notification ->
                                        RpcCodec.encodeFrame notification
                                        |> fun frame -> frame.Length > context.MaximumFrameBytes())

                                let! effectiveInvalidation =
                                    if reset then
                                        task {
                                            let! value =
                                                PipeWorkspaceRequests.resetForFramePressure
                                                    context.State
                                                    CancellationToken.None

                                            return WorkspaceInvalidationResult.Reset value
                                        }
                                    else
                                        Task.FromResult invalidated

                                let requiresWatcherReset =
                                    match effectiveInvalidation with
                                    | WorkspaceInvalidationResult.Reset _ -> true
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

                            outcome <- PublicOperationOutcome.Cancelled
                        | :? IOException as error ->
                            outcome <- PublicOperationOutcome.Failed("io_error", error.Message)
                        | error ->
                            outcome <-
                                PublicOperationOutcome.Failed("operation_failed", error.Message)

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
                            PublicProtocol.commandOperationResult operationId context.State.Revision
                          Notifications = []
                          BackgroundWork = Some background
                          AfterResponse = None
                          StopAfterResponse = false }
        }
