namespace Dotnet.WorkspaceExplorer

#nowarn "3511"

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

    let private invalidPreview =
        RpcErrors.invalidParams
            "The confirmation token does not identify a current preview of this operation kind."

    let private restoreProgress (requestId: PackageRequestId) state =
        Notification(
            "package/restore/progress",
            RpcValue.map
                [ "requestId", RpcValue.String(requestId.Value.ToString "D")
                  "state", RpcValue.String state ]
        )

    let private restoreFailure (sink: RpcNotificationSink) (requestId: PackageRequestId) =
        PackageRpcResponses.restoreTransportFailureNotification requestId RpcErrors.responseTooLarge
        |> sink.WriteAsync

    let private startSearch
        (state: PackageRpcState)
        (requestId: PackageRequestId)
        search
        pageSize
        continuation
        =
        let pageSize =
            PackagePageSize.create pageSize
            |> Result.defaultWith (fun _ -> invalidOp "page size")

        let work (sink: RpcNotificationSink) cancellationToken =
            task {
                let! outcome =
                    PackageProducer.collect
                        state.Ports.Search
                        (request
                            state
                            requestId
                            { Search = search
                              PageSize = pageSize
                              Continuation = continuation })
                    |> fromAsync cancellationToken

                let projected =
                    outcome
                    |> Result.map (fun (items, completion) ->
                        PackageRpcResponses.searchResult
                            requestId
                            { Items = items
                              Continuation = completion.Continuation
                              SourceFailures = completion.SourceFailures })

                do! completed sink "package/search/completed" requestId projected
            }

        PackageRpcResponses.accepted requestId
        |> fun accepted -> continueWithBackground accepted work |> Ok

    let private installed
        (state: PackageRpcState)
        (requestId: PackageRequestId)
        pageSize
        offset
        cancellationToken
        =
        task {
            let request = request state requestId ()

            let! immediate =
                PackageProducer.collect state.Ports.Installed request
                |> fromAsync cancellationToken

            match immediate with
            | Error failure -> return Error(PackageRpcResponses.failureError failure)
            | Ok(entries, ()) ->
                let work (sink: RpcNotificationSink) backgroundCancellation =
                    task {
                        try
                            do! sink.WriteAsync(restoreProgress requestId "inProgress")

                            let! refreshed =
                                PackageProducer.collect state.Ports.RefreshInstalled request
                                |> fromAsync backgroundCancellation

                            match refreshed with
                            | Ok(value, ()) ->
                                do!
                                    sink.WriteAsync(
                                        Notification(
                                            "package/installed/refreshed",
                                            PackageRpcResponses.installedResult
                                                requestId
                                                "refreshed"
                                                pageSize
                                                offset
                                                value
                                        )
                                    )

                                do!
                                    sink.WriteAsync(
                                        PackageRpcResponses.restoreCompletedNotification
                                            requestId
                                            (Ok())
                                    )
                            | Error failure ->
                                do!
                                    sink.WriteAsync(
                                        PackageRpcResponses.restoreCompletedNotification
                                            requestId
                                            (Error failure)
                                    )
                        with :? RpcFrameLimitExceededException ->
                            do! restoreFailure sink requestId
                    }

                return
                    Ok(
                        continueWithBackground
                            (PackageRpcResponses.installedResult
                                requestId
                                "inProgress"
                                pageSize
                                offset
                                entries)
                            work
                    )
        }

    let private inventory (requestId: PackageRequestId) methodName operation projection =
        let work sink cancellationToken =
            task {
                let! outcome =
                    operation ()
                    |> fun (producer, request) -> PackageProducer.collect producer request
                    |> fromAsync cancellationToken

                let projected = outcome |> Result.map (fst >> projection)
                do! completed sink methodName requestId projected
            }

        PackageRpcResponses.accepted requestId
        |> fun accepted -> continueWithBackground accepted work |> Ok

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
            | PackageRpcRequest.Search(requestId, search, pageSize, continuation) ->
                return startSearch state requestId search pageSize continuation
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
            | PackageRpcRequest.Installed(requestId, pageSize, offset) ->
                return! installed state requestId pageSize offset cancellationToken
            | PackageRpcRequest.Updates(requestId, prerelease, pageSize, offset) ->
                return
                    inventory
                        requestId
                        "package/updates/completed"
                        (fun () -> state.Ports.Updates, request state requestId prerelease)
                        (PackageRpcResponses.updatesResult pageSize offset)
            | PackageRpcRequest.Consolidation(requestId, pageSize, offset) ->
                return
                    inventory
                        requestId
                        "package/consolidation/completed"
                        (fun () -> state.Ports.Consolidation, request state requestId ())
                        (PackageRpcResponses.consolidationResult pageSize offset)
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
