namespace Dotnet.CLI.Plus

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport
open CanonicalMutationPlanning

type internal CanonicalCommandOperationContext =
    { Workspace: SolutionWorkspace
      State: WorkspaceState
      Watcher: WorkspaceWatcher
      Coordinator: MutationCoordinator
      PublicationGate: SemaphoreSlim
      ActiveOperations: ConcurrentDictionary<string, ExportOperationState>
      WorkspaceRoot: string
      MaximumFrameBytes: unit -> int
      RebuildWatcher: CancellationToken -> Task<RpcFrame list>
      MutationNotifications: WorkspaceInvalidationResult -> RpcFrame list }

module internal CanonicalCommandOperation =
    let private mutationFailure outcome =
        match outcome with
        | Success Applied -> None
        | Success(RolledBack failure)
        | Failure failure -> Some failure

    let private templateCountMessage count =
        $"Template produced {count} projects; expected one."

    let private passThroughRecoveryMessage =
        "pass-through arguments may target artifacts outside the authorized output"

    let start
        (context: CanonicalCommandOperationContext)
        (descriptor: CommandDescriptor)
        (request: CommandMutationRequest)
        (canonicalPlan: PlannedMutation option)
        (previewId: string option)
        (argv: string list)
        (requestCancellationToken: CancellationToken)
        : Task<Result<RpcDispatchResult, RpcError>> =
        task {
            let workspace = context.Workspace
            let state = context.State
            let watcher = context.Watcher
            let coordinator = context.Coordinator
            let publicationGate = context.PublicationGate
            let activeOperations = context.ActiveOperations
            let workspaceRoot = context.WorkspaceRoot
            let rebuildWatcher = context.RebuildWatcher
            let mutationNotifications = context.MutationNotifications
            let operationId = Guid.NewGuid().ToString "N"
            let operation = ExportOperationState requestCancellationToken

            if not (activeOperations.TryAdd(operationId, operation)) then
                operation.Complete()
                return Error RpcErrors.internalError
            else
                let background (sink: RpcNotificationSink) sessionToken =
                    task {
                        let mutable sequence = 0

                        let nextSequence () = Interlocked.Increment(&sequence) - 1

                        let revision = state.Revision

                        let publish =
                            PipeOperations.createOutputPublisher
                                (context.MaximumFrameBytes())
                                state.Descriptor
                                operationId
                                revision
                                sink
                                nextSequence

                        let mutable outcome = PublicOperationOutcome.Succeeded

                        let mutable publicationHeld = false
                        let mutable completionReserved = false
                        let mutable transitionPublished = false
                        let mutable ownedSnapshots: OwnedFileSnapshot array = Array.empty
                        let mutable invalidationPaths: WorkspaceArtifactPath array = Array.empty
                        let mutable templateBefore: OutputSnapshot option = None
                        let mutable templateAfter: OutputSnapshot option = None
                        let mutable templateCreated: OutputEntry option = None
                        let mutable templateSolution: OwnedFileSnapshot array = Array.empty
                        let captureExpectedFiles = CommandCompensation.captureExpectedFiles
                        let resetForFramePressure = PipeWorkspaceRequests.resetForFramePressure

                        let brokerOutcome fallback (result: BrokerResult) =
                            match result.Diagnostics |> List.tryHead with
                            | Some diagnostic when diagnostic.DiagnosticCode.Value = "cancelled" ->
                                PublicOperationOutcome.Cancelled
                            | Some diagnostic ->
                                PublicOperationOutcome.Failed(
                                    diagnostic.DiagnosticCode.Value,
                                    diagnostic.Message
                                )
                            | None ->
                                PublicOperationOutcome.Failed("external_tool_failed", fallback)

                        let requirePartialRecovery detail =
                            let message =
                                match outcome with
                                | PublicOperationOutcome.Failed("partial_recovery_required",
                                                                existing) -> $"{existing}; {detail}"
                                | _ -> detail

                            outcome <-
                                PublicOperationOutcome.Failed("partial_recovery_required", message)

                        let compensateTemplate () =
                            if
                                outcome <> PublicOperationOutcome.Succeeded
                                && templateBefore.IsSome
                                && not transitionPublished
                            then
                                try
                                    let before = templateBefore.Value
                                    let remaining = ResizeArray<string>()

                                    match
                                        CommandCompensation.restoreFiles
                                            coordinator
                                            workspaceRoot
                                            (fun () -> state.Revision)
                                            descriptor.CommandId
                                            request.Arguments
                                            templateSolution
                                    with
                                    | Ok() -> ()
                                    | Error message ->
                                        remaining.Add $"{workspace.BackingPath.Value} ({message})"

                                    if before.Existed then
                                        remaining.Add
                                            $"{before.Root} (pre-existing output is not rewritten)"
                                    else
                                        templateCreated
                                        |> Option.bind CommandCompensation.removeNewOutput
                                        |> Option.iter remaining.Add

                                    let passThroughMayEscape =
                                        request.Arguments.Values
                                        |> Seq.exists (fun argument ->
                                            argument.ParameterId.Value = "arguments"
                                            && match argument.Value with
                                               | TextArray values -> values.Length > 0
                                               | _ -> false)

                                    if passThroughMayEscape then
                                        remaining.Add passThroughRecoveryMessage

                                    if remaining.Count > 0 then
                                        let detail = String.concat ", " remaining

                                        requirePartialRecovery
                                            $"Template compensation could not account for: {detail}"
                                with error ->
                                    requirePartialRecovery
                                        $"Template compensation failed: {error.Message}"

                        let compensateOwnedFiles () =
                            if
                                outcome <> PublicOperationOutcome.Succeeded
                                && ownedSnapshots.Length > 0
                                && not transitionPublished
                            then
                                try
                                    match
                                        CommandCompensation.restoreFiles
                                            coordinator
                                            workspaceRoot
                                            (fun () -> state.Revision)
                                            descriptor.CommandId
                                            request.Arguments
                                            ownedSnapshots
                                    with
                                    | Ok() -> ()
                                    | Error message ->
                                        let paths =
                                            ownedSnapshots
                                            |> CommandCompensation.snapshotPaths
                                            |> String.concat ", "

                                        requirePartialRecovery (
                                            $"Compensation could not be verified for {paths}: "
                                            + message
                                        )
                                with error ->
                                    requirePartialRecovery
                                        $"Command compensation failed: {error.Message}"

                        try
                            use linked =
                                CancellationTokenSource.CreateLinkedTokenSource(
                                    operation.Token,
                                    sessionToken
                                )

                            do! publicationGate.WaitAsync linked.Token
                            publicationHeld <- true

                            do!
                                sink.WriteAsync(
                                    PublicProtocol.operationProgress
                                        state.Descriptor
                                        operationId
                                        (nextSequence ())
                                        revision
                                        "Starting canonical dotnet command."
                                )

                            let canExecute =
                                match canonicalPlan with
                                | None -> true
                                | Some plan ->
                                    let execution =
                                        coordinator.Execute(
                                            plannedRequest plan,
                                            plannedActions plan,
                                            MutationConfirmationToken.Create previewId.Value,
                                            linked.Token
                                        )

                                    match mutationFailure execution with
                                    | None -> true
                                    | Some failure ->
                                        outcome <-
                                            PublicOperationOutcome.Failed(
                                                failure.Code.Value,
                                                failure.Diagnostic.Message
                                            )

                                        false

                            if canExecute then
                                invalidationPaths <-
                                    canonicalPlan
                                    |> Option.map (plannedPaths >> Seq.toArray)
                                    |> Option.defaultValue Array.empty

                                if
                                    CanonicalCommands.isPackageMutation descriptor.CommandId.Value
                                    || descriptor.CommandId.Value = "reference.add"
                                    || descriptor.CommandId.Value = "reference.remove"
                                then
                                    ownedSnapshots <-
                                        canonicalPlan
                                        |> Option.map (
                                            plannedPaths >> CommandCompensation.snapshotFiles
                                        )
                                        |> Option.defaultValue Array.empty

                                if
                                    descriptor.CommandId.Value = "template.create"
                                    && not (CanonicalCommands.isTemplateDryRun request)
                                then
                                    let output =
                                        CanonicalCommands.templateOutput workspace request
                                        |> Result.defaultWith invalidOp

                                    templateBefore <-
                                        Some(CommandCompensation.outputSnapshot output)

                                    templateSolution <-
                                        CommandCompensation.snapshotFiles
                                            [| WorkspaceArtifactPath.Create
                                                   workspace.BackingPath.Value |]

                                use outputWriter = new OperationNotificationWriter(publish "stdout")

                                use errorWriter = new OperationNotificationWriter(publish "stderr")

                                let! executed =
                                    Broker.ExecuteAsync(
                                        argv |> List.toArray,
                                        Human(outputWriter, errorWriter, false, false),
                                        linked.Token
                                    )

                                ownedSnapshots <- captureExpectedFiles ownedSnapshots

                                match templateBefore with
                                | Some before ->
                                    let after = CommandCompensation.outputSnapshot before.Root
                                    templateAfter <- Some after

                                    templateCreated <-
                                        CommandCompensation.newOutputRoot before after
                                | None -> ()

                                if operation.IsCancellationReserved && executed.Success then
                                    outcome <- PublicOperationOutcome.Cancelled
                                elif not executed.Success then
                                    outcome <-
                                        brokerOutcome
                                            "The canonical dotnet command failed."
                                            executed
                                else
                                    if
                                        CanonicalCommands.requiresRestore
                                            descriptor.CommandId.Value
                                            request.Arguments
                                    then
                                        let project =
                                            argv
                                            |> List.skipWhile ((<>) "--project")
                                            |> List.tryItem 1

                                        match project with
                                        | Some project ->
                                            let! restored =
                                                Broker.ExecuteAsync(
                                                    [| "restore"; project |],
                                                    Human(outputWriter, errorWriter, false, false),
                                                    linked.Token
                                                )

                                            if not restored.Success then
                                                outcome <-
                                                    brokerOutcome
                                                        "The canonical restore failed."
                                                        restored
                                        | None ->
                                            outcome <-
                                                PublicOperationOutcome.Failed(
                                                    "internal_error",
                                                    "The canonical project argument is unavailable."
                                                )

                                    if
                                        outcome = PublicOperationOutcome.Succeeded
                                        && descriptor.CommandId.Value = "template.create"
                                        && not (CanonicalCommands.isTemplateDryRun request)
                                    then
                                        match templateBefore, templateAfter with
                                        | Some before, Some after ->
                                            match
                                                CommandCompensation.newProjectFiles before after
                                            with
                                            | [| project |] ->
                                                linked.Token.ThrowIfCancellationRequested()

                                                let addRequest =
                                                    { CommandId =
                                                        CommandId.Create "solution.project.add"
                                                      TargetId = request.TargetId
                                                      Arguments =
                                                        CommandArguments.Create
                                                            [ { ParameterId =
                                                                  CommandParameterId.Create "path"
                                                                Value =
                                                                  Path(
                                                                      WorkspaceArtifactPath.Create
                                                                          project
                                                                  ) } ]
                                                      ExpectedRevision =
                                                        WorkspaceRevision.Create state.Revision }

                                                let! planned =
                                                    SolutionPersistenceMutator.PlanAsync(
                                                        workspace,
                                                        addRequest,
                                                        linked.Token
                                                    )

                                                match planned with
                                                | Failure failure ->
                                                    outcome <-
                                                        PublicOperationOutcome.Failed(
                                                            failure.Code.Value,
                                                            failure.Diagnostic.Message
                                                        )
                                                | Success plan ->
                                                    let actions = plannedActions (SolutionPlan plan)

                                                    match
                                                        coordinator.Prepare(plan.Request, actions)
                                                    with
                                                    | Failure failure ->
                                                        outcome <-
                                                            PublicOperationOutcome.Failed(
                                                                failure.Code.Value,
                                                                failure.Diagnostic.Message
                                                            )
                                                    | Success preview ->
                                                        let execution =
                                                            coordinator.Execute(
                                                                plan.Request,
                                                                actions,
                                                                preview.Confirmation,
                                                                linked.Token
                                                            )

                                                        match mutationFailure execution with
                                                        | None ->
                                                            templateSolution <-
                                                                captureExpectedFiles
                                                                    templateSolution

                                                            invalidationPaths <-
                                                                [| WorkspaceArtifactPath.Create
                                                                       workspace.BackingPath.Value
                                                                   WorkspaceArtifactPath.Create
                                                                       before.Root
                                                                   WorkspaceArtifactPath.Create
                                                                       project |]
                                                        | Some failure ->
                                                            outcome <-
                                                                PublicOperationOutcome.Failed(
                                                                    failure.Code.Value,
                                                                    failure.Diagnostic.Message
                                                                )
                                            | projects ->
                                                let failureMessage =
                                                    templateCountMessage projects.Length

                                                outcome <-
                                                    PublicOperationOutcome.Failed(
                                                        "template_project_count",
                                                        failureMessage
                                                    )
                                        | _ ->
                                            outcome <-
                                                PublicOperationOutcome.Failed(
                                                    "template_output_unavailable",
                                                    "The template output could not be inspected."
                                                )

                                    if
                                        outcome = PublicOperationOutcome.Succeeded
                                        && CanonicalCommands.isPackageMutation
                                            descriptor.CommandId.Value
                                    then
                                        let project =
                                            argv
                                            |> List.skipWhile ((<>) "--project")
                                            |> List.tryItem 1

                                        match project with
                                        | Some project ->
                                            let root =
                                                Path.GetDirectoryName workspace.BackingPath.Value
                                                |> Option.ofObj
                                                |> Option.defaultValue (
                                                    Directory.GetCurrentDirectory()
                                                )

                                            match
                                                CentralPackageManagement.normalize root project
                                            with
                                            | Error message ->
                                                outcome <-
                                                    PublicOperationOutcome.Failed(
                                                        "central_package_conflict",
                                                        message
                                                    )
                                            | Ok updates ->
                                                let targets =
                                                    updates
                                                    |> List.map (fun (path, _) ->
                                                        WorkspaceArtifactPath.Create path)
                                                    |> ImmutableArray.CreateRange

                                                let normalizationRequest =
                                                    { CommandId = descriptor.CommandId
                                                      Targets = targets
                                                      Arguments = request.Arguments
                                                      ExpectedRevision =
                                                        WorkspaceRevision.Create state.Revision
                                                      Intents =
                                                        ImmutableHashSet.Create
                                                            MutationIntent.Overwrite
                                                      AuthorizedRoots =
                                                        ImmutableArray.Create(
                                                            WorkspaceArtifactPath.Create root
                                                        ) }

                                                let actions =
                                                    updates
                                                    |> Seq.map (fun (path, bytes) ->
                                                        MutationAction.ReplaceFile(path, bytes))

                                                match
                                                    coordinator.Prepare(
                                                        normalizationRequest,
                                                        actions
                                                    )
                                                with
                                                | Failure failure ->
                                                    outcome <-
                                                        PublicOperationOutcome.Failed(
                                                            failure.Code.Value,
                                                            failure.Diagnostic.Message
                                                        )
                                                | Success preview ->
                                                    let execution =
                                                        coordinator.Execute(
                                                            normalizationRequest,
                                                            actions,
                                                            preview.Confirmation,
                                                            linked.Token
                                                        )

                                                    match mutationFailure execution with
                                                    | None ->
                                                        ownedSnapshots <-
                                                            captureExpectedFiles ownedSnapshots
                                                    | Some failure ->
                                                        outcome <-
                                                            PublicOperationOutcome.Failed(
                                                                failure.Code.Value,
                                                                failure.Diagnostic.Message
                                                            )
                                        | None ->
                                            outcome <-
                                                PublicOperationOutcome.Failed(
                                                    "internal_error",
                                                    "The canonical project argument is unavailable."
                                                )

                                    if
                                        outcome = PublicOperationOutcome.Succeeded
                                        && CanonicalCommands.isMutation descriptor.CommandId.Value
                                        && not (CanonicalCommands.isTemplateDryRun request)
                                    then
                                        if operation.TryReserveCompletion() then
                                            completionReserved <- true

                                            let! invalidated =
                                                state.InvalidateFromTransactionAsync(
                                                    invalidationPaths,
                                                    CancellationToken.None
                                                )

                                            transitionPublished <- true

                                            let stateNotifications =
                                                mutationNotifications invalidated

                                            let frameTooLarge =
                                                stateNotifications
                                                |> List.exists (fun notification ->
                                                    let encoded = RpcCodec.encodeFrame notification

                                                    encoded.Length > context.MaximumFrameBytes())

                                            let! effectiveInvalidation =
                                                if frameTooLarge then
                                                    task {
                                                        let! reset =
                                                            resetForFramePressure
                                                                state
                                                                CancellationToken.None

                                                        return
                                                            WorkspaceInvalidationResult.Reset reset
                                                    }
                                                else
                                                    Task.FromResult invalidated

                                            let reset =
                                                match effectiveInvalidation with
                                                | WorkspaceInvalidationResult.Reset _ -> true
                                                | _ -> false

                                            if reset then
                                                watcher.Pause()

                                            let! watcherNotifications =
                                                if reset then
                                                    Task.FromResult []
                                                else
                                                    rebuildWatcher CancellationToken.None

                                            let notifications =
                                                mutationNotifications effectiveInvalidation
                                                @ watcherNotifications

                                            for notification in notifications do
                                                do! sink.WriteAsync notification
                                        else
                                            do! operation.WaitForCancellationResponseAsync()
                                            outcome <- PublicOperationOutcome.Cancelled
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

                        compensateTemplate ()
                        compensateOwnedFiles ()

                        try
                            let! completedOutcome =
                                task {
                                    if completionReserved || operation.TryReserveCompletion() then
                                        return outcome
                                    else
                                        do! operation.WaitForCancellationResponseAsync()

                                        return
                                            match outcome with
                                            | PublicOperationOutcome.Failed(code, _) when
                                                code = "partial_recovery_required"
                                                ->
                                                outcome
                                            | _ -> PublicOperationOutcome.Cancelled
                                }

                            do!
                                sink.WriteAsync(
                                    PublicProtocol.operationCompleted
                                        state.Descriptor
                                        operationId
                                        (nextSequence ())
                                        state.Revision
                                        completedOutcome
                                )
                        finally
                            if publicationHeld then
                                publicationGate.Release() |> ignore

                            activeOperations.TryRemove operationId |> ignore
                            operation.Complete()
                    }

                return
                    Ok
                        { Result = PublicProtocol.commandOperationResult operationId state.Revision
                          Notifications = []
                          BackgroundWork = Some background
                          AfterResponse = None
                          StopAfterResponse = false }
        }
