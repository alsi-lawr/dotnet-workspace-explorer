namespace Dotnet.WorkspaceExplorer

#nowarn "3511"

open System
open System.Threading
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.Rpc

[<RequireQualifiedAccess>]
module internal PackageRpcDispatch =
    let private continueWith result =
        RpcRequestResult.Continue
            { Result = result
              Notifications = []
              BackgroundWork = None
              AfterResponse = None }

    let private continueWithBackground result work =
        RpcRequestResult.Continue
            { Result = result
              Notifications = []
              BackgroundWork = Some work
              AfterResponse = None }

    let private request (state: PackageRpcState) (requestId: PackageRequestId) value =
        { Id = requestId
          Target = state.Target
          Value = value }

    let private fromAsync (cancellationToken: CancellationToken) operation =
        Async.StartAsTask(operation, cancellationToken = cancellationToken)

    let private result value = value |> continueWith |> Ok

    let private mapFailure value =
        value |> Result.mapError PackageRpcResponses.failureError

    let private completed
        (sink: RpcNotificationSink)
        methodName
        (requestId: PackageRequestId)
        (outcome: Result<RpcValue, PackageFailure>)
        =
        task {
            try
                do!
                    outcome
                    |> PackageRpcResponses.completedNotification methodName requestId
                    |> sink.WriteAsync
            with :? RpcFrameLimitExceededException ->
                do!
                    PackageRpcResponses.transportFailureNotification
                        methodName
                        requestId
                        RpcErrors.responseTooLarge
                    |> sink.WriteAsync
        }

    type private DiscoveryProgress() =
        member val BatchCount = 0 with get, set
        member val ItemCount = 0 with get, set

    let private cancelledFailure =
        PackageFailure.create
            PackageFailureKind.Cancelled
            "The package work was cancelled."
            PackageFailureRetry.Never
        |> Result.defaultWith (failwithf "%A")

    let rec private containsFrameLimitFailure (error: exn) =
        match error with
        | :? RpcFrameLimitExceededException -> true
        | :? AggregateException as aggregate ->
            aggregate.Flatten().InnerExceptions |> Seq.exists containsFrameLimitFailure
        | _ -> false

    let private writeBatches
        maximumFrameBytes
        maximumItems
        (progress: DiscoveryProgress)
        countItems
        notification
        values
        (sink: RpcNotificationSink)
        (cancellation: CancellationToken)
        =
        task {
            let values = values |> List.toArray
            let mutable offset = 0

            while offset < values.Length do
                cancellation.ThrowIfCancellationRequested()
                let remaining = values.Length - offset
                let maximumCount = min maximumItems remaining

                let encode count =
                    values[offset .. offset + count - 1]
                    |> Array.toList
                    |> notification progress.BatchCount
                    |> EncodedRpcNotification.Create

                let whole = encode maximumCount

                let count, encoded =
                    if whole.Length <= maximumFrameBytes then
                        maximumCount, whole
                    else
                        let first = encode 1

                        if first.Length > maximumFrameBytes then
                            raise (RpcFrameLimitExceededException(maximumFrameBytes, first.Length))

                        let mutable accepted = 1
                        let mutable selected = first
                        let mutable low = 2
                        let mutable high = maximumCount - 1

                        while low <= high do
                            let middle = low + (high - low) / 2
                            let candidate = encode middle

                            if candidate.Length <= maximumFrameBytes then
                                accepted <- middle
                                selected <- candidate
                                low <- middle + 1
                            else
                                high <- middle - 1

                        accepted, selected

                cancellation.ThrowIfCancellationRequested()
                do! sink.WriteEncodedAsync encoded
                progress.BatchCount <- progress.BatchCount + 1

                if countItems then
                    progress.ItemCount <- progress.ItemCount + count

                offset <- offset + count
        }

    let private producerSink maximumFrameBytes maximumItems progress notification sink =
        fun (cancellation: CancellationToken) batch ->
            async {
                do!
                    writeBatches
                        maximumFrameBytes
                        maximumItems
                        progress
                        true
                        notification
                        (NonEmptyList.toList batch)
                        sink
                        cancellation
                    |> Async.AwaitTask
            }

    let private discoveryWork
        (state: PackageRpcState)
        kind
        requestId
        terminalMethod
        operation
        beforeCompleted
        completedNotification
        =
        let work (sink: RpcNotificationSink) backgroundCancellation =
            task {
                let progress = DiscoveryProgress()

                let writeTerminal notification =
                    state.Release(kind, requestId)
                    sink.WriteAsync notification

                try
                    try
                        let! outcome = operation progress sink backgroundCancellation

                        match outcome with
                        | Ok completion ->
                            do! beforeCompleted progress sink backgroundCancellation completion

                            do! completedNotification progress completion |> writeTerminal
                        | Error failure ->
                            do!
                                PackageRpcResponses.discoveryFailed
                                    terminalMethod
                                    requestId
                                    progress.BatchCount
                                    progress.ItemCount
                                    failure
                                |> writeTerminal
                    with
                    | error when containsFrameLimitFailure error ->
                        do!
                            PackageRpcResponses.discoveryFailedWithRpcError
                                terminalMethod
                                requestId
                                progress.BatchCount
                                progress.ItemCount
                                RpcErrors.responseTooLarge
                            |> writeTerminal
                    | :? OperationCanceledException ->
                        do!
                            PackageRpcResponses.discoveryFailed
                                terminalMethod
                                requestId
                                progress.BatchCount
                                progress.ItemCount
                                cancelledFailure
                            |> writeTerminal
                    | _ ->
                        do!
                            PackageRpcResponses.discoveryFailedWithRpcError
                                terminalMethod
                                requestId
                                progress.BatchCount
                                progress.ItemCount
                                RpcErrors.internalError
                            |> writeTerminal
                finally
                    state.Release(kind, requestId)
            }

        continueWithBackground (PackageRpcResponses.accepted requestId) work

    let private startDiscovery
        (state: PackageRpcState)
        (kind: PackageDiscoveryKind)
        (requestId: PackageRequestId)
        create
        =
        if state.TryAdmit(kind, requestId) then
            create () |> Ok
        else
            Error PackageRpcResponses.discoveryInProgress

    let private noCompletionData _ _ _ _ = task { return () }

    let private invalidPreview =
        RpcErrors.invalidParams
            "The confirmation token does not identify a current preview of this operation kind."

    let private startSearch
        (state: PackageRpcState)
        (requestId: PackageRequestId)
        search
        continuation
        maximumFrameBytes
        maximumItems
        =
        let pageSize =
            PackagePageSize.create maximumItems
            |> Result.defaultWith (fun _ -> invalidOp "page size")

        startDiscovery state PackageDiscoveryKind.Search requestId (fun () ->
            let operation progress sink cancellation =
                state.Ports.Search
                    (request
                        state
                        requestId
                        { Search = search
                          PageSize = pageSize
                          Continuation = continuation })
                    (producerSink
                        maximumFrameBytes
                        maximumItems
                        progress
                        (fun sequence items ->
                            PackageRpcResponses.searchBatch requestId sequence items [])
                        sink)
                |> fromAsync cancellation

            let beforeCompleted progress sink cancellation (completion: PackageSearchCompletion) =
                match completion.SourceFailures with
                | [] -> task { return () }
                | failures ->
                    writeBatches
                        maximumFrameBytes
                        maximumItems
                        progress
                        false
                        (fun sequence values ->
                            PackageRpcResponses.searchBatch requestId sequence [] values)
                        failures
                        sink
                        cancellation

            discoveryWork
                state
                PackageDiscoveryKind.Search
                requestId
                "package/search/completed"
                operation
                beforeCompleted
                (fun progress completion ->
                    PackageRpcResponses.searchCompleted
                        requestId
                        progress.BatchCount
                        progress.ItemCount
                        completion.Query
                        completion.Continuation))

    let private startInstalled
        (state: PackageRpcState)
        (requestId: PackageRequestId)
        restore
        maximumFrameBytes
        maximumItems
        =
        startDiscovery state PackageDiscoveryKind.Installed requestId (fun () ->
            let batchMethod, terminalMethod, producer =
                if restore then
                    "package/installed/restore/batch",
                    "package/installed/restore/completed",
                    state.Ports.RefreshInstalled
                else
                    "package/installed/batch", "package/installed/completed", state.Ports.Installed

            let operation progress sink cancellation =
                producer
                    (request state requestId ())
                    (producerSink
                        maximumFrameBytes
                        maximumItems
                        progress
                        (PackageRpcResponses.installedBatch batchMethod requestId)
                        sink)
                |> fromAsync cancellation

            discoveryWork
                state
                PackageDiscoveryKind.Installed
                requestId
                terminalMethod
                operation
                noCompletionData
                (fun progress _ ->
                    PackageRpcResponses.discoveryCompleted
                        terminalMethod
                        requestId
                        progress.BatchCount
                        progress.ItemCount
                        []))

    let private startUpdates
        (state: PackageRpcState)
        (requestId: PackageRequestId)
        prerelease
        maximumFrameBytes
        maximumItems
        =
        startDiscovery state PackageDiscoveryKind.Updates requestId (fun () ->
            let operation progress sink cancellation =
                state.Ports.Updates
                    (request state requestId prerelease)
                    (producerSink
                        maximumFrameBytes
                        maximumItems
                        progress
                        (PackageRpcResponses.updatesBatch requestId)
                        sink)
                |> fromAsync cancellation

            discoveryWork
                state
                PackageDiscoveryKind.Updates
                requestId
                "package/updates/completed"
                operation
                noCompletionData
                (fun progress _ ->
                    PackageRpcResponses.discoveryCompleted
                        "package/updates/completed"
                        requestId
                        progress.BatchCount
                        progress.ItemCount
                        []))

    let private startConsolidation
        (state: PackageRpcState)
        (requestId: PackageRequestId)
        maximumFrameBytes
        maximumItems
        =
        startDiscovery state PackageDiscoveryKind.Consolidation requestId (fun () ->
            let operation progress sink cancellation =
                state.Ports.Consolidation
                    (request state requestId ())
                    (producerSink
                        maximumFrameBytes
                        maximumItems
                        progress
                        (PackageRpcResponses.consolidationBatch requestId)
                        sink)
                |> fromAsync cancellation

            discoveryWork
                state
                PackageDiscoveryKind.Consolidation
                requestId
                "package/consolidation/completed"
                operation
                noCompletionData
                (fun progress _ ->
                    PackageRpcResponses.discoveryCompleted
                        "package/consolidation/completed"
                        requestId
                        progress.BatchCount
                        progress.ItemCount
                        []))

    let private preview
        (state: PackageRpcState)
        (requestId: PackageRequestId)
        operation
        targets
        source
        cancellationToken
        =
        task {
            let preconditionRequest =
                request
                    state
                    requestId
                    { Operation = operation
                      Targets = targets
                      BrowseSource = source }

            let! precondition =
                state.Ports.PreviewPrecondition preconditionRequest
                |> fromAsync cancellationToken

            match precondition with
            | Error failure -> return Error(PackageRpcResponses.failureError failure)
            | Ok current ->
                let! outcome =
                    state.Ports.Preview(
                        request
                            state
                            requestId
                            { Operation = operation
                              Targets = targets
                              BrowseSource = source
                              Precondition = current }
                    )
                    |> fromAsync cancellationToken

                return
                    outcome
                    |> mapFailure
                    |> Result.map (fun value ->
                        state.Remember value
                        value |> PackageRpcResponses.previewResult |> continueWith)
        }

    let private previewBatch
        (state: PackageRpcState)
        (requestId: PackageRequestId)
        updates
        source
        cancellationToken
        =
        task {
            let preconditionRequest =
                request
                    state
                    requestId
                    { Updates = updates
                      BrowseSource = source }

            let! precondition =
                state.Ports.UpdateBatchPrecondition preconditionRequest
                |> fromAsync cancellationToken

            match precondition with
            | Error failure -> return Error(PackageRpcResponses.failureError failure)
            | Ok current ->
                let! outcome =
                    state.Ports.PreviewUpdateBatch(
                        request
                            state
                            requestId
                            { Updates = updates
                              BrowseSource = source
                              Precondition = current }
                    )
                    |> fromAsync cancellationToken

                return
                    outcome
                    |> mapFailure
                    |> Result.map (fun value ->
                        state.Remember value
                        value |> PackageRpcResponses.batchPreviewResult |> continueWith)
        }

    let private execute (state: PackageRpcState) (requestId: PackageRequestId) token =
        match state.TakeSingle token with
        | None -> Error invalidPreview
        | Some preview ->
            match PackageConfirmation.create preview token with
            | Error _ -> Error invalidPreview
            | Ok confirmation ->
                let work (sink: RpcNotificationSink) cancellationToken =
                    task {
                        let mutable progressExceededLimit = false

                        let report progress =
                            if not progressExceededLimit then
                                try
                                    Notification(
                                        "package/operations/progress",
                                        RpcValue.map
                                            [ "requestId",
                                              RpcValue.String(requestId.Value.ToString "D")
                                              "progress", PackageRpcResponses.progress progress ]
                                    )
                                    |> sink.WriteAsync
                                    |> _.GetAwaiter()
                                    |> _.GetResult()
                                with :? RpcFrameLimitExceededException ->
                                    progressExceededLimit <- true

                        let! outcome =
                            state.Ports.ExecuteConfirmed
                                (request state requestId confirmation)
                                report
                            |> fromAsync cancellationToken

                        if progressExceededLimit then
                            do!
                                PackageRpcResponses.transportFailureNotification
                                    "package/operations/completed"
                                    requestId
                                    RpcErrors.responseTooLarge
                                |> sink.WriteAsync
                        else
                            let projected =
                                outcome |> Result.map PackageRpcResponses.executionResult

                            do! completed sink "package/operations/completed" requestId projected
                    }

                PackageRpcResponses.accepted requestId
                |> fun accepted -> continueWithBackground accepted work |> Ok

    let private executeBatch (state: PackageRpcState) (requestId: PackageRequestId) token =
        match state.TakeBatch token with
        | None -> Error invalidPreview
        | Some preview ->
            match PackageUpdateBatchConfirmation.create preview token with
            | Error _ -> Error invalidPreview
            | Ok confirmation ->
                let work (sink: RpcNotificationSink) cancellationToken =
                    task {
                        let mutable progressExceededLimit = false

                        let report progress =
                            if not progressExceededLimit then
                                try
                                    Notification(
                                        "package/operations/progress",
                                        RpcValue.map
                                            [ "requestId",
                                              RpcValue.String(requestId.Value.ToString "D")
                                              "progress", PackageRpcResponses.progress progress ]
                                    )
                                    |> sink.WriteAsync
                                    |> _.GetAwaiter()
                                    |> _.GetResult()
                                with :? RpcFrameLimitExceededException ->
                                    progressExceededLimit <- true

                        let! outcome =
                            state.Ports.ExecuteConfirmedUpdateBatch
                                (request state requestId confirmation)
                                report
                            |> fromAsync cancellationToken

                        if progressExceededLimit then
                            do!
                                PackageRpcResponses.transportFailureNotification
                                    "package/operations/completed"
                                    requestId
                                    RpcErrors.responseTooLarge
                                |> sink.WriteAsync
                        else
                            let projected =
                                outcome |> Result.map PackageRpcResponses.executionResult

                            do! completed sink "package/operations/completed" requestId projected
                    }

                PackageRpcResponses.accepted requestId
                |> fun accepted -> continueWithBackground accepted work |> Ok

    let dispatch
        (state: PackageRpcState)
        maximumFrameBytes
        maximumItems
        (rpcRequest: PackageRpcRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            match rpcRequest with
            | PackageRpcRequest.Sources requestId ->
                let! outcome =
                    state.Ports.ConfiguredSources(request state requestId ())
                    |> fromAsync cancellationToken

                return
                    outcome
                    |> mapFailure
                    |> Result.map PackageRpcResponses.sourcesResult
                    |> Result.bind result
            | PackageRpcRequest.SourceMapping(requestId, package, source, transitives) ->
                let! outcome =
                    state.Ports.SourceMapping(
                        request
                            state
                            requestId
                            { Package = package
                              CandidateSource = source
                              RestoredTransitives = transitives }
                    )
                    |> fromAsync cancellationToken

                return
                    outcome
                    |> mapFailure
                    |> Result.map PackageRpcResponses.sourceMappingResult
                    |> Result.bind result
            | PackageRpcRequest.Search(requestId, search, continuation) ->
                return
                    startSearch state requestId search continuation maximumFrameBytes maximumItems
            | PackageRpcRequest.Details(requestId, package, version, source) ->
                let! outcome =
                    state.Ports.Details(
                        request
                            state
                            requestId
                            { Package = package
                              Version = version
                              Source = source }
                    )
                    |> fromAsync cancellationToken

                return
                    outcome
                    |> mapFailure
                    |> Result.map (PackageRpcResponses.detailsResult state.ReadmeEnabled)
                    |> Result.bind result
            | PackageRpcRequest.Installed requestId ->
                return startInstalled state requestId false maximumFrameBytes maximumItems
            | PackageRpcRequest.RestoreInstalled requestId ->
                return startInstalled state requestId true maximumFrameBytes maximumItems
            | PackageRpcRequest.Updates(requestId, prerelease) ->
                return startUpdates state requestId prerelease maximumFrameBytes maximumItems
            | PackageRpcRequest.Consolidation requestId ->
                return startConsolidation state requestId maximumFrameBytes maximumItems
            | PackageRpcRequest.Preview(requestId, operation, targets, source) ->
                return! preview state requestId operation targets source cancellationToken
            | PackageRpcRequest.PreviewBatch(requestId, updates, source) ->
                return! previewBatch state requestId updates source cancellationToken
            | PackageRpcRequest.Execute(requestId, token) -> return execute state requestId token
            | PackageRpcRequest.ExecuteBatch(requestId, token) ->
                return executeBatch state requestId token
            | PackageRpcRequest.CancelRequest requestId ->
                do!
                    state.Ports.Cancel(PackageCancellation.Request requestId)
                    |> fromAsync cancellationToken

                return Ok(continueWith (PackageRpcResponses.cancelled true))
            | PackageRpcRequest.CancelOperation operationId ->
                do!
                    state.Ports.Cancel(PackageCancellation.Operation operationId)
                    |> fromAsync cancellationToken

                return Ok(continueWith (PackageRpcResponses.cancelled true))
            | PackageRpcRequest.Shutdown ->
                return Ok(RpcRequestResult.Stop PackageRpcResponses.shutdown)
        }
