namespace Dotnet.CLI.Plus

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport

type internal ExportOperationState(sessionToken: CancellationToken) =
    let cancellation = CancellationTokenSource.CreateLinkedTokenSource sessionToken

    let cancellationResponseFlushed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable state = 0 // 0 running, 1 cancellation reserved, 2 completion reserved, 3 complete
    let mutable cancellationCommitted = 0

    let cancelAndRelease () =
        if Interlocked.CompareExchange(&cancellationCommitted, 1, 0) = 0 then
            try
                cancellation.Cancel()
            finally
                cancellationResponseFlushed.TrySetResult() |> ignore

    member _.Token = cancellation.Token
    member _.IsCancellationReserved = Volatile.Read(&state) = 1

    member _.TryReserveCancellation() =
        Interlocked.CompareExchange(&state, 1, 0) = 0

    member _.TryReserveCompletion() =
        Interlocked.CompareExchange(&state, 2, 0) = 0

    member _.WaitForCancellationResponseAsync() = cancellationResponseFlushed.Task
    member _.CommitCancellationAfterResponse() = cancelAndRelease ()

    member _.CancelForShutdown() =
        if Interlocked.CompareExchange(&state, 1, 0) = 0 || Volatile.Read(&state) = 1 then
            cancelAndRelease ()

    member _.Complete() =
        Volatile.Write(&state, 3)
        cancellation.Dispose()

