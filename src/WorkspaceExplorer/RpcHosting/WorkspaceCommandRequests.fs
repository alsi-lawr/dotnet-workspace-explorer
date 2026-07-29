namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceCommands

#nowarn "3511"

open System.Threading
open System.Threading.Tasks
open WorkspaceCommandEditing
open WorkspaceCommandArguments

module internal WorkspaceCommandRequests =
    let private isDotnetCommandPlan =
        function
        | DotnetCommandPlan _ -> true
        | _ -> false

    let private availableCommands workspace target =
        Seq.append
            (SolutionEditor.Discover(workspace, target))
            (ProjectEditing.discover workspace target)
        |> Seq.append (DotnetCommandCatalog.discover workspace target)
        |> Seq.append (DotnetLifecycleCommands.discover workspace target)
        |> Seq.append (SolutionLaunchProfileCommands.discover workspace target)

    let private dispatchResolved
        (context: WorkspaceCommandContext)
        workspace
        request
        (requestCancellationToken: CancellationToken)
        =
        task {
            match request with
            | WorkspaceRpcRequest.CommandList targetNodeId ->
                match commandTarget workspace targetNodeId with
                | Error rpcError -> return Error rpcError
                | Ok target ->
                    return
                        Ok
                            { Result =
                                availableCommands workspace target
                                |> WorkspaceRpcResponses.commandListResult
                              Notifications = []
                              BackgroundWork = None
                              AfterResponse = None
                              StopAfterResponse = false }
            | WorkspaceRpcRequest.CommandDescribe(commandId, targetNodeId) ->
                match
                    commandTarget workspace targetNodeId,
                    WorkspaceCommandCatalog.tryDescribe commandId
                with
                | Error rpcError, _ -> return Error rpcError
                | _, None ->
                    return Error(RpcErrors.create "not_found" "The command was not found." None)
                | Ok target, Some descriptor when
                    availableCommands workspace target
                    |> Seq.exists (fun candidate -> candidate.Id = descriptor.Id)
                    ->
                    return
                        Ok
                            { Result = WorkspaceRpcResponses.commandDescribeResult descriptor
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
            | WorkspaceRpcRequest.CommandPreview(commandId,
                                                 targetNodeId,
                                                 arguments,
                                                 expectedRevision) ->
                if context.State.Descriptor.IsReadOnly then
                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                else
                    match
                        commandTarget workspace targetNodeId,
                        WorkspaceCommandCatalog.tryDescribe commandId
                    with
                    | Error rpcError, _ -> return Error rpcError
                    | _, None ->
                        return Error(RpcErrors.create "not_found" "The command was not found." None)
                    | Ok target, Some descriptor ->
                        match commandArguments workspace descriptor arguments with
                        | Error rpcError -> return Error rpcError
                        | Ok parsed ->
                            let mutationRequest =
                                { CommandId = descriptor.Id
                                  TargetWorkspaceNodeId = target
                                  Arguments = parsed
                                  ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                            let! planned =
                                planMutation
                                    workspace
                                    context.State
                                    mutationRequest
                                    requestCancellationToken

                            match planned with
                            | Failure failure ->
                                return Error(WorkspaceRpcResponses.failureError failure)
                            | Success plan ->
                                match
                                    context.Coordinator.Prepare(
                                        plannedRequest plan,
                                        plannedActions plan
                                    )
                                with
                                | Failure failure ->
                                    return Error(WorkspaceRpcResponses.failureError failure)
                                | Success preview ->
                                    return
                                        Ok
                                            { Result =
                                                WorkspaceRpcResponses.commandPreviewResult preview
                                              Notifications = []
                                              BackgroundWork = None
                                              AfterResponse = None
                                              StopAfterResponse = false }
            | WorkspaceRpcRequest.CommandExecute(commandId,
                                                 targetNodeId,
                                                 arguments,
                                                 expectedRevision,
                                                 confirmationToken) ->
                let! lifecycle =
                    match
                        commandTarget workspace targetNodeId,
                        WorkspaceCommandCatalog.tryDescribe commandId
                    with
                    | Ok target, Some descriptor ->
                        DotnetLifecycleRequests.tryExecute
                            context
                            workspace
                            target
                            descriptor
                            arguments
                            expectedRevision
                            confirmationToken
                            requestCancellationToken
                    | _ -> Task.FromResult None

                match lifecycle with
                | Some result -> return result
                | None ->
                    let! profile =
                        match
                            commandTarget workspace targetNodeId,
                            WorkspaceCommandCatalog.tryDescribe commandId
                        with
                        | Ok target, Some descriptor ->
                            SolutionLaunchProfileRequests.tryExecute
                                context
                                workspace
                                target
                                descriptor
                                arguments
                                expectedRevision
                                confirmationToken
                                requestCancellationToken
                        | _ -> Task.FromResult None

                    match profile with
                    | Some result -> return result
                    | None ->
                        match
                            commandTarget workspace targetNodeId,
                            WorkspaceCommandCatalog.tryDescribe commandId,
                            confirmationToken
                        with
                        | Error rpcError, _, _ -> return Error rpcError
                        | _, None, _ ->
                            return
                                Error(
                                    RpcErrors.create "not_found" "The command was not found." None
                                )
                        | _, Some descriptor, _ when
                            context.State.Descriptor.IsReadOnly
                            && descriptor.Access = CommandAccess.Write
                            ->
                            return
                                Error(
                                    RpcErrors.unsupported
                                        "The selected .slnf workspace is read-only."
                                )
                        | Ok target, Some descriptor, confirmationToken when
                            DotnetCommandCatalog.tryDescribe descriptor.Id |> Option.isSome
                            && (not (DotnetCommandCatalog.isMutation descriptor.Id.Value)
                                && descriptor.Access <> CommandAccess.Write
                                || confirmationToken.IsSome)
                            ->
                            if context.State.Revision <> expectedRevision then
                                return
                                    Error(
                                        WorkspaceRpcResponses.workspaceConflict
                                            context.State.Revision
                                    )
                            else
                                match commandArguments workspace descriptor arguments with
                                | Error rpcError -> return Error rpcError
                                | Ok parsed ->
                                    let mutationRequest =
                                        { CommandId = descriptor.Id
                                          TargetWorkspaceNodeId = target
                                          Arguments = parsed
                                          ExpectedRevision =
                                            WorkspaceRevision.Create expectedRevision }

                                    let! authorized =
                                        task {
                                            if
                                                DotnetCommandCatalog.isMutation descriptor.Id.Value
                                                || descriptor.Access = CommandAccess.Write
                                            then
                                                let! planned =
                                                    planMutation
                                                        workspace
                                                        context.State
                                                        mutationRequest
                                                        requestCancellationToken

                                                match planned, confirmationToken with
                                                | Failure failure, _ ->
                                                    return
                                                        Error(
                                                            WorkspaceRpcResponses.failureError
                                                                failure
                                                        )
                                                | Success plan, Some _ when isDotnetCommandPlan plan ->
                                                    return Ok(Some plan)
                                                | Success plan, Some value ->
                                                    let execution =
                                                        context.Coordinator.Execute(
                                                            plannedRequest plan,
                                                            plannedActions plan,
                                                            WorkspaceEditConfirmation.Create value,
                                                            requestCancellationToken
                                                        )

                                                    match execution with
                                                    | Failure failure ->
                                                        return
                                                            Error(
                                                                WorkspaceRpcResponses.failureError
                                                                    failure
                                                            )
                                                    | Success result ->
                                                        match result with
                                                        | RolledBack failure ->
                                                            return
                                                                Error(
                                                                    WorkspaceRpcResponses.failureError
                                                                        failure
                                                                )
                                                        | Applied -> return Ok None
                                                | _, None ->
                                                    return
                                                        Error(
                                                            RpcErrors.invalidParams
                                                                "workspace/commands/execute requires confirmationToken."
                                                        )
                                            else
                                                return Ok None
                                        }

                                    match authorized with
                                    | Error rpcError -> return Error rpcError
                                    | Ok plannedCommand ->
                                        let argv =
                                            DotnetCommandCatalog.argv workspace mutationRequest

                                        match argv with
                                        | Error message ->
                                            return Error(RpcErrors.invalidParams message)
                                        | Ok argv ->
                                            let mutationNotifications =
                                                context.MutationNotifications

                                            return!
                                                DotnetCommandOperation.start
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
                                                    plannedCommand
                                                    confirmationToken
                                                    argv
                                                    requestCancellationToken
                        | Ok target, Some descriptor, Some confirmationToken when
                            descriptor.Id.Value = "project.relocate"
                            ->
                            match commandArguments workspace descriptor arguments with
                            | Error rpcError -> return Error rpcError
                            | Ok parsed ->
                                let mutationRequest =
                                    { CommandId = descriptor.Id
                                      TargetWorkspaceNodeId = target
                                      Arguments = parsed
                                      ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                                let! planned =
                                    planMutation
                                        workspace
                                        context.State
                                        mutationRequest
                                        requestCancellationToken

                                match planned with
                                | Failure failure ->
                                    return Error(WorkspaceRpcResponses.failureError failure)
                                | Success(CompositePlan plan) ->
                                    return!
                                        WorkspaceEditOperation.Start(
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
                                            confirmationToken,
                                            "Starting project relocation.",
                                            (fun () -> Ok()),
                                            requestCancellationToken
                                        )
                                | Success _ -> return Error RpcErrors.internalError
                        | _, _, None ->
                            return
                                Error(
                                    RpcErrors.invalidParams
                                        "workspace/commands/execute requires confirmationToken."
                                )
                        | Ok target, Some descriptor, Some confirmationToken ->
                            match commandArguments workspace descriptor arguments with
                            | Error rpcError -> return Error rpcError
                            | Ok parsed ->
                                let mutationRequest =
                                    { CommandId = descriptor.Id
                                      TargetWorkspaceNodeId = target
                                      Arguments = parsed
                                      ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                                let! planned =
                                    planMutation
                                        workspace
                                        context.State
                                        mutationRequest
                                        requestCancellationToken

                                match planned with
                                | Failure failure ->
                                    return Error(WorkspaceRpcResponses.failureError failure)
                                | Success plan ->
                                    let token = WorkspaceEditConfirmation.Create confirmationToken

                                    match
                                        context.Coordinator.Execute(
                                            plannedRequest plan,
                                            plannedActions plan,
                                            token,
                                            requestCancellationToken
                                        )
                                    with
                                    | Failure failure ->
                                        return Error(WorkspaceRpcResponses.failureError failure)
                                    | Success(RolledBack failure) ->
                                        return Error(WorkspaceRpcResponses.failureError failure)
                                    | Success Applied ->
                                        let! invalidated =
                                            context.State.InvalidateFromTransactionAsync(
                                                plannedPaths plan,
                                                CancellationToken.None
                                            )

                                        let reset =
                                            match invalidated with
                                            | WorkspaceProjectInvalidationResult.Reset _ -> true
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
                                                    WorkspaceRpcResponses.commandExecuteResult
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
        (context: WorkspaceCommandContext)
        (request: WorkspaceRpcRequest)
        (requestCancellationToken: CancellationToken)
        : Task<Result<RpcRequestResult, RpcError>> =
        task {
            match request with
            | WorkspaceRpcRequest.CommandList _
            | WorkspaceRpcRequest.CommandDescribe _
            | WorkspaceRpcRequest.CommandPreview _
            | WorkspaceRpcRequest.CommandExecute _ ->
                let! workspace = context.State.WorkspaceAsync requestCancellationToken

                match workspace with
                | Error rpcError -> return Error rpcError
                | Ok workspace ->
                    return! dispatchResolved context workspace request requestCancellationToken
            | _ -> return invalidArg "request" "A command request is required."
        }
