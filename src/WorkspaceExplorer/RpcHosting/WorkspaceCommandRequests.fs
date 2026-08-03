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
    let private publishMutationNotifications
        (context: WorkspaceCommandContext)
        invalidated
        cancellationToken
        =
        task {
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
                    context.RebuildWatcher cancellationToken

            let notifications = context.MutationNotifications invalidated @ watcherNotifications

            if
                notifications
                |> List.exists (fun notification ->
                    let encodedBytes = (MessagePackRpcCodec.encodeFrame notification).Length

                    encodedBytes > context.MaximumFrameBytes())
            then
                let! pressureReset =
                    WorkspaceNavigationRequests.resetForFramePressure
                        context.State
                        cancellationToken

                context.Watcher.Pause()
                return [ WorkspaceRpcNotifications.workspaceReset pressureReset ]
            else
                return notifications
        }

    let private createOption (entry: WorkspaceTemplateEntry) =
        let kind =
            match entry.Kind with
            | WorkspaceCreateKind.Empty -> "empty"
            | WorkspaceCreateKind.ItemTemplate -> "itemTemplate"
            | WorkspaceCreateKind.ProjectTemplate -> "projectTemplate"
            | WorkspaceCreateKind.SolutionFolder -> "solutionFolder"
            | WorkspaceCreateKind.AddExisting -> "addExisting"

        let execution =
            match entry.Execution with
            | WorkspaceCreateExecution.Transaction -> "transaction"
            | WorkspaceCreateExecution.Operation -> "operation"

        let fields =
            ResizeArray<string * RpcValue>
                [ "selectionId", RpcValue.String entry.SelectionId
                  "kind", RpcValue.String kind
                  "displayName", RpcValue.String entry.DisplayName
                  "description", RpcValue.String entry.Description
                  "execution", RpcValue.String execution ]

        entry.Language
        |> Option.iter (fun value -> fields.Add("language", RpcValue.String value))

        RpcValue.map fields

    let private isDotnetCommandPlan =
        function
        | DotnetCommandPlan _ -> true
        | _ -> false

    let private genericEffects (plan: PlannedWorkspaceCommand) =
        plannedActions plan
        |> Seq.collect (function
            | WorkspaceEditAction.CreateDirectory path ->
                [ { Operation = "create"
                    Target = path
                    Recursive = false } ]
            | WorkspaceEditAction.ReplaceFile(path, _) ->
                [ { Operation = "modify"
                    Target = path
                    Recursive = false } ]
            | WorkspaceEditAction.ReplaceGeneratedDocument(path, _) ->
                [ { Operation = "modify"
                    Target = path
                    Recursive = false } ]
            | WorkspaceEditAction.Rename(source, destination)
            | WorkspaceEditAction.Move(source, destination)
            | WorkspaceEditAction.Copy(source, destination) ->
                [ { Operation = "modify"
                    Target = source
                    Recursive = false }
                  { Operation = "modify"
                    Target = destination
                    Recursive = false } ]
            | WorkspaceEditAction.PermanentDelete(path, recursive) ->
                [ { Operation = "delete"
                    Target = path
                    Recursive = recursive } ]
            | WorkspaceEditAction.Trash path ->
                [ { Operation = "trash"
                    Target = path
                    Recursive = false } ])
        |> Seq.toArray

    let private prepareMutation
        (workspace: SolutionWorkspace)
        (context: WorkspaceCommandContext)
        (targetContext: WorkspaceSemanticContext)
        (descriptor: CommandDescriptor)
        (parsed: CommandArguments)
        (expectedRevision: int64)
        (cancellationToken: CancellationToken)
        =
        task {
            if descriptor.Id.Value = "workspace.addExisting" then
                if not (context.AddExistingNegotiated()) then
                    return
                        Error(
                            RpcErrors.unsupported
                                "The client did not negotiate workspace.addExisting.selector."
                        )
                else
                    return!
                        AddExistingMutation.prepareAsync
                            workspace
                            context.State
                            context.AddExistingSelector
                            targetContext
                            parsed
                            expectedRevision
                            cancellationToken
            elif
                descriptor.Id.Value = "workspace.rename"
                || descriptor.Id.Value = "workspace.move"
                || descriptor.Id.Value = "workspace.copy"
            then
                return!
                    ContextWorkspaceBatchActions.prepareAsync
                        workspace
                        context.State
                        targetContext
                        descriptor
                        parsed
                        expectedRevision
                        cancellationToken
            elif ContextWorkspaceCommands.tryDescribe descriptor.Id |> Option.isSome then
                return!
                    ContextWorkspaceActions.prepareAsync
                        workspace
                        context.State
                        targetContext
                        descriptor
                        parsed
                        expectedRevision
                        cancellationToken
            else
                let request =
                    { CommandId = descriptor.Id
                      TargetWorkspaceNodeId = commandTarget targetContext
                      Arguments = parsed
                      ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                let! planned = planMutation workspace context.State request cancellationToken

                return
                    match planned with
                    | Failure failure -> Error(WorkspaceRpcResponses.failureError failure)
                    | Success plan ->
                        Ok
                            { Plan = plan
                              CommandRequest = None
                              Summary = descriptor.Name
                              Effects = genericEffects plan
                              TemplateExecution = None }
        }

    let private availableCommands
        (requestContext: WorkspaceCommandContext)
        workspace
        (context: WorkspaceSemanticContext)
        =
        let target = commandTarget context

        let solutionAndProjectCommands =
            seq {
                yield! SolutionEditor.Discover(workspace, target)
                yield! ProjectEditing.discoverItems workspace target
                yield! ProjectRelocation.discover workspace target
                yield! ProjectEditing.discoverProperties workspace target
                yield! ProjectEditing.discoverFolders workspace target
            }

        solutionAndProjectCommands
        |> Seq.append (DotnetCommandCatalog.discover workspace target)
        |> Seq.append (DotnetLifecycleCommands.discover workspace target)
        |> Seq.append (SolutionLaunchProfileCommands.discover workspace target)
        |> Seq.append (
            ContextWorkspaceCommands.discover workspace.Descriptor.IsReadOnly (Some context.Node)
        )
        |> Seq.filter (fun descriptor ->
            descriptor.Id.Value <> "workspace.addExisting"
            || requestContext.AddExistingNegotiated())

    let private contextualCommandIsApplicable
        (requestContext: WorkspaceCommandContext)
        (context: WorkspaceSemanticContext)
        (descriptor: CommandDescriptor)
        =
        match ContextWorkspaceCommands.tryDescribe descriptor.Id with
        | None -> true
        | Some _ ->
            ContextWorkspaceCommands.discover false (Some context.Node)
            |> Seq.filter (fun candidate ->
                candidate.Id.Value <> "workspace.addExisting"
                || requestContext.AddExistingNegotiated())
            |> Seq.exists (fun candidate -> candidate.Id = descriptor.Id)

    let private resolveTarget
        (context: WorkspaceCommandContext)
        (workspace: SolutionWorkspace)
        targetNodeId
        (cancellationToken: CancellationToken)
        =
        let nodeId =
            targetNodeId
            |> Option.defaultValue (WorkspaceIndexPure.workspaceRoot workspace.Descriptor).Id.Value

        context.State.SemanticContextAsync(nodeId, None, cancellationToken)

    let private dispatchResolved
        (context: WorkspaceCommandContext)
        workspace
        request
        (requestCancellationToken: CancellationToken)
        =
        task {
            match request with
            | WorkspaceRpcRequest.CreateOptions(targetNodeId, expectedRevision) ->
                let! resolved =
                    context.State.SemanticContextAsync(
                        targetNodeId,
                        Some expectedRevision,
                        requestCancellationToken
                    )

                match resolved with
                | Error rpcError -> return Error rpcError
                | Ok(_, target) when target.Node.LoadState = WorkspaceNodeLoadState.FilteredOut ->
                    return
                        Error(
                            RpcErrors.unsupported
                                "New is not available for a filtered project placeholder."
                        )
                | Ok(revision, target) ->
                    match target.Node.Kind with
                    | WorkspaceNodeKind.Workspace
                    | WorkspaceNodeKind.SolutionFolder
                    | WorkspaceNodeKind.SolutionItem
                    | WorkspaceNodeKind.Project
                    | WorkspaceNodeKind.ProjectFolder
                    | WorkspaceNodeKind.ProjectFile
                    | WorkspaceNodeKind.DependencyContainer
                    | WorkspaceNodeKind.Dependency
                    | WorkspaceNodeKind.DependencyProperty ->
                        let! catalog =
                            WorkspaceTemplateCatalog.readAsync workspace requestCancellationToken

                        return
                            catalog
                            |> Result.map (fun catalog ->
                                RpcRequestResult.Continue
                                    { Result =
                                        WorkspaceTemplateCatalog.options
                                            target
                                            (context.AddExistingNegotiated())
                                            catalog
                                        |> Seq.filter (fun option ->
                                            not workspace.Descriptor.IsReadOnly
                                            || (option.Kind <> WorkspaceCreateKind.SolutionFolder
                                                && option.Kind <> WorkspaceCreateKind.AddExisting))
                                        |> Seq.map createOption
                                        |> WorkspaceRpcResponses.createOptionsResult revision
                                      Notifications = []
                                      BackgroundWork = None
                                      AfterResponse = None })
                    | _ ->
                        return
                            Error(
                                RpcErrors.unsupported
                                    "New is not available for the selected workspace node."
                            )
            | WorkspaceRpcRequest.CommandList targetNodeId ->
                let! resolved =
                    resolveTarget context workspace targetNodeId requestCancellationToken

                match resolved with
                | Error rpcError -> return Error rpcError
                | Ok(_, target) ->
                    return
                        Ok(
                            RpcRequestResult.Continue
                                { Result =
                                    availableCommands context workspace target
                                    |> WorkspaceRpcResponses.commandListResult
                                  Notifications = []
                                  BackgroundWork = None
                                  AfterResponse = None }
                        )
            | WorkspaceRpcRequest.CommandDescribe(commandId, targetNodeId) ->
                let! resolved =
                    resolveTarget context workspace targetNodeId requestCancellationToken

                match resolved, WorkspaceCommandCatalog.tryDescribe commandId with
                | Error rpcError, _ -> return Error rpcError
                | _, None ->
                    return Error(RpcErrors.create "not_found" "The command was not found." None)
                | Ok(_, target), Some descriptor when
                    availableCommands context workspace target
                    |> Seq.exists (fun candidate -> candidate.Id = descriptor.Id)
                    ->
                    return
                        Ok(
                            RpcRequestResult.Continue
                                { Result = WorkspaceRpcResponses.commandDescribeResult descriptor
                                  Notifications = []
                                  BackgroundWork = None
                                  AfterResponse = None }
                        )
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
                    let! resolved =
                        resolveTarget context workspace targetNodeId requestCancellationToken

                    match resolved, WorkspaceCommandCatalog.tryDescribe commandId with
                    | Error rpcError, _ -> return Error rpcError
                    | _, None ->
                        return Error(RpcErrors.create "not_found" "The command was not found." None)
                    | Ok(_, targetContext), Some descriptor when
                        not (contextualCommandIsApplicable context targetContext descriptor)
                        ->
                        return
                            Error(
                                RpcErrors.create
                                    "not_found"
                                    "The command is not applicable to the target."
                                    None
                            )
                    | Ok(_, targetContext), Some descriptor ->
                        match commandArguments workspace descriptor arguments with
                        | Error rpcError -> return Error rpcError
                        | Ok parsed ->
                            let! planned =
                                prepareMutation
                                    workspace
                                    context
                                    targetContext
                                    descriptor
                                    parsed
                                    expectedRevision
                                    requestCancellationToken

                            match planned with
                            | Error rpcError -> return Error rpcError
                            | Ok prepared ->
                                match
                                    context.Coordinator.Prepare(
                                        plannedRequest prepared.Plan,
                                        plannedActions prepared.Plan
                                    )
                                with
                                | Failure failure ->
                                    return Error(WorkspaceRpcResponses.failureError failure)
                                | Success preview ->
                                    return
                                        Ok(
                                            RpcRequestResult.Continue
                                                { Result =
                                                    WorkspaceRpcResponses.commandPreviewResult
                                                        preview
                                                        prepared.Summary
                                                        (prepared.Effects
                                                         |> Seq.map (fun effect ->
                                                             effect.Operation,
                                                             effect.Target,
                                                             effect.Recursive))
                                                  Notifications = []
                                                  BackgroundWork = None
                                                  AfterResponse = None }
                                        )
            | WorkspaceRpcRequest.CommandExecute(commandId,
                                                 targetNodeId,
                                                 arguments,
                                                 expectedRevision,
                                                 confirmationToken) ->
                let! resolved =
                    resolveTarget context workspace targetNodeId requestCancellationToken

                let resolvedDescriptor =
                    match resolved, WorkspaceCommandCatalog.tryDescribe commandId with
                    | Ok(_, targetContext), Some descriptor ->
                        Some(commandTarget targetContext, descriptor)
                    | _ -> None

                let! lifecycle =
                    match resolvedDescriptor with
                    | Some(target, descriptor) ->
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
                        match resolvedDescriptor with
                        | Some(target, descriptor) ->
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
                            resolved,
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
                        | Ok(_, targetContext), Some descriptor, _ when
                            not (contextualCommandIsApplicable context targetContext descriptor)
                            ->
                            return
                                Error(
                                    RpcErrors.create
                                        "not_found"
                                        "The command is not applicable to the target."
                                        None
                                )
                        | Ok(_, targetContext), Some descriptor, Some confirmationToken when
                            ContextWorkspaceCommands.tryDescribe descriptor.Id |> Option.isSome
                            ->
                            match commandArguments workspace descriptor arguments with
                            | Error rpcError -> return Error rpcError
                            | Ok parsed ->
                                let! prepared =
                                    prepareMutation
                                        workspace
                                        context
                                        targetContext
                                        descriptor
                                        parsed
                                        expectedRevision
                                        requestCancellationToken

                                match prepared with
                                | Error rpcError -> return Error rpcError
                                | Ok prepared when prepared.TemplateExecution.IsSome ->
                                    let request =
                                        prepared.CommandRequest
                                        |> Option.defaultWith (fun () ->
                                            invalidOp
                                                "The template command request is unavailable.")

                                    match
                                        WorkspaceCommandCatalog.tryDescribe "template.create",
                                        DotnetCommandCatalog.argv workspace request
                                    with
                                    | None, _ -> return Error RpcErrors.internalError
                                    | _, Error message ->
                                        return Error(RpcErrors.invalidParams message)
                                    | Some templateDescriptor, Ok argv ->
                                        return!
                                            DotnetCommandOperation.start
                                                (WorkspaceCommandContext.operation workspace context)
                                                templateDescriptor
                                                request
                                                (Some prepared.Plan)
                                                (Some confirmationToken)
                                                argv
                                                prepared.TemplateExecution
                                                requestCancellationToken
                                | Ok prepared ->
                                    let token = WorkspaceEditConfirmation.Create confirmationToken

                                    match
                                        context.Coordinator.Execute(
                                            plannedRequest prepared.Plan,
                                            plannedActions prepared.Plan,
                                            token,
                                            requestCancellationToken
                                        )
                                    with
                                    | Failure failure ->
                                        return Error(WorkspaceRpcResponses.failureError failure)
                                    | Success(RolledBack failure) ->
                                        return Error(WorkspaceRpcResponses.failureError failure)
                                    | Success Applied ->
                                        if descriptor.Id.Value = "workspace.addExisting" then
                                            context.AddExistingSelector.Invalidate()

                                        let! invalidated =
                                            context.State.InvalidateFromTransactionAsync(
                                                plannedPaths prepared.Plan,
                                                CancellationToken.None
                                            )

                                        let! notifications =
                                            publishMutationNotifications
                                                context
                                                invalidated
                                                CancellationToken.None

                                        return
                                            Ok(
                                                RpcRequestResult.Continue
                                                    { Result =
                                                        WorkspaceRpcResponses.commandExecuteResult
                                                            context.State.Revision
                                                      Notifications = notifications
                                                      BackgroundWork = None
                                                      AfterResponse = None }
                                            )
                        | Ok(_, targetContext), Some descriptor, confirmationToken when
                            DotnetCommandCatalog.tryDescribe descriptor.Id |> Option.isSome
                            && (not (DotnetCommandCatalog.isMutation descriptor.Id.Value)
                                && descriptor.Access <> CommandAccess.Write
                                || confirmationToken.IsSome)
                            ->
                            let target = commandTarget targetContext

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
                                            return!
                                                DotnetCommandOperation.start
                                                    (WorkspaceCommandContext.operation
                                                        workspace
                                                        context)
                                                    descriptor
                                                    mutationRequest
                                                    plannedCommand
                                                    confirmationToken
                                                    argv
                                                    None
                                                    requestCancellationToken
                        | Ok(_, targetContext), Some descriptor, Some confirmationToken when
                            descriptor.Id.Value = "project.relocate"
                            ->
                            let target = commandTarget targetContext

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
                                            WorkspaceCommandContext.operation workspace context,
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
                        | Ok(_, targetContext), Some descriptor, Some confirmationToken ->
                            let target = commandTarget targetContext

                            match commandArguments workspace descriptor arguments with
                            | Error rpcError -> return Error rpcError
                            | Ok parsed ->
                                let! prepared =
                                    prepareMutation
                                        workspace
                                        context
                                        targetContext
                                        descriptor
                                        parsed
                                        expectedRevision
                                        requestCancellationToken

                                match prepared with
                                | Error rpcError -> return Error rpcError
                                | Ok prepared ->
                                    let token = WorkspaceEditConfirmation.Create confirmationToken

                                    match
                                        context.Coordinator.Execute(
                                            plannedRequest prepared.Plan,
                                            plannedActions prepared.Plan,
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
                                                plannedPaths prepared.Plan,
                                                CancellationToken.None
                                            )

                                        let! notifications =
                                            publishMutationNotifications
                                                context
                                                invalidated
                                                CancellationToken.None

                                        return
                                            Ok(
                                                RpcRequestResult.Continue
                                                    { Result =
                                                        WorkspaceRpcResponses.commandExecuteResult
                                                            context.State.Revision
                                                      Notifications = notifications
                                                      BackgroundWork = None
                                                      AfterResponse = None }
                                            )
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
            | WorkspaceRpcRequest.CreateOptions _
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
