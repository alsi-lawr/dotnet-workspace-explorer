namespace Dotnet.WorkspaceExplorer

#nowarn "3511"

open System
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex

module internal WorkspaceExportRequests =
    let private dispatch (context: WorkspaceRpcContext) cancellationToken =
        task {
            let snapshotRevision = context.State.Revision
            let descriptor = context.State.Descriptor
            let operationId = Guid.NewGuid().ToString "N"
            let operation = WorkspaceExportOperation cancellationToken

            if not (context.ActiveOperations.TryAdd(operationId, operation)) then
                operation.Complete()
                return Error RpcErrors.internalError
            else
                let background (sink: RpcNotificationSink) sessionToken =
                    task {
                        let mutable sequence = 0
                        let mutable outcome = WorkspaceOperationCompletion.Succeeded
                        let mutable completionReserved = false

                        let reserveFailure failure =
                            task {
                                let! completed =
                                    WorkspaceOperations.completedOutcome
                                        operation
                                        completionReserved
                                        failure

                                outcome <- completed
                            }

                        try
                            try
                                use linked =
                                    CancellationTokenSource.CreateLinkedTokenSource(
                                        operation.Token,
                                        sessionToken
                                    )

                                let ensureActive () =
                                    if operation.IsCancellationReserved then
                                        raise (OperationCanceledException())

                                    linked.Token.ThrowIfCancellationRequested()

                                let reserveFinal () =
                                    task {
                                        ensureActive ()

                                        if operation.TryReserveCompletion() then
                                            completionReserved <- true
                                            return true
                                        else
                                            do! operation.WaitForCancellationResponseAsync()
                                            return false
                                    }

                                let writeBatch (batch: WorkspaceExportBatch) =
                                    task {
                                        let! next =
                                            WorkspaceOperations.writeExportBatch
                                                (context.MaximumFrameBytes())
                                                descriptor
                                                operationId
                                                snapshotRevision
                                                sequence
                                                batch.Nodes
                                                batch.IsFinal
                                                ensureActive
                                                reserveFinal
                                                sink

                                        sequence <- next
                                    }

                                let! exported =
                                    context.State.ExportAsync(
                                        snapshotRevision,
                                        writeBatch,
                                        linked.Token
                                    )

                                match exported with
                                | Error rpcError when rpcError.Code = "cancelled" ->
                                    raise (OperationCanceledException())
                                | Error rpcError ->
                                    do!
                                        reserveFailure (
                                            WorkspaceOperationCompletion.Failed(
                                                rpcError.Code,
                                                rpcError.Message
                                            )
                                        )
                                | Ok() ->
                                    if not completionReserved then
                                        raise (
                                            InvalidOperationException
                                                "A successful export did not emit a final chunk."
                                        )
                            with
                            | :? OperationCanceledException ->
                                if operation.IsCancellationReserved then
                                    do! operation.WaitForCancellationResponseAsync()

                                outcome <- WorkspaceOperationCompletion.Cancelled
                            | :? RpcFrameLimitExceededException ->
                                do!
                                    reserveFailure (
                                        WorkspaceOperationCompletion.Failed(
                                            "response_too_large",
                                            "Workspace export exceeded the outbound frame limit."
                                        )
                                    )
                            | :? InvalidOperationException
                            | :? ArgumentException
                            | :? FormatException ->
                                do!
                                    reserveFailure (
                                        WorkspaceOperationCompletion.Failed(
                                            "export_failed",
                                            "The workspace export could not be completed safely."
                                        )
                                    )

                            do!
                                sink.WriteAsync(
                                    WorkspaceRpcNotifications.operationCompleted
                                        descriptor
                                        operationId
                                        sequence
                                        snapshotRevision
                                        outcome
                                )
                        finally
                            context.ActiveOperations.TryRemove operationId |> ignore
                            operation.Complete()
                    }

                return
                    Ok(
                        RpcRequestResult.Continue
                            { Result =
                                WorkspaceRpcResponses.exportResult operationId snapshotRevision
                              Notifications = []
                              BackgroundWork = Some background
                              AfterResponse = None }
                    )
        }


    let tryDispatch context request cancellationToken =
        match request with
        | WorkspaceRpcRequest.Export ->
            task {
                let! result = dispatch context cancellationToken
                return Some result
            }
        | _ -> Task.FromResult None
