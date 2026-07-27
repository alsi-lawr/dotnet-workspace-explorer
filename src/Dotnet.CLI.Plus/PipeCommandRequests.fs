namespace Dotnet.CLI.Plus

#nowarn "3511"

open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport
open CanonicalMutationPlanning
open PipeCommandProtocol

module internal PipeCommandRequests =
    let private isCanonicalPlan =
        function
        | CanonicalPlan _ -> true
        | _ -> false

    let private availableCommands workspace target =
        Seq.append
            (SolutionPersistenceMutator.Discover(workspace, target))
            (ProjectMutations.discover workspace target)
        |> Seq.append (CanonicalCommands.discover workspace target)
        |> Seq.append (LifecycleCommands.discover workspace target)
        |> Seq.append (LaunchProfileCommandPlanning.discover workspace target)

    let private dispatchResolved
        (context: CommandRequestContext)
        workspace
        request
        (requestCancellationToken: CancellationToken)
        =
        task {
            match request with
            | PublicRequest.CommandList targetId ->
                match commandTarget workspace targetId with
                | Error rpcError -> return Error rpcError
                | Ok target ->
                    return
                        Ok
                            { Result =
                                availableCommands workspace target
                                |> PublicProtocol.commandListResult
                              Notifications = []
                              BackgroundWork = None
                              AfterResponse = None
                              StopAfterResponse = false }
            | PublicRequest.CommandDescribe(commandId, targetId) ->
                match commandTarget workspace targetId, commandDescriptor commandId with
                | Error rpcError, _ -> return Error rpcError
                | _, None ->
                    return Error(RpcErrors.create "not_found" "The command was not found." None)
                | Ok target, Some descriptor when
                    availableCommands workspace target
                    |> Seq.exists (fun candidate -> candidate.CommandId = descriptor.CommandId)
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
                if context.State.Descriptor.IsReadOnly then
                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                else
                    match commandTarget workspace targetId, commandDescriptor commandId with
                    | Error rpcError, _ -> return Error rpcError
                    | _, None ->
                        return Error(RpcErrors.create "not_found" "The command was not found." None)
                    | Ok target, Some descriptor ->
                        match commandArguments workspace descriptor arguments with
                        | Error rpcError -> return Error rpcError
                        | Ok parsed ->
                            let mutationRequest =
                                { CommandId = descriptor.CommandId
                                  TargetId = target
                                  Arguments = parsed
                                  ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                            let! planned =
                                planMutation
                                    workspace
                                    context.State
                                    mutationRequest
                                    requestCancellationToken

                            match planned with
                            | Failure failure -> return Error(PublicProtocol.failureError failure)
                            | Success plan ->
                                match
                                    context.Coordinator.Prepare(
                                        plannedRequest plan,
                                        plannedActions plan
                                    )
                                with
                                | Failure failure ->
                                    return Error(PublicProtocol.failureError failure)
                                | Success preview ->
                                    return
                                        Ok
                                            { Result = PublicProtocol.commandPreviewResult preview
                                              Notifications = []
                                              BackgroundWork = None
                                              AfterResponse = None
                                              StopAfterResponse = false }
            | PublicRequest.CommandExecute(commandId,
                                           targetId,
                                           arguments,
                                           expectedRevision,
                                           previewId) ->
                let! lifecycle =
                    match commandTarget workspace targetId, commandDescriptor commandId with
                    | Ok target, Some descriptor ->
                        PipeLifecycleRequests.tryExecute
                            context
                            workspace
                            target
                            descriptor
                            arguments
                            expectedRevision
                            previewId
                            requestCancellationToken
                    | _ -> Task.FromResult None

                match lifecycle with
                | Some result -> return result
                | None ->
                    match
                        commandTarget workspace targetId, commandDescriptor commandId, previewId
                    with
                    | Error rpcError, _, _ -> return Error rpcError
                    | _, None, _ ->
                        return Error(RpcErrors.create "not_found" "The command was not found." None)
                    | _, Some descriptor, _ when
                        context.State.Descriptor.IsReadOnly
                        && descriptor.CommandAccess = CommandAccess.Write
                        ->
                        return
                            Error(
                                RpcErrors.unsupported "The selected .slnf workspace is read-only."
                            )
                    | Ok target, Some descriptor, previewId when
                        CanonicalCommands.tryDescribe descriptor.CommandId |> Option.isSome
                        && (not (CanonicalCommands.isMutation descriptor.CommandId.Value)
                            && descriptor.CommandAccess <> CommandAccess.Write
                            || previewId.IsSome)
                        ->
                        if context.State.Revision <> expectedRevision then
                            return Error(PublicProtocol.workspaceConflict context.State.Revision)
                        else
                            match commandArguments workspace descriptor arguments with
                            | Error rpcError -> return Error rpcError
                            | Ok parsed ->
                                let mutationRequest =
                                    { CommandId = descriptor.CommandId
                                      TargetId = target
                                      Arguments = parsed
                                      ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                                let! authorized =
                                    task {
                                        if
                                            CanonicalCommands.isMutation descriptor.CommandId.Value
                                            || descriptor.CommandAccess = CommandAccess.Write
                                        then
                                            let! planned =
                                                planMutation
                                                    workspace
                                                    context.State
                                                    mutationRequest
                                                    requestCancellationToken

                                            match planned, previewId with
                                            | Failure failure, _ ->
                                                return Error(PublicProtocol.failureError failure)
                                            | Success plan, Some _ when isCanonicalPlan plan ->
                                                return Ok(Some plan)
                                            | Success plan, Some value ->
                                                let execution =
                                                    context.Coordinator.Execute(
                                                        plannedRequest plan,
                                                        plannedActions plan,
                                                        MutationConfirmationToken.Create value,
                                                        requestCancellationToken
                                                    )

                                                match execution with
                                                | Failure failure ->
                                                    return
                                                        Error(PublicProtocol.failureError failure)
                                                | Success result ->
                                                    match result with
                                                    | RolledBack failure ->
                                                        return
                                                            Error(
                                                                PublicProtocol.failureError failure
                                                            )
                                                    | Applied -> return Ok None
                                            | _, None ->
                                                return
                                                    Error(
                                                        RpcErrors.invalidParams
                                                            "command/execute requires previewId."
                                                    )
                                        else
                                            return Ok None
                                    }

                                match authorized with
                                | Error rpcError -> return Error rpcError
                                | Ok canonicalPlan ->
                                    let argv = CanonicalCommands.argv workspace mutationRequest

                                    match argv with
                                    | Error message -> return Error(RpcErrors.invalidParams message)
                                    | Ok argv ->
                                        let mutationNotifications = context.MutationNotifications

                                        return!
                                            CanonicalCommandOperation.start
                                                { Workspace = workspace
                                                  State = context.State
                                                  Watcher = context.Watcher
                                                  Coordinator = context.Coordinator
                                                  PublicationGate = context.PublicationGate
                                                  ActiveOperations = context.ActiveOperations
                                                  WorkspaceRoot = context.WorkspaceRoot
                                                  MaximumFrameBytes = context.MaximumFrameBytes
                                                  RebuildWatcher = context.RebuildWatcher
                                                  MutationNotifications = mutationNotifications }
                                                descriptor
                                                mutationRequest
                                                canonicalPlan
                                                previewId
                                                argv
                                                requestCancellationToken
                    | Ok target, Some descriptor, Some previewId when
                        descriptor.CommandId.Value = "project.physical-move"
                        ->
                        match commandArguments workspace descriptor arguments with
                        | Error rpcError -> return Error rpcError
                        | Ok parsed ->
                            let mutationRequest =
                                { CommandId = descriptor.CommandId
                                  TargetId = target
                                  Arguments = parsed
                                  ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                            let! planned =
                                planMutation
                                    workspace
                                    context.State
                                    mutationRequest
                                    requestCancellationToken

                            match planned with
                            | Failure failure -> return Error(PublicProtocol.failureError failure)
                            | Success(CompositePlan plan) ->
                                return!
                                    PlannedMutationOperation.Start(
                                        { Workspace = workspace
                                          State = context.State
                                          Watcher = context.Watcher
                                          Coordinator = context.Coordinator
                                          PublicationGate = context.PublicationGate
                                          ActiveOperations = context.ActiveOperations
                                          WorkspaceRoot = context.WorkspaceRoot
                                          MaximumFrameBytes = context.MaximumFrameBytes
                                          RebuildWatcher = context.RebuildWatcher
                                          MutationNotifications = context.MutationNotifications },
                                        CompositePlan plan,
                                        previewId,
                                        "Starting project relocation.",
                                        (fun () -> Ok()),
                                        requestCancellationToken
                                    )
                            | Success _ -> return Error RpcErrors.internalError
                    | _, _, None ->
                        return Error(RpcErrors.invalidParams "command/execute requires previewId.")
                    | Ok target, Some descriptor, Some previewId ->
                        match commandArguments workspace descriptor arguments with
                        | Error rpcError -> return Error rpcError
                        | Ok parsed ->
                            let mutationRequest =
                                { CommandId = descriptor.CommandId
                                  TargetId = target
                                  Arguments = parsed
                                  ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                            let! planned =
                                planMutation
                                    workspace
                                    context.State
                                    mutationRequest
                                    requestCancellationToken

                            match planned with
                            | Failure failure -> return Error(PublicProtocol.failureError failure)
                            | Success plan ->
                                let token = MutationConfirmationToken.Create previewId

                                match
                                    context.Coordinator.Execute(
                                        plannedRequest plan,
                                        plannedActions plan,
                                        token,
                                        requestCancellationToken
                                    )
                                with
                                | Failure failure ->
                                    return Error(PublicProtocol.failureError failure)
                                | Success(RolledBack failure) ->
                                    return Error(PublicProtocol.failureError failure)
                                | Success Applied ->
                                    let! invalidated =
                                        context.State.InvalidateFromTransactionAsync(
                                            plannedPaths plan,
                                            CancellationToken.None
                                        )

                                    let reset =
                                        match invalidated with
                                        | WorkspaceInvalidationResult.Reset _ -> true
                                        | _ -> false

                                    if reset then
                                        context.Watcher.Pause()

                                    let! watcherNotifications =
                                        if reset then
                                            Task.FromResult []
                                        else
                                            context.RebuildWatcher CancellationToken.None

                                    return
                                        Ok
                                            { Result =
                                                PublicProtocol.commandExecuteResult
                                                    context.State.Revision
                                              Notifications =
                                                context.MutationNotifications invalidated
                                                @ watcherNotifications
                                              BackgroundWork = None
                                              AfterResponse = None
                                              StopAfterResponse = false }
            | _ -> return invalidArg "request" "A command request is required."
        }

    let dispatch
        (context: CommandRequestContext)
        (request: PublicRequest)
        (requestCancellationToken: CancellationToken)
        : Task<Result<RpcDispatchResult, RpcError>> =
        task {
            match request with
            | PublicRequest.CommandList _
            | PublicRequest.CommandDescribe _
            | PublicRequest.CommandPreview _
            | PublicRequest.CommandExecute _ ->
                let! workspace = context.State.WorkspaceAsync requestCancellationToken

                match workspace with
                | Error rpcError -> return Error rpcError
                | Ok workspace ->
                    return! dispatchResolved context workspace request requestCancellationToken
            | _ -> return invalidArg "request" "A command request is required."
        }
