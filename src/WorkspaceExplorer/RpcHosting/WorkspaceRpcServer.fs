namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.IO
open System.Threading

module internal WorkspaceRpcServer =
    let private openWorkspace target cancellationToken =
        task {
            let! outcome = SolutionWorkspaceReader.OpenAsync(target, cancellationToken)

            return
                match outcome with
                | Success workspace -> Ok workspace
                | Failure failure -> Error(WorkspaceRpcResponses.failureError failure)
        }

    let runAsync
        (target: string)
        (exportCapacity: int)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            let! opened = openWorkspace target cancellationToken

            match opened with
            | Error rpcError ->
                do!
                    error.WriteLineAsync
                        $"dotnet-workspace-explorer workspace RPC startup failure: {rpcError.Message}"

                do! error.FlushAsync()
                return 64
            | Ok workspace ->
                let state = WorkspaceIndex.CreateProduction(target, workspace, exportCapacity)
                let mutable watcherStarted = false
                let mutable maximumFrameBytes = MessagePackRpcCodec.secureLimits.MaximumValueBytes
                let mutable maximumPageSize = 256
                let mutable gitStatusNegotiated = false
                let mutable gitStatusV2Negotiated = false
                let mutable addExistingNegotiated = false
                let mutable addExistingPresentationV2Negotiated = false
                let mutable addExistingDirectoriesV1Negotiated = false
                use publicationGate = new SemaphoreSlim(1, 1)

                let gitStatus = WorkspaceGitStatus(workspace.SolutionPath.Value)

                use addExistingSelector =
                    new AddExistingSelector(
                        (fun () -> maximumPageSize),
                        TimeProvider.System,
                        gitStatus.ReadPathSnapshotAsync
                    )

                let watcher =
                    WorkspaceIndexWatcher(
                        state,
                        128,
                        (fun () -> maximumFrameBytes),
                        publicationGate
                    )

                let coordinator =
                    let root =
                        Path.GetDirectoryName workspace.SolutionPath.Value
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    WorkspaceEditTransaction.CreateProduction(
                        WorkspaceArtifactPath.Create root,
                        fun () -> WorkspaceRevision.Create state.Revision
                    )

                let workspaceRoot =
                    Path.GetDirectoryName workspace.SolutionPath.Value
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory())

                let activeOperations =
                    ConcurrentDictionary<string, WorkspaceExportOperation> StringComparer.Ordinal

                let startWatcher active =
                    if watcherStarted then
                        None
                    elif not active then
                        None
                    else
                        watcherStarted <- true
                        Some(fun sink token -> watcher.StartAsync(sink, token))

                let workspaceRequestContext =
                    { State = state
                      GitStatus = gitStatus
                      GitStatusResponseVersion =
                        fun () ->
                            if gitStatusV2Negotiated then Some Version2
                            elif gitStatusNegotiated then Some Legacy
                            else None
                      Watcher = watcher
                      ActiveOperations = activeOperations
                      MaximumFrameBytes = fun () -> maximumFrameBytes
                      MaximumPageSize = fun () -> maximumPageSize
                      StartWatcher = startWatcher }

                let commandRequestContext =
                    { State = state
                      Watcher = watcher
                      Coordinator = coordinator
                      PublicationGate = publicationGate
                      ActiveOperations = activeOperations
                      WorkspaceRoot = workspaceRoot
                      MaximumFrameBytes = fun () -> maximumFrameBytes
                      AddExistingNegotiated = fun () -> addExistingNegotiated
                      AddExistingSelector = addExistingSelector
                      RebuildWatcher =
                        WorkspaceNavigationRequests.rebuildWatcher workspaceRequestContext
                      MutationNotifications = WorkspaceNavigationRequests.mutationNotifications }

                let initialize parameters _ =
                    task {
                        match WorkspaceRpc.parseInitialize parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            maximumFrameBytes <- request.MaximumFrameBytes
                            maximumPageSize <- request.MaximumPageSize

                            gitStatusNegotiated <-
                                request.Capabilities.Contains "workspace.git.status"

                            gitStatusV2Negotiated <-
                                request.Capabilities.Contains "workspace.git.status.v2"

                            addExistingNegotiated <-
                                request.Capabilities.Contains "workspace.addExisting.selector"

                            addExistingPresentationV2Negotiated <-
                                request.Capabilities.Contains
                                    "workspace.addExisting.presentation.v2"

                            addExistingDirectoriesV1Negotiated <-
                                request.Capabilities.Contains "workspace.addExisting.directories.v1"

                            return
                                Ok(
                                    WorkspaceRpcResponses.initializeResult
                                        state.Descriptor
                                        state.Revision
                                        request
                                )
                    }

                let dispatchCore
                    (_: RpcSessionContext)
                    methodName
                    parameters
                    requestCancellationToken
                    =
                    task {
                        match WorkspaceRpc.parseRequest methodName parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            let! addExistingResult =
                                task {
                                    let result value =
                                        { Result = value
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = None
                                          StopAfterResponse = false }

                                    match request with
                                    | WorkspaceRpcRequest.AddExistingStart(targetNodeId,
                                                                           selectionId,
                                                                           expectedRevision,
                                                                           pageSize) ->
                                        if not addExistingNegotiated then
                                            return
                                                Some(
                                                    Error(
                                                        RpcErrors.unsupported
                                                            "The client did not negotiate workspace.addExisting.selector."
                                                    )
                                                )
                                        else
                                            let! opened =
                                                state.WorkspaceAsync requestCancellationToken

                                            match opened with
                                            | Error error -> return Some(Error error)
                                            | Ok workspace ->
                                                let! resolved =
                                                    state.SemanticContextAsync(
                                                        targetNodeId,
                                                        Some expectedRevision,
                                                        requestCancellationToken
                                                    )

                                                match resolved with
                                                | Error error -> return Some(Error error)
                                                | Ok(_, target) ->
                                                    let! catalog =
                                                        WorkspaceTemplateCatalog.readAsync
                                                            workspace
                                                            requestCancellationToken

                                                    match catalog with
                                                    | Error error -> return Some(Error error)
                                                    | Ok catalog ->
                                                        let valid =
                                                            WorkspaceTemplateCatalog.options
                                                                target
                                                                true
                                                                catalog
                                                            |> Seq.exists (fun entry ->
                                                                entry.SelectionId = selectionId
                                                                && entry.Kind = WorkspaceCreateKind.AddExisting)

                                                        if not valid then
                                                            return
                                                                Some(
                                                                    Error(
                                                                        RpcErrors.invalidParams
                                                                            "selectionId is not the currently advertised Add Existing option."
                                                                    )
                                                                )
                                                        else
                                                            let! started =
                                                                addExistingSelector.StartAsync(
                                                                    workspace,
                                                                    state,
                                                                    target,
                                                                    selectionId,
                                                                    expectedRevision,
                                                                    pageSize,
                                                                    addExistingPresentationV2Negotiated,
                                                                    addExistingDirectoriesV1Negotiated,
                                                                    requestCancellationToken
                                                                )

                                                            return
                                                                Some(started |> Result.map result)
                                    | WorkspaceRpcRequest.AddExistingChildren(selectorId,
                                                                              parentEntryId,
                                                                              pageSize,
                                                                              continuationToken) ->
                                        if not addExistingNegotiated then
                                            return
                                                Some(
                                                    Error(
                                                        RpcErrors.unsupported
                                                            "The client did not negotiate workspace.addExisting.selector."
                                                    )
                                                )
                                        else
                                            return
                                                Some(
                                                    addExistingSelector.Children(
                                                        selectorId,
                                                        parentEntryId,
                                                        pageSize,
                                                        continuationToken,
                                                        state.Revision
                                                    )
                                                    |> Result.map result
                                                )
                                    | WorkspaceRpcRequest.AddExistingClose selectorId ->
                                        if not addExistingNegotiated then
                                            return
                                                Some(
                                                    Error(
                                                        RpcErrors.unsupported
                                                            "The client did not negotiate workspace.addExisting.selector."
                                                    )
                                                )
                                        else
                                            return
                                                Some(
                                                    addExistingSelector.Close selectorId
                                                    |> Result.map result
                                                )
                                    | _ -> return None
                                }

                            match addExistingResult with
                            | Some result -> return result
                            | None ->
                                let! workspaceResult =
                                    WorkspaceNavigationRequests.tryDispatch
                                        workspaceRequestContext
                                        request
                                        requestCancellationToken

                                match workspaceResult with
                                | Some result -> return result
                                | None ->
                                    let! exportResult =
                                        WorkspaceExportRequests.tryDispatch
                                            workspaceRequestContext
                                            request
                                            requestCancellationToken

                                    match exportResult with
                                    | Some result -> return result
                                    | None ->
                                        match request with
                                        | WorkspaceRpcRequest.CommandList _
                                        | WorkspaceRpcRequest.CreateOptions _
                                        | WorkspaceRpcRequest.CommandDescribe _
                                        | WorkspaceRpcRequest.CommandPreview _
                                        | WorkspaceRpcRequest.CommandExecute _ ->
                                            return!
                                                WorkspaceCommandRequests.dispatch
                                                    commandRequestContext
                                                    request
                                                    requestCancellationToken
                                        | _ -> return Error RpcErrors.internalError
                    }

                let dispatch
                    (context: RpcSessionContext)
                    (methodName: string)
                    (parameters: RpcValue)
                    (requestCancellationToken: CancellationToken)
                    =
                    task {
                        let serialized =
                            match WorkspaceRpc.parseRequest methodName parameters with
                            | Ok WorkspaceRpcRequest.Root
                            | Ok(WorkspaceRpcRequest.Children _)
                            | Ok(WorkspaceRpcRequest.ResolveFile _)
                            | Ok(WorkspaceRpcRequest.GitStatus _)
                            | Ok(WorkspaceRpcRequest.Refresh _)
                            | Ok(WorkspaceRpcRequest.CreateOptions _)
                            | Ok(WorkspaceRpcRequest.AddExistingStart _)
                            | Ok(WorkspaceRpcRequest.AddExistingChildren _)
                            | Ok(WorkspaceRpcRequest.AddExistingClose _)
                            | Ok(WorkspaceRpcRequest.CommandList _)
                            | Ok(WorkspaceRpcRequest.CommandDescribe _)
                            | Ok(WorkspaceRpcRequest.CommandPreview _)
                            | Ok(WorkspaceRpcRequest.CommandExecute _) -> true
                            | _ -> false

                        let mutable releaseOnExit = false

                        try
                            if serialized then
                                do! publicationGate.WaitAsync requestCancellationToken
                                releaseOnExit <- true

                            let! result =
                                dispatchCore context methodName parameters requestCancellationToken

                            return
                                match result with
                                | Ok value when serialized ->
                                    let release () = publicationGate.Release() |> ignore
                                    releaseOnExit <- false

                                    Ok
                                        { value with
                                            AfterResponse = Some release }
                                | _ -> result
                        finally
                            if releaseOnExit then
                                publicationGate.Release() |> ignore
                    }

                let configuration =
                    { Profile = WorkspaceRpcProfile.current
                      Limits = MessagePackRpcCodec.secureLimits
                      GetOutboundFrameLimit = fun () -> maximumFrameBytes
                      Initialize = initialize
                      Dispatch = dispatch }

                try
                    let! result =
                        RpcSession.runAsync configuration input output error cancellationToken

                    do! state.DisposeAsync()
                    return result
                with exceptionValue ->
                    do! state.DisposeAsync()
                    return raise exceptionValue
        }