module internal Pipe =
    let private openWorkspace target cancellationToken =
        task {
            let! outcome = SolutionStore.OpenAsync(target, cancellationToken)

            return
                match outcome with
                | WorkspaceOutcome.Success workspace -> Ok workspace
                | WorkspaceOutcome.Failure failure -> Error(PublicProtocol.failureError failure)
        }

    let private chunkNodes maximumFrameBytes descriptor operationId revision (nodes: WorkspaceNode array) =
        let chunks = ResizeArray<WorkspaceNode array>()

        let encodedSize offset count =
            let candidate =
                ArraySegment<WorkspaceNode>(nodes, offset, count) :> seq<WorkspaceNode>

            PublicProtocol.exportChunk descriptor operationId chunks.Count revision candidate false
            |> RpcCodec.encodeFrame
            |> _.Length

        if nodes.Length = 0 then
            chunks.Add Array.empty
        else
            let mutable offset = 0

            while offset < nodes.Length do
                let remaining = nodes.Length - offset
                let firstSize = encodedSize offset 1

                if firstSize > maximumFrameBytes then
                    raise (RpcOutboundFrameTooLargeException(maximumFrameBytes, firstSize))

                let mutable accepted = 1
                let mutable probe = 2

                while probe <= remaining && encodedSize offset probe <= maximumFrameBytes do
                    accepted <- probe
                    probe <- probe * 2

                let mutable low = accepted + 1
                let mutable high = min remaining (probe - 1)

                while low <= high do
                    let middle = low + (high - low) / 2

                    if encodedSize offset middle <= maximumFrameBytes then
                        accepted <- middle
                        low <- middle + 1
                    else
                        high <- middle - 1

                chunks.Add(nodes[offset .. offset + accepted - 1])
                offset <- offset + accepted

        chunks.ToArray()

    let private commandTarget (workspace: SolutionWorkspace) targetId =
        match targetId with
        | None -> Ok None
        | Some value when value = workspace.WorkspaceDescriptor.WorkspaceId.Value -> Ok None
        | Some value ->
            workspace.RootProjection.Nodes
            |> Seq.tryFind (fun node -> node.NodeId.Value = value)
            |> Option.map (fun node -> Ok(Some node.NodeId))
            |> Option.defaultValue (Error(RpcErrors.create "not_found" "The command target was not found." None))

    let private commandArguments (workspace: SolutionWorkspace) (descriptor: CommandDescriptor) (value: RpcValue) =
        try
            let fields = RpcValue.requireMap "arguments" value

            RpcValue.ensureOnly "arguments" (descriptor.ParameterDescriptors |> Seq.map _.ParameterId.Value) fields

            let solutionDirectory =
                Path.GetDirectoryName workspace.BackingPath.Value
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let arguments =
                descriptor.ParameterDescriptors
                |> Seq.choose (fun parameter ->
                    match RpcValue.optionalField parameter.ParameterId.Value fields with
                    | None when parameter.Required ->
                        invalidArg "arguments" $"Missing required argument '{parameter.ParameterId.Value}'."
                    | None -> None
                    | Some raw ->
                        let parsed =
                            match parameter.ParameterType with
                            | CommandParameterType.Text ->
                                CommandParameterValue.Text(RpcValue.requireString parameter.ParameterId.Value raw)
                            | CommandParameterType.Path ->
                                let path = RpcValue.requireString parameter.ParameterId.Value raw

                                CommandParameterValue.Path(
                                    WorkspaceArtifactPath.Create(Path.GetFullPath(path, solutionDirectory))
                                )
                            | CommandParameterType.Boolean ->
                                match raw with
                                | RpcValue.Boolean value -> CommandParameterValue.Boolean value
                                | _ -> invalidArg parameter.ParameterId.Value "Expected a boolean."
                            | CommandParameterType.NodeId ->
                                let nodeId = RpcValue.requireString parameter.ParameterId.Value raw

                                match
                                    workspace.RootProjection.Nodes
                                    |> Seq.tryFind (fun node -> node.NodeId.Value = nodeId)
                                with
                                | Some node -> CommandParameterValue.Node node.NodeId
                                | None -> invalidArg parameter.ParameterId.Value "The node argument was not found."
                            | CommandParameterType.Integer ->
                                CommandParameterValue.Integer(RpcValue.requireInteger parameter.ParameterId.Value raw)
                            | CommandParameterType.Choice ->
                                CommandParameterValue.Choice(
                                    RpcValue.requireString parameter.ParameterId.Value raw |> CommandChoiceId.Create
                                )
                            | _ -> invalidArg parameter.ParameterId.Value "Unsupported command parameter type."

                        Some
                            { ParameterId = parameter.ParameterId
                              Value = parsed })
                |> CommandArguments.Create

            Ok arguments
        with :? ArgumentException as error ->
            Error(RpcErrors.invalidParams error.Message)

    let private commandDescriptor commandId =
        try
            let id = CommandId.Create commandId

            SolutionPersistenceMutator.TryDescribe id
            |> Option.orElseWith (fun () -> ProjectMutations.tryDescribe id)
        with :? ArgumentException as error ->
            raise (ArgumentException(error.Message, "commandId"))

    type private PlannedMutation =
        | SolutionPlan of SolutionMutationPlan
        | ProjectPlan of ProjectMutationPlan

    let private plannedActions =
        function
        | SolutionPlan plan ->
            seq {
                match plan.FileRename with
                | Some rename -> yield MutationAction.Rename(rename.Source.Value, rename.Destination.Value)
                | None -> ()

                yield MutationAction.ReplaceFile(plan.BackingPath.Value, plan.Contents)
            }
        | ProjectPlan plan -> plan.Actions :> seq<MutationAction>

    let private plannedPaths =
        function
        | SolutionPlan plan ->
            seq {
                yield plan.BackingPath

                match plan.FileRename with
                | Some rename ->
                    yield rename.Source
                    yield rename.Destination
                | None -> ()
            }
        | ProjectPlan plan -> plan.Paths :> seq<WorkspaceArtifactPath>

    let private plannedRequest =
        function
        | SolutionPlan plan -> plan.Request
        | ProjectPlan plan -> plan.Request

    let private planMutation
        (workspace: SolutionWorkspace)
        (state: WorkspaceState)
        (request: CommandMutationRequest)
        cancellationToken
        =
        task {
            match SolutionPersistenceMutator.TryDescribe request.CommandId with
            | Some _ ->
                let! plan = SolutionPersistenceMutator.PlanAsync(workspace, request, cancellationToken)

                return
                    plan
                    |> function
                        | Success value -> Success(SolutionPlan value)
                        | Failure failure -> Failure failure
            | None ->
                match request.TargetId with
                | None ->
                    return
                        Failure(
                            NotFound(
                                "targetId",
                                WorkspaceDiagnostic.CreateSimple(
                                    WorkspaceDiagnosticSeverity.Error,
                                    WorkspaceDiagnosticCode.Create "not_found",
                                    "A project target is required.",
                                    false,
                                    CorrelationId.New()
                                )
                            )
                        )
                | Some target ->
                    let! project = state.ProjectAsync(target, cancellationToken)

                    match project with
                    | Failure failure -> return Failure failure
                    | Success(projectWorkspace, project, snapshot) ->
                        match ProjectMutations.plan projectWorkspace project snapshot request cancellationToken with
                        | Success value -> return Success(ProjectPlan value)
                        | Failure failure -> return Failure failure
        }

    let private mutationNotifications =
        function
        | WorkspaceInvalidationResult.Delta delta -> [ PublicProtocol.workspaceDelta delta ]
        | WorkspaceInvalidationResult.Reset reset -> [ PublicProtocol.workspaceReset reset ]
        | WorkspaceInvalidationResult.None -> []

    let isPipeInvocation (arguments: string array) =
        match arguments with
        | [| ("solution" | "sln"); target; "--pipe" |] -> Some target
        | _ -> None

    let runAsync
        (target: string)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            let! opened = openWorkspace target cancellationToken

            match opened with
            | Error rpcError ->
                do! error.WriteLineAsync($"dotnet-plus pipe startup failure: {rpcError.Message}")
                do! error.FlushAsync()
                return 64
            | Ok workspace ->
                let state = WorkspaceState.CreateProduction(target, workspace)
                let mutable watcherStarted = false
                let mutable maximumFrameBytes = RpcCodec.secureLimits.MaximumValueBytes
                let mutable maximumPageSize = 256
                use publicationGate = new SemaphoreSlim(1, 1)

                let watcher =
                    WorkspaceWatcher(state, 128, (fun () -> maximumFrameBytes), publicationGate)

                let coordinator =
                    let root =
                        Path.GetDirectoryName workspace.BackingPath.Value
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    MutationCoordinator.CreateProduction(
                        WorkspaceArtifactPath.Create root,
                        fun () -> WorkspaceRevision.Create state.Revision
                    )

                let prepareWatcher requestCancellationToken =
                    task {
                        watcher.Resume()
                        let! handoff = watcher.ActivateAsync requestCancellationToken

                        match handoff with
                        | WatcherHandoff.Complete -> return true
                        | WatcherHandoff.Revalidate _
                        | WatcherHandoff.RevalidateWorkspace ->
                            watcher.QueueActivationHandoff handoff
                            return true
                        | WatcherHandoff.Uncertain -> return false
                    }

                let rebuildWatcher requestCancellationToken =
                    task {
                        let notifications = ResizeArray<RpcFrame>()

                        let publish handoff =
                            match handoff with
                            | WorkspaceInvalidationResult.Delta delta ->
                                notifications.Add(PublicProtocol.workspaceDelta delta)
                            | WorkspaceInvalidationResult.Reset reset ->
                                notifications.Add(PublicProtocol.workspaceReset reset)
                            | WorkspaceInvalidationResult.None -> ()

                            Task.FromResult(())

                        let! _ = watcher.RebuildAndRevalidateAsync(publish, requestCancellationToken)
                        return notifications |> Seq.toList
                    }

                let activeExports =
                    ConcurrentDictionary<string, ExportOperationState>(StringComparer.Ordinal)

                let startWatcher active =
                    if watcherStarted then
                        None
                    elif not active then
                        None
                    else
                        watcherStarted <- true
                        Some(fun sink token -> watcher.StartAsync(sink, token))

                let initialize parameters _ =
                    task {
                        match PublicProtocol.parseInitialize parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            maximumFrameBytes <- request.MaximumFrameBytes
                            maximumPageSize <- request.MaximumPageSize
                            return Ok(PublicProtocol.initializeResult state.Descriptor state.Revision request)
                    }

                let dispatchCore (_: RpcSessionContext) methodName parameters requestCancellationToken =
                    task {
                        match PublicProtocol.parseRequest methodName parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            let! commandWorkspace =
                                match request with
                                | PublicRequest.CommandList _
                                | PublicRequest.CommandDescribe _
                                | PublicRequest.CommandPreview _
                                | PublicRequest.CommandExecute _ ->
                                    task {
                                        let! workspace = state.WorkspaceAsync requestCancellationToken
                                        return workspace |> Result.map Some
                                    }
                                | _ -> Task.FromResult(Ok None)

                            let commandWorkspaceError =
                                match commandWorkspace with
                                | Error rpcError -> Some rpcError
                                | Ok _ -> None

                            let commandWorkspace = Result.defaultValue None commandWorkspace

                            match request with
                            | PublicRequest.Root ->
                                let! active = prepareWatcher requestCancellationToken

                                if not active then
                                    return Error RpcErrors.internalError
                                else
                                    let! rooted = state.RootAsync requestCancellationToken

                                    match rooted with
                                    | Error rpcError -> return Error rpcError
                                    | Ok(revision, nodes) ->
                                        return
                                            Ok
                                                { Result = PublicProtocol.rootResult state.Descriptor revision nodes
                                                  Notifications = []
                                                  BackgroundWork = startWatcher true
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                            | PublicRequest.Children(parentId, pageSize, continuation) ->
                                let! active = prepareWatcher requestCancellationToken

                                if not active then
                                    return Error RpcErrors.internalError
                                else
                                    let! page =
                                        state.ChildrenAsync(
                                            parentId,
                                            pageSize,
                                            maximumPageSize,
                                            continuation,
                                            requestCancellationToken
                                        )

                                    match page with
                                    | Error rpcError -> return Error rpcError
                                    | Ok result ->
                                        let! stateNotifications, reset =
                                            match result.Delta |> Option.map PublicProtocol.workspaceDelta with
                                            | Some notification when
                                                (RpcCodec.encodeFrame notification).Length > maximumFrameBytes
                                                ->
                                                task {
                                                    let diagnostic =
                                                        WorkspaceDiagnostic.CreateSimple(
                                                            WorkspaceDiagnosticSeverity.Warning,
                                                            WorkspaceDiagnosticCode.Create "workspace.delta_pressure",
                                                            "The verified delta exceeded delivery capacity; request a fresh workspace graph.",
                                                            true,
                                                            CorrelationId.New()
                                                        )

                                                    let! reset = state.ResetAsync(diagnostic, requestCancellationToken)

                                                    return [ PublicProtocol.workspaceReset reset ], true
                                                }
                                            | Some notification -> Task.FromResult([ notification ], false)
                                            | None -> Task.FromResult([], false)

                                        if reset then
                                            watcher.Pause()

                                        let! handoffNotifications =
                                            if reset then
                                                Task.FromResult []
                                            else
                                                rebuildWatcher requestCancellationToken

                                        return
                                            Ok
                                                { Result =
                                                    PublicProtocol.childrenResult
                                                        state.Descriptor
                                                        result.Revision
                                                        result.ParentId
                                                        result.Nodes
                                                        result.NextToken
                                                  Notifications = stateNotifications @ handoffNotifications
                                                  BackgroundWork = if reset then None else startWatcher true
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                            | PublicRequest.Refresh expectedRevision ->
                                let! active = prepareWatcher requestCancellationToken

                                if not active then
                                    return Error RpcErrors.internalError
                                else
                                    let! refreshed = state.RefreshAsync(expectedRevision, requestCancellationToken)

                                    match refreshed with
                                    | Error rpcError -> return Error rpcError
                                    | Ok result ->
                                        let! effective =
                                            match result.Delta |> Option.map PublicProtocol.workspaceDelta with
                                            | Some notification when
                                                (RpcCodec.encodeFrame notification).Length > maximumFrameBytes
                                                ->
                                                task {
                                                    let diagnostic =
                                                        WorkspaceDiagnostic.CreateSimple(
                                                            WorkspaceDiagnosticSeverity.Warning,
                                                            WorkspaceDiagnosticCode.Create "workspace.delta_pressure",
                                                            "The verified delta exceeded delivery capacity; request a fresh workspace graph.",
                                                            true,
                                                            CorrelationId.New()
                                                        )

                                                    let! reset = state.ResetAsync(diagnostic, requestCancellationToken)

                                                    return
                                                        { Revision = reset.Revision.Value
                                                          Reset = true
                                                          Delta = None
                                                          ResetEvent = Some reset
                                                          Diagnostics = reset.Diagnostics }
                                                }
                                            | _ -> Task.FromResult result

                                        let stateNotifications =
                                            [ effective.Delta |> Option.map PublicProtocol.workspaceDelta
                                              effective.ResetEvent |> Option.map PublicProtocol.workspaceReset ]
                                            |> List.choose id

                                        if effective.Reset then
                                            watcher.Pause()

                                        let! handoffNotifications =
                                            if effective.Reset then
                                                Task.FromResult []
                                            else
                                                rebuildWatcher requestCancellationToken

                                        return
                                            Ok
                                                { Result =
                                                    PublicProtocol.refreshResult effective.Revision effective.Reset
                                                  Notifications = stateNotifications @ handoffNotifications
                                                  BackgroundWork = None
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                            | PublicRequest.Export ->
                                let snapshotRevision = state.Revision
                                let descriptor = state.Descriptor
                                let operationId = Guid.NewGuid().ToString("N")
                                let operation = ExportOperationState(requestCancellationToken)

                                if not (activeExports.TryAdd(operationId, operation)) then
                                    operation.Complete()
                                    return Error RpcErrors.internalError
                                else
                                    let background (sink: RpcNotificationSink) sessionToken =
                                        task {
                                            let mutable sequence = 0
                                            let mutable outcome = PublicOperationOutcome.Succeeded

                                            let reserveFailure failure =
                                                task {
                                                    if operation.TryReserveCompletion() then
                                                        outcome <- failure
                                                    else
                                                        do! operation.WaitForCancellationResponseAsync()
                                                        outcome <- PublicOperationOutcome.Cancelled
                                                }

                                            try
                                                try
                                                    use linked =
                                                        CancellationTokenSource.CreateLinkedTokenSource(
                                                            operation.Token,
                                                            sessionToken
                                                        )

                                                    let! exported = state.ExportAsync(snapshotRevision, linked.Token)

                                                    match exported with
                                                    | Error rpcError when rpcError.Code = "cancelled" ->
                                                        raise (OperationCanceledException())
                                                    | Error rpcError ->
                                                        do!
                                                            reserveFailure (
                                                                PublicOperationOutcome.Failed(
                                                                    rpcError.Code,
                                                                    rpcError.Message
                                                                )
                                                            )
                                                    | Ok snapshot ->
                                                        let chunks =
                                                            chunkNodes
                                                                maximumFrameBytes
                                                                snapshot.Descriptor
                                                                operationId
                                                                snapshot.Revision
                                                                (snapshot.Nodes |> Seq.toArray)

                                                        for index in 0 .. chunks.Length - 1 do
                                                            if operation.IsCancellationReserved then
                                                                raise (OperationCanceledException())

                                                            linked.Token.ThrowIfCancellationRequested()

                                                            do!
                                                                sink.WriteAsync(
                                                                    PublicProtocol.exportChunk
                                                                        snapshot.Descriptor
                                                                        operationId
                                                                        sequence
                                                                        snapshot.Revision
                                                                        chunks[index]
                                                                        (index = chunks.Length - 1)
                                                                )

                                                            sequence <- sequence + 1

                                                        if operation.TryReserveCompletion() then
                                                            outcome <- PublicOperationOutcome.Succeeded
                                                        else
                                                            do! operation.WaitForCancellationResponseAsync()
                                                            outcome <- PublicOperationOutcome.Cancelled
                                                with
                                                | :? OperationCanceledException ->
                                                    if operation.IsCancellationReserved then
                                                        do! operation.WaitForCancellationResponseAsync()

                                                    outcome <- PublicOperationOutcome.Cancelled
                                                | :? RpcOutboundFrameTooLargeException ->
                                                    do!
                                                        reserveFailure (
                                                            PublicOperationOutcome.Failed(
                                                                "response_too_large",
                                                                "The workspace export exceeded the negotiated outbound frame limit."
                                                            )
                                                        )
                                                | :? InvalidOperationException ->
                                                    do!
                                                        reserveFailure (
                                                            PublicOperationOutcome.Failed(
                                                                "export_failed",
                                                                "The workspace export could not be completed safely."
                                                            )
                                                        )
                                                | :? ArgumentException
                                                | :? FormatException ->
                                                    do!
                                                        reserveFailure (
                                                            PublicOperationOutcome.Failed(
                                                                "export_failed",
                                                                "The workspace export could not be completed safely."
                                                            )
                                                        )

                                                do!
                                                    sink.WriteAsync(
                                                        PublicProtocol.operationCompleted
                                                            descriptor
                                                            operationId
                                                            sequence
                                                            snapshotRevision
                                                            outcome
                                                    )
                                            finally
                                                activeExports.TryRemove operationId |> ignore
                                                operation.Complete()
                                        }

                                    return
                                        Ok
                                            { Result = PublicProtocol.exportResult operationId snapshotRevision
                                              Notifications = []
                                              BackgroundWork = Some background
                                              AfterResponse = None
                                              StopAfterResponse = false }
                            | PublicRequest.Cancel operationId ->
                                let accepted, afterResponse =
                                    match activeExports.TryGetValue operationId with
                                    | true, operation when operation.TryReserveCancellation() ->
                                        true, Some operation.CommitCancellationAfterResponse
                                    | _ -> false, None

                                return
                                    Ok
                                        { Result = PublicProtocol.cancelResult accepted
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = afterResponse
                                          StopAfterResponse = false }
                            | PublicRequest.Shutdown ->
                                for operation in activeExports.Values do
                                    operation.CancelForShutdown()

                                return
                                    Ok
                                        { Result = PublicProtocol.shutdownResult
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = None
                                          StopAfterResponse = true }
                            | PublicRequest.CommandList _
                            | PublicRequest.CommandDescribe _
                            | PublicRequest.CommandPreview _
                            | PublicRequest.CommandExecute _ when commandWorkspaceError.IsSome ->
                                return Error commandWorkspaceError.Value
                            | PublicRequest.CommandList targetId ->
                                match commandTarget commandWorkspace.Value targetId with
                                | Error rpcError -> return Error rpcError
                                | Ok target ->
                                    return
                                        Ok
                                            { Result =
                                                Seq.append
                                                    (SolutionPersistenceMutator.Discover(commandWorkspace.Value, target))
                                                    (ProjectMutations.discover commandWorkspace.Value target)
                                                |> PublicProtocol.commandListResult
                                              Notifications = []
                                              BackgroundWork = None
                                              AfterResponse = None
                                              StopAfterResponse = false }
                            | PublicRequest.CommandDescribe(commandId, targetId) ->
                                if state.Descriptor.IsReadOnly then
                                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                                else
                                    match
                                        commandTarget commandWorkspace.Value targetId, commandDescriptor commandId
                                    with
                                    | Error rpcError, _ -> return Error rpcError
                                    | _, None ->
                                        return Error(RpcErrors.create "not_found" "The command was not found." None)
                                    | Ok target, Some descriptor when
                                        (Seq.append
                                            (SolutionPersistenceMutator.Discover(commandWorkspace.Value, target))
                                            (ProjectMutations.discover commandWorkspace.Value target)
                                         |> Seq.exists (fun candidate -> candidate.CommandId = descriptor.CommandId))
                                        ->
                                        return
                                            Ok
                                                { Result = PublicProtocol.commandDescribeResult descriptor
                                                  Notifications = []
                                                  BackgroundWork = None
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                                    | _ ->
                                        return
                                            Error(
                                                RpcErrors.create
                                                    "not_found"
                                                    "The command is not applicable to the target."
                                                    None
                                            )
                            | PublicRequest.CommandPreview(commandId, targetId, arguments, expectedRevision) ->
                                if state.Descriptor.IsReadOnly then
                                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                                else
                                    match
                                        commandTarget commandWorkspace.Value targetId, commandDescriptor commandId
                                    with
                                    | Error rpcError, _ -> return Error rpcError
                                    | _, None ->
                                        return Error(RpcErrors.create "not_found" "The command was not found." None)
                                    | Ok target, Some descriptor ->
                                        match commandArguments commandWorkspace.Value descriptor arguments with
                                        | Error rpcError -> return Error rpcError
                                        | Ok parsed ->
                                            let request =
                                                { CommandId = descriptor.CommandId
                                                  TargetId = target
                                                  Arguments = parsed
                                                  ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                                            let! planned =
                                                planMutation
                                                    commandWorkspace.Value
                                                    state
                                                    request
                                                    requestCancellationToken

                                            match planned with
                                            | WorkspaceOutcome.Failure failure ->
                                                return Error(PublicProtocol.failureError failure)
                                            | WorkspaceOutcome.Success plan ->
                                                match coordinator.Prepare(plannedRequest plan, plannedActions plan) with
                                                | WorkspaceOutcome.Failure failure ->
                                                    return Error(PublicProtocol.failureError failure)
                                                | WorkspaceOutcome.Success preview ->
                                                    return
                                                        Ok
                                                            { Result = PublicProtocol.commandPreviewResult preview
                                                              Notifications = []
                                                              BackgroundWork = None
                                                              AfterResponse = None
                                                              StopAfterResponse = false }
                            | PublicRequest.CommandExecute(commandId, targetId, arguments, expectedRevision, previewId) ->
                                if state.Descriptor.IsReadOnly then
                                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                                else
                                    match
                                        commandTarget commandWorkspace.Value targetId,
                                        commandDescriptor commandId,
                                        previewId
                                    with
                                    | Error rpcError, _, _ -> return Error rpcError
                                    | _, None, _ ->
                                        return Error(RpcErrors.create "not_found" "The command was not found." None)
                                    | _, _, None ->
                                        return Error(RpcErrors.invalidParams "command/execute requires previewId.")
                                    | Ok target, Some descriptor, Some previewId ->
                                        match commandArguments commandWorkspace.Value descriptor arguments with
                                        | Error rpcError -> return Error rpcError
                                        | Ok parsed ->
                                            let request =
                                                { CommandId = descriptor.CommandId
                                                  TargetId = target
                                                  Arguments = parsed
                                                  ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                                            let! planned =
                                                planMutation
                                                    commandWorkspace.Value
                                                    state
                                                    request
                                                    requestCancellationToken

                                            match planned with
                                            | WorkspaceOutcome.Failure failure ->
                                                return Error(PublicProtocol.failureError failure)
                                            | WorkspaceOutcome.Success plan ->
                                                let token = MutationConfirmationToken.Create previewId

                                                match
                                                    coordinator.Execute(
                                                        plannedRequest plan,
                                                        plannedActions plan,
                                                        token,
                                                        requestCancellationToken
                                                    )
                                                with
                                                | WorkspaceOutcome.Failure failure ->
                                                    return Error(PublicProtocol.failureError failure)
                                                | WorkspaceOutcome.Success(MutationApplyResult.RolledBack failure) ->
                                                    return Error(PublicProtocol.failureError failure)
                                                | WorkspaceOutcome.Success MutationApplyResult.Applied ->
                                                    let! invalidated =
                                                        state.InvalidateFromTransactionAsync(
                                                            plannedPaths plan,
                                                            CancellationToken.None
                                                        )

                                                    let reset =
                                                        match invalidated with
                                                        | WorkspaceInvalidationResult.Reset _ -> true
                                                        | _ -> false

                                                    if reset then
                                                        watcher.Pause()

                                                    let! watcherNotifications =
                                                        if reset then
                                                            Task.FromResult []
                                                        else
                                                            rebuildWatcher CancellationToken.None

                                                    return
                                                        Ok
                                                            { Result =
                                                                PublicProtocol.commandExecuteResult state.Revision
                                                              Notifications =
                                                                mutationNotifications invalidated @ watcherNotifications
                                                              BackgroundWork = None
                                                              AfterResponse = None
                                                              StopAfterResponse = false }
                    }

                let dispatch
                    (context: RpcSessionContext)
                    (methodName: string)
                    (parameters: RpcValue)
                    (requestCancellationToken: CancellationToken)
                    =
                    task {
                        let serialized =
                            match PublicProtocol.parseRequest methodName parameters with
                            | Ok PublicRequest.Root
                            | Ok(PublicRequest.Children _)
                            | Ok(PublicRequest.Refresh _)
                            | Ok(PublicRequest.CommandList _)
                            | Ok(PublicRequest.CommandDescribe _)
                            | Ok(PublicRequest.CommandPreview _)
                            | Ok(PublicRequest.CommandExecute _) -> true
                            | _ -> false

                        if serialized then
                            do! publicationGate.WaitAsync requestCancellationToken

                        try
                            let! result = dispatchCore context methodName parameters requestCancellationToken

                            return
                                match result with
                                | Ok value when serialized ->
                                    Ok
                                        { value with
                                            AfterResponse = Some(fun () -> publicationGate.Release() |> ignore) }
                                | _ ->
                                    if serialized then
                                        publicationGate.Release() |> ignore

                                    result
                        with exceptionValue ->
                            if serialized then
                                publicationGate.Release() |> ignore

                            return raise exceptionValue
                    }

                let configuration =
                    { Profile = RpcProfile.publicProfile
                      Limits = RpcCodec.secureLimits
                      GetOutboundFrameLimit = fun () -> maximumFrameBytes
                      Initialize = initialize
                      Dispatch = dispatch }

                try
                    let! result = RpcSession.runAsync configuration input output error cancellationToken
                    do! state.DisposeAsync()
                    return result
                with exceptionValue ->
                    do! state.DisposeAsync()
                    return raise exceptionValue
        }
