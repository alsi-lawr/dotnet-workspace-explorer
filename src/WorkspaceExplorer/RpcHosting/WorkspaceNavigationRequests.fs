namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex

#nowarn "3511"

open System.IO
open System.Threading.Tasks

module internal WorkspaceNavigationRequests =
    let mutationNotifications =
        function
        | WorkspaceProjectInvalidationResult.Delta delta ->
            [ WorkspaceRpcNotifications.workspaceDelta delta ]
        | WorkspaceProjectInvalidationResult.Reset reset ->
            [ WorkspaceRpcNotifications.workspaceReset reset ]
        | WorkspaceProjectInvalidationResult.None -> []

    let prepareWatcher (context: WorkspaceRpcContext) cancellationToken =
        task {
            context.Watcher.Resume()
            let! handoff = context.Watcher.ActivateAsync cancellationToken

            match handoff with
            | WorkspaceWatchHandoff.Complete -> return true
            | WorkspaceWatchHandoff.Revalidate _ ->
                context.Watcher.QueueActivationHandoff handoff
                return true
            | WorkspaceWatchHandoff.Uncertain -> return false
        }

    let private guardHydration (context: WorkspaceRpcContext) parentNodeId cancellationToken =
        task {
            let! resolved =
                context.State.SemanticContextAsync(parentNodeId, None, cancellationToken)

            match resolved with
            | Ok(_, target) when target.Node.Kind = WorkspaceNodeKind.Project ->
                match target.ProjectPath with
                | Some projectPath ->
                    return! context.Watcher.GuardHydrationAsync(projectPath, cancellationToken)
                | None -> return true
            | _ -> return true
        }

    let rebuildWatcher (context: WorkspaceRpcContext) cancellationToken =
        task {
            let notifications = ResizeArray<RpcFrame>()

            let publish handoff =
                match handoff with
                | WorkspaceProjectInvalidationResult.Delta delta ->
                    notifications.Add(WorkspaceRpcNotifications.workspaceDelta delta)
                | WorkspaceProjectInvalidationResult.Reset reset ->
                    notifications.Add(WorkspaceRpcNotifications.workspaceReset reset)
                | WorkspaceProjectInvalidationResult.None -> ()

                Task.FromResult(())

            let! _ = context.Watcher.RebuildAndRevalidateAsync(publish, cancellationToken)
            return notifications |> Seq.toList
        }

    let resetForFramePressure (state: WorkspaceIndex) cancellationToken =
        task {
            let diagnostic =
                WorkspaceDiagnostic.CreateSimple(
                    WorkspaceDiagnosticSeverity.Warning,
                    WorkspaceDiagnosticCode.Create "workspace.delta_pressure",
                    "Verified delta exceeded capacity; request a fresh workspace graph.",
                    true,
                    CorrelationId.New()
                )

            return! state.ResetAsync(diagnostic, cancellationToken)
        }

    let private dispatchRoot context cancellationToken =
        task {
            let! active = prepareWatcher context cancellationToken

            if not active then
                return Error RpcErrors.internalError
            else
                let! rooted = context.State.RootAsync cancellationToken

                match rooted with
                | Error rpcError -> return Error rpcError
                | Ok(revision, nodes) ->
                    return
                        Ok(
                            RpcRequestResult.Continue
                                { Result =
                                    WorkspaceRpcResponses.rootResult
                                        context.State.Descriptor
                                        revision
                                        nodes
                                  Notifications = []
                                  BackgroundWork = context.StartWatcher true
                                  AfterResponse = None }
                        )
        }

    let private dispatchChildren context parentNodeId pageSize continuation cancellationToken =
        task {
            let! active = prepareWatcher context cancellationToken

            if not active then
                return Error RpcErrors.internalError
            else
                let! guarded = guardHydration context parentNodeId cancellationToken

                if not guarded then
                    return Error RpcErrors.internalError
                else
                    let! page =
                        context.State.ChildrenAsync(
                            parentNodeId,
                            pageSize,
                            context.MaximumPageSize(),
                            continuation,
                            cancellationToken
                        )

                    match page with
                    | Error rpcError -> return Error rpcError
                    | Ok result ->
                        let! stateNotifications, reset =
                            match
                                result.Delta |> Option.map WorkspaceRpcNotifications.workspaceDelta
                            with
                            | Some notification when
                                (MessagePackRpcCodec.encodeFrame notification).Length > context
                                    .MaximumFrameBytes()
                                ->
                                task {
                                    let! reset =
                                        resetForFramePressure context.State cancellationToken

                                    return [ WorkspaceRpcNotifications.workspaceReset reset ], true
                                }
                            | Some notification -> Task.FromResult([ notification ], false)
                            | None -> Task.FromResult([], false)

                        if reset then
                            context.Watcher.Pause()

                        if not reset then
                            let! _ = prepareWatcher context cancellationToken
                            ()

                        return
                            Ok(
                                RpcRequestResult.Continue
                                    { Result =
                                        WorkspaceRpcResponses.childrenResult
                                            context.State.Descriptor
                                            result.Revision
                                            result.ParentWorkspaceNodeId
                                            result.Nodes
                                            result.NextToken
                                      Notifications = stateNotifications
                                      BackgroundWork =
                                        if reset then None else context.StartWatcher true
                                      AfterResponse = None }
                            )
        }

    let private dispatchRefresh context expectedRevision cancellationToken =
        task {
            let! active = prepareWatcher context cancellationToken

            if not active then
                return Error RpcErrors.internalError
            else
                let! refreshed = context.State.RefreshAsync(expectedRevision, cancellationToken)

                match refreshed with
                | Error rpcError -> return Error rpcError
                | Ok result ->
                    let! effective =
                        match result with
                        | WorkspaceRefreshResult.Refreshed(_, Some delta) when
                            (MessagePackRpcCodec.encodeFrame (
                                WorkspaceRpcNotifications.workspaceDelta delta
                            ))
                                .Length > context.MaximumFrameBytes()
                            ->
                            task {
                                let! reset = resetForFramePressure context.State cancellationToken
                                return WorkspaceRefreshResult.Reset reset
                            }
                        | _ -> Task.FromResult result

                    let revision, reset, stateNotifications =
                        match effective with
                        | WorkspaceRefreshResult.Refreshed(revision, delta) ->
                            revision,
                            false,
                            (delta
                             |> Option.map WorkspaceRpcNotifications.workspaceDelta
                             |> Option.toList)
                        | WorkspaceRefreshResult.Reset reset ->
                            reset.Revision.Value,
                            true,
                            [ WorkspaceRpcNotifications.workspaceReset reset ]

                    if reset then
                        context.Watcher.Pause()

                    let! handoffNotifications =
                        if reset then
                            Task.FromResult []
                        else
                            rebuildWatcher context cancellationToken

                    return
                        Ok(
                            RpcRequestResult.Continue
                                { Result = WorkspaceRpcResponses.refreshResult revision reset
                                  Notifications = stateNotifications @ handoffNotifications
                                  BackgroundWork = None
                                  AfterResponse = None }
                        )
        }

    let private dispatchResolveFile
        (context: WorkspaceRpcContext)
        targetNodeId
        expectedRevision
        cancellationToken
        =
        task {
            let! resolved =
                context.State.SemanticContextAsync(
                    targetNodeId,
                    Some expectedRevision,
                    cancellationToken
                )

            match resolved with
            | Error rpcError -> return Error rpcError
            | Ok(revision, target) ->
                let resolvedPath =
                    match target.Node.Kind with
                    | WorkspaceNodeKind.Project -> target.ProjectPath
                    | _ -> target.PhysicalPath

                match target.Node.Kind, resolvedPath with
                | WorkspaceNodeKind.Project, Some path
                | (WorkspaceNodeKind.ProjectFile | WorkspaceNodeKind.SolutionItem), Some path when
                    File.Exists path.Value
                    ->
                    return
                        Ok(
                            RpcRequestResult.Continue
                                { Result =
                                    WorkspaceRpcResponses.fileResolveResult
                                        revision
                                        target.Node.Id.Value
                                        path.Value
                                  Notifications = []
                                  BackgroundWork = None
                                  AfterResponse = None }
                        )
                | (WorkspaceNodeKind.Project | WorkspaceNodeKind.ProjectFile | WorkspaceNodeKind.SolutionItem),
                  _ ->
                    return
                        Error(
                            RpcErrors.create "not_found" "The workspace file no longer exists." None
                        )
                | _ ->
                    return
                        Error(
                            RpcErrors.invalidParams
                                "The requested workspace node is not an openable file."
                        )
        }

    let private dispatchGitStatus
        (context: WorkspaceRpcContext)
        expectedRevision
        cancellationToken
        =
        task {
            if not (context.GitStatusNegotiated()) then
                return
                    Error(
                        RpcErrors.unsupported
                            "workspace.git.status was not negotiated for this session."
                    )
            else
                let! result =
                    context.GitStatus.ReadAsync(context.State, expectedRevision, cancellationToken)

                return
                    result
                    |> Result.map (fun value ->
                        RpcRequestResult.Continue
                            { Result = value
                              Notifications = []
                              BackgroundWork = None
                              AfterResponse = None })
        }

    let private dispatchCancel (context: WorkspaceRpcContext) operationId =
        let accepted, afterResponse =
            match context.ActiveOperations.TryGetValue operationId with
            | true, operation when operation.TryReserveCancellation() ->
                true, Some operation.CommitCancellationAfterResponse
            | _ -> false, None

        RpcRequestResult.Continue
            { Result = WorkspaceRpcResponses.cancelResult accepted
              Notifications = []
              BackgroundWork = None
              AfterResponse = afterResponse }
        |> Ok
        |> Task.FromResult

    let private dispatchShutdown (context: WorkspaceRpcContext) =
        for operation in context.ActiveOperations.Values do
            operation.CancelForShutdown()

        RpcRequestResult.Stop WorkspaceRpcResponses.shutdownResult
        |> Ok
        |> Task.FromResult

    let private someAsync operation =
        task {
            let! result = operation
            return Some result
        }

    let tryDispatch (context: WorkspaceRpcContext) request cancellationToken =
        match request with
        | WorkspaceRpcRequest.Root -> dispatchRoot context cancellationToken |> someAsync
        | WorkspaceRpcRequest.Children(parentNodeId, pageSize, continuation) ->
            dispatchChildren context parentNodeId pageSize continuation cancellationToken
            |> someAsync
        | WorkspaceRpcRequest.ResolveFile(targetNodeId, expectedRevision) ->
            dispatchResolveFile context targetNodeId expectedRevision cancellationToken
            |> someAsync
        | WorkspaceRpcRequest.GitStatus expectedRevision ->
            dispatchGitStatus context expectedRevision cancellationToken |> someAsync
        | WorkspaceRpcRequest.Refresh expectedRevision ->
            dispatchRefresh context expectedRevision cancellationToken |> someAsync
        | WorkspaceRpcRequest.Cancel operationId -> dispatchCancel context operationId |> someAsync
        | WorkspaceRpcRequest.Shutdown -> dispatchShutdown context |> someAsync
        | WorkspaceRpcRequest.Export
        | WorkspaceRpcRequest.CreateOptions _
        | WorkspaceRpcRequest.AddExistingStart _
        | WorkspaceRpcRequest.AddExistingChildren _
        | WorkspaceRpcRequest.AddExistingClose _
        | WorkspaceRpcRequest.CommandList _
        | WorkspaceRpcRequest.CommandDescribe _
        | WorkspaceRpcRequest.CommandPreview _
        | WorkspaceRpcRequest.CommandExecute _ -> Task.FromResult None
