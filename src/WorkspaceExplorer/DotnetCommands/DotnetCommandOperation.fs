namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.CommandLine

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open WorkspaceCommandEditing

type internal DotnetCommandOperationContext =
    { Workspace: SolutionWorkspace
      State: WorkspaceIndex
      Watcher: WorkspaceIndexWatcher
      Coordinator: WorkspaceEditTransaction
      PublicationGate: SemaphoreSlim
      ActiveOperations: ConcurrentDictionary<string, WorkspaceExportOperation>
      WorkspaceRoot: string
      MaximumFrameBytes: unit -> int
      RebuildWatcher: CancellationToken -> Task<RpcFrame list>
      MutationNotifications: WorkspaceProjectInvalidationResult -> RpcFrame list }

module internal DotnetCommandOperation =
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
        (context: DotnetCommandOperationContext)
        (descriptor: CommandDescriptor)
        (request: CommandMutationRequest)
        (plannedCommand: PlannedWorkspaceCommand option)
        (confirmationToken: string option)
        (argv: string list)
        (templateExecution: ContextTemplateExecution option)
        (requestCancellationToken: CancellationToken)
        : Task<Result<RpcRequestResult, RpcError>> =
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
            let operation = WorkspaceExportOperation requestCancellationToken

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
                            WorkspaceOperations.createOutputPublisher
                                (context.MaximumFrameBytes())
                                state.Descriptor
                                operationId
                                revision
                                sink
                                nextSequence

                        let mutable outcome = WorkspaceOperationCompletion.Succeeded

                        let mutable publicationHeld = false
                        let mutable completionReserved = false
                        let mutable transitionPublished = false
                        let mutable ownedSnapshots: OwnedFileSnapshot array = Array.empty
                        let mutable ownedDirectories: string array = Array.empty
                        let mutable invalidationPaths: WorkspaceArtifactPath array = Array.empty
                        let mutable templateBefore: OutputDirectorySnapshot option = None
                        let mutable templateAfter: OutputDirectorySnapshot option = None
                        let mutable templateCreated: CreatedOutput option = None
                        let mutable templateSolution: OwnedFileSnapshot array = Array.empty
                        let mutable itemStagingDirectory: string option = None
                        let captureExpectedFiles = DotnetCommandCompensation.captureExpectedFiles

                        let resetForFramePressure =
                            WorkspaceNavigationRequests.resetForFramePressure

                        let brokerOutcome fallback (result: DirectCommandResult) =
                            match result.Diagnostics |> List.tryHead with
                            | Some diagnostic when diagnostic.Code.Value = "cancelled" ->
                                WorkspaceOperationCompletion.Cancelled
                            | Some diagnostic ->
                                WorkspaceOperationCompletion.Failed(
                                    diagnostic.Code.Value,
                                    diagnostic.Message
                                )
                            | None ->
                                WorkspaceOperationCompletion.Failed(
                                    "external_tool_failed",
                                    fallback
                                )

                        let requirePartialRecovery detail =
                            let message =
                                match outcome with
                                | WorkspaceOperationCompletion.Failed("partial_recovery_required",
                                                                      existing) ->
                                    $"{existing}; {detail}"
                                | _ -> detail

                            outcome <-
                                WorkspaceOperationCompletion.Failed(
                                    "partial_recovery_required",
                                    message
                                )

                        let compensateTemplate () =
                            if
                                outcome <> WorkspaceOperationCompletion.Succeeded
                                && templateBefore.IsSome
                                && not transitionPublished
                            then
                                try
                                    let before = templateBefore.Value
                                    let remaining = ResizeArray<string>()

                                    match
                                        DotnetCommandCompensation.restoreFiles
                                            coordinator
                                            workspaceRoot
                                            (fun () -> state.Revision)
                                            descriptor.Id
                                            request.Arguments
                                            templateSolution
                                    with
                                    | Ok() -> ()
                                    | Error message ->
                                        remaining.Add $"{workspace.SolutionPath.Value} ({message})"

                                    if before.Existed then
                                        remaining.Add
                                            $"{before.Root} (pre-existing output is not rewritten)"
                                    else
                                        templateCreated
                                        |> Option.bind DotnetCommandCompensation.removeNewOutput
                                        |> Option.iter remaining.Add

                                    let passThroughMayEscape =
                                        templateExecution.IsNone
                                        && request.Arguments.Values
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
                                outcome <> WorkspaceOperationCompletion.Succeeded
                                && ownedSnapshots.Length > 0
                                && not transitionPublished
                            then
                                try
                                    match
                                        DotnetCommandCompensation.restoreFiles
                                            coordinator
                                            workspaceRoot
                                            (fun () -> state.Revision)
                                            descriptor.Id
                                            request.Arguments
                                            ownedSnapshots
                                    with
                                    | Ok() -> ()
                                    | Error message ->
                                        let paths =
                                            ownedSnapshots
                                            |> DotnetCommandCompensation.snapshotPaths
                                            |> String.concat ", "

                                        requirePartialRecovery (
                                            $"Compensation could not be verified for {paths}: "
                                            + message
                                        )
                                with error ->
                                    requirePartialRecovery
                                        $"Command compensation failed: {error.Message}"

                            if
                                outcome <> WorkspaceOperationCompletion.Succeeded
                                && ownedDirectories.Length > 0
                                && not transitionPublished
                            then
                                let remaining =
                                    ownedDirectories
                                    |> Array.sortByDescending _.Length
                                    |> Array.choose (fun path ->
                                        try
                                            if
                                                Directory.Exists path
                                                && not (
                                                    Directory.EnumerateFileSystemEntries path
                                                    |> Seq.isEmpty
                                                )
                                            then
                                                Some $"{path} (directory is not empty)"
                                            else
                                                if Directory.Exists path then
                                                    Directory.Delete path

                                                if ArtifactFiles.exists path then
                                                    Some path
                                                else
                                                    None
                                        with error ->
                                            Some $"{path} ({error.Message})")

                                if remaining.Length > 0 then
                                    requirePartialRecovery (
                                        "Template compensation could not remove: "
                                        + String.concat ", " remaining
                                    )

                        let cleanupItemStaging () =
                            itemStagingDirectory
                            |> Option.iter (fun path ->
                                try
                                    if Directory.Exists path then
                                        Directory.Delete(path, true)

                                    if ArtifactFiles.exists path then
                                        requirePartialRecovery
                                            $"Item-template staging remains at {path}."
                                with error ->
                                    requirePartialRecovery
                                        $"Item-template staging remains at {path}: {error.Message}")

                        let stageArgv staging =
                            let rec replace =
                                function
                                | "--output" :: _ :: tail -> "--output" :: staging :: tail
                                | head :: tail -> head :: replace tail
                                | [] -> [ "--output"; staging ]

                            replace argv

                        let publishItemTemplate
                            projectPath
                            outputDirectory
                            (expectedOutputs: WorkspaceArtifactPath array)
                            staging
                            (cancellationToken: CancellationToken)
                            =
                            cancellationToken.ThrowIfCancellationRequested()

                            let directories =
                                Directory.EnumerateDirectories(
                                    staging,
                                    "*",
                                    SearchOption.AllDirectories
                                )
                                |> Seq.toArray

                            if
                                ArtifactFiles.isLink staging
                                || directories |> Array.exists ArtifactFiles.isLink
                            then
                                Error "Item templates cannot produce symbolic links."
                            else
                                let files =
                                    Directory.EnumerateFiles(
                                        staging,
                                        "*",
                                        SearchOption.AllDirectories
                                    )
                                    |> Seq.toArray

                                if files.Length = 0 then
                                    Error "The item template did not produce any files."
                                elif files |> Array.exists ArtifactFiles.isLink then
                                    Error "Item templates cannot produce symbolic links."
                                else
                                    let destinations =
                                        files
                                        |> Array.map (fun source ->
                                            let relative = Path.GetRelativePath(staging, source)
                                            source, Path.GetFullPath(relative, outputDirectory))

                                    let actualOutputs =
                                        destinations
                                        |> Array.map (snd >> Path.GetFullPath)
                                        |> Array.sortWith (fun left right ->
                                            StringComparer.Ordinal.Compare(left, right))

                                    let expectedOutputs =
                                        expectedOutputs
                                        |> Array.map (_.Value >> Path.GetFullPath)
                                        |> Array.sortWith (fun left right ->
                                            StringComparer.Ordinal.Compare(left, right))

                                    if actualOutputs <> expectedOutputs then
                                        Error
                                            "The item-template output no longer matches its preview."
                                    elif
                                        destinations
                                        |> Array.exists (fun (_, destination) ->
                                            not (ArtifactFiles.isUnder outputDirectory destination)
                                            || ArtifactFiles.exists destination)
                                    then
                                        Error
                                            "The item template would overwrite an existing artifact."
                                    else
                                        let missingDirectories =
                                            destinations
                                            |> Seq.collect (fun (_, destination) ->
                                                let rec parents current =
                                                    if
                                                        String.Equals(
                                                            current,
                                                            outputDirectory,
                                                            StringComparison.Ordinal
                                                        )
                                                    then
                                                        []
                                                    elif
                                                        not (
                                                            ArtifactFiles.isUnder
                                                                outputDirectory
                                                                current
                                                        )
                                                    then
                                                        invalidOp
                                                            "An item-template output escaped its output directory."
                                                    else
                                                        let parent =
                                                            Path.GetDirectoryName current
                                                            |> Option.ofObj
                                                            |> Option.defaultValue outputDirectory

                                                        current :: parents parent

                                                Path.GetDirectoryName destination
                                                |> Option.ofObj
                                                |> Option.map parents
                                                |> Option.defaultValue [])
                                            |> Seq.filter (ArtifactFiles.exists >> not)
                                            |> Seq.distinct
                                            |> Seq.sortBy (fun path ->
                                                Path
                                                    .GetRelativePath(outputDirectory, path)
                                                    .Split(
                                                        Path.DirectorySeparatorChar,
                                                        StringSplitOptions.RemoveEmptyEntries
                                                    )
                                                    .Length,
                                                path)
                                            |> Seq.toArray

                                        let targetPaths =
                                            destinations
                                            |> Seq.map (snd >> WorkspaceArtifactPath.Create)
                                            |> Seq.append (
                                                missingDirectories
                                                |> Seq.map WorkspaceArtifactPath.Create
                                            )
                                            |> Seq.append [ projectPath ]
                                            |> ImmutableArray.CreateRange

                                        let external =
                                            not (
                                                ArtifactFiles.isUnder workspaceRoot outputDirectory
                                            )

                                        let publicationRequest =
                                            { CommandId = descriptor.Id
                                              Targets = targetPaths
                                              Arguments = request.Arguments
                                              ExpectedRevision =
                                                WorkspaceRevision.Create state.Revision
                                              Intents =
                                                if external then
                                                    ImmutableHashSet.Create
                                                        WorkspaceEditIntent.AccessExternalPath
                                                else
                                                    ImmutableHashSet<WorkspaceEditIntent>.Empty
                                              AuthorizedRoots =
                                                if external then
                                                    ImmutableArray.Create(
                                                        WorkspaceArtifactPath.Create workspaceRoot,
                                                        WorkspaceArtifactPath.Create outputDirectory
                                                    )
                                                else
                                                    ImmutableArray.Create(
                                                        WorkspaceArtifactPath.Create workspaceRoot
                                                    ) }

                                        let actions =
                                            Array.append
                                                (missingDirectories
                                                 |> Array.map WorkspaceEditAction.CreateDirectory)
                                                (destinations
                                                 |> Array.map (fun (source, destination) ->
                                                     WorkspaceEditAction.ReplaceFile(
                                                         destination,
                                                         File.ReadAllBytes source
                                                     )))

                                        ownedSnapshots <-
                                            destinations
                                            |> Seq.map (snd >> WorkspaceArtifactPath.Create)
                                            |> DotnetCommandCompensation.snapshotFiles

                                        ownedDirectories <- missingDirectories

                                        match coordinator.Prepare(publicationRequest, actions) with
                                        | Failure failure -> Error failure.Diagnostic.Message
                                        | Success preview ->
                                            match
                                                coordinator.Execute(
                                                    publicationRequest,
                                                    actions,
                                                    preview.Confirmation,
                                                    cancellationToken
                                                )
                                            with
                                            | Failure failure -> Error failure.Diagnostic.Message
                                            | Success(RolledBack failure) ->
                                                Error failure.Diagnostic.Message
                                            | Success Applied ->
                                                ownedSnapshots <-
                                                    captureExpectedFiles ownedSnapshots

                                                invalidationPaths <-
                                                    [| projectPath
                                                       WorkspaceArtifactPath.Create outputDirectory |]

                                                Ok()

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
                                    WorkspaceRpcNotifications.operationProgress
                                        state.Descriptor
                                        operationId
                                        (nextSequence ())
                                        revision
                                        "Starting dotnet command."
                                )

                            let canExecute =
                                match plannedCommand with
                                | None -> true
                                | Some plan ->
                                    let execution =
                                        coordinator.Execute(
                                            plannedRequest plan,
                                            plannedActions plan,
                                            WorkspaceEditConfirmation.Create confirmationToken.Value,
                                            linked.Token
                                        )

                                    match mutationFailure execution with
                                    | None -> true
                                    | Some failure ->
                                        outcome <-
                                            WorkspaceOperationCompletion.Failed(
                                                failure.Code.Value,
                                                failure.Diagnostic.Message
                                            )

                                        false

                            let! catalogIsCurrent =
                                if canExecute then
                                    let binding =
                                        match templateExecution with
                                        | Some(ProjectTemplate(binding, _))
                                        | Some(ItemTemplate(binding, _, _, _)) -> Some binding
                                        | None -> None

                                    match binding with
                                    | None -> Task.FromResult true
                                    | Some binding ->
                                        task {
                                            let! catalog =
                                                WorkspaceTemplateCatalog.readAsync
                                                    workspace
                                                    linked.Token

                                            match
                                                catalog
                                                |> Result.bind (
                                                    WorkspaceTemplateCatalog.validateBinding binding
                                                )
                                            with
                                            | Ok() -> return true
                                            | Error error ->
                                                outcome <-
                                                    WorkspaceOperationCompletion.Failed(
                                                        error.Code,
                                                        error.Message
                                                    )

                                                return false
                                        }
                                else
                                    Task.FromResult false

                            if canExecute && catalogIsCurrent then
                                invalidationPaths <-
                                    plannedCommand
                                    |> Option.map (plannedPaths >> Seq.toArray)
                                    |> Option.defaultValue Array.empty

                                if
                                    DotnetCommandCatalog.isPackageMutation descriptor.Id.Value
                                    || descriptor.Id.Value = "reference.add"
                                    || descriptor.Id.Value = "reference.remove"
                                then
                                    ownedSnapshots <-
                                        plannedCommand
                                        |> Option.map (
                                            plannedPaths >> DotnetCommandCompensation.snapshotFiles
                                        )
                                        |> Option.defaultValue Array.empty

                                if
                                    descriptor.Id.Value = "template.create"
                                    && not (DotnetCommandCatalog.isTemplateDryRun request)
                                then
                                    match templateExecution with
                                    | Some(ItemTemplate _) ->
                                        let staging =
                                            Path.Combine(
                                                Path.GetTempPath(),
                                                "dotnet-workspace-explorer",
                                                operationId
                                            )

                                        if ArtifactFiles.exists staging then
                                            invalidOp
                                                "The item-template staging path already exists."

                                        itemStagingDirectory <- Some staging
                                    | _ ->
                                        let output =
                                            DotnetCommandCatalog.templateOutput workspace request
                                            |> Result.defaultWith invalidOp

                                        templateBefore <-
                                            Some(DotnetCommandCompensation.outputSnapshot output)

                                        templateSolution <-
                                            DotnetCommandCompensation.snapshotFiles
                                                [| WorkspaceArtifactPath.Create
                                                       workspace.SolutionPath.Value |]

                                use outputWriter =
                                    new WorkspaceOperationNotifications(publish "stdout")

                                use errorWriter =
                                    new WorkspaceOperationNotifications(publish "stderr")

                                let effectiveArgv =
                                    match itemStagingDirectory with
                                    | Some staging -> stageArgv staging
                                    | None -> argv

                                let! executed =
                                    DirectCommandRunner.ExecuteAsync(
                                        effectiveArgv |> List.toArray,
                                        Human(outputWriter, errorWriter, false, false),
                                        linked.Token
                                    )

                                ownedSnapshots <- captureExpectedFiles ownedSnapshots

                                match templateBefore with
                                | Some before ->
                                    let after = DotnetCommandCompensation.outputSnapshot before.Root
                                    templateAfter <- Some after

                                    templateCreated <-
                                        DotnetCommandCompensation.newOutputRoot before after
                                | None -> ()

                                if operation.IsCancellationReserved && executed.Success then
                                    outcome <- WorkspaceOperationCompletion.Cancelled
                                elif not executed.Success then
                                    outcome <- brokerOutcome "The dotnet command failed." executed
                                else
                                    if
                                        DotnetCommandCatalog.requiresRestore
                                            descriptor.Id.Value
                                            request.Arguments
                                    then
                                        let project =
                                            argv
                                            |> List.skipWhile ((<>) "--project")
                                            |> List.tryItem 1

                                        match project with
                                        | Some project ->
                                            let! restored =
                                                DirectCommandRunner.ExecuteAsync(
                                                    [| "restore"; project |],
                                                    Human(outputWriter, errorWriter, false, false),
                                                    linked.Token
                                                )

                                            if not restored.Success then
                                                outcome <-
                                                    brokerOutcome
                                                        "The dotnet restore failed."
                                                        restored
                                        | None ->
                                            outcome <-
                                                WorkspaceOperationCompletion.Failed(
                                                    "internal_error",
                                                    "The project argument is unavailable."
                                                )

                                    if
                                        outcome = WorkspaceOperationCompletion.Succeeded
                                        && descriptor.Id.Value = "template.create"
                                        && not (DotnetCommandCatalog.isTemplateDryRun request)
                                    then
                                        match templateExecution, templateBefore, templateAfter with
                                        | Some(ItemTemplate(_,
                                                            projectPath,
                                                            outputDirectory,
                                                            expectedOutputs)),
                                          _,
                                          _ ->
                                            match itemStagingDirectory with
                                            | Some staging ->
                                                match
                                                    publishItemTemplate
                                                        projectPath
                                                        outputDirectory.Value
                                                        expectedOutputs
                                                        staging
                                                        linked.Token
                                                with
                                                | Ok() -> ()
                                                | Error message ->
                                                    outcome <-
                                                        WorkspaceOperationCompletion.Failed(
                                                            "template_item_publish_failed",
                                                            message
                                                        )
                                            | None ->
                                                outcome <-
                                                    WorkspaceOperationCompletion.Failed(
                                                        "template_output_unavailable",
                                                        "The item-template staging output is unavailable."
                                                    )
                                        | Some(ProjectTemplate(_, expectedOutputs)),
                                          Some before,
                                          Some after ->
                                            let expected =
                                                expectedOutputs
                                                |> Seq.map _.Value
                                                |> DotnetCommandCompensation.expectedOutputArtifacts
                                                    after.Root

                                            match
                                                DotnetCommandCompensation.outputArtifacts after.Root
                                            with
                                            | Error message ->
                                                outcome <-
                                                    WorkspaceOperationCompletion.Failed(
                                                        "template_output_changed",
                                                        message
                                                    )
                                            | Ok actual when actual <> expected ->
                                                outcome <-
                                                    WorkspaceOperationCompletion.Failed(
                                                        "template_output_changed",
                                                        "The project-template output no longer matches its complete preview."
                                                    )
                                            | Ok _ ->
                                                match
                                                    DotnetCommandCompensation.newProjectFiles
                                                        before
                                                        after
                                                with
                                                | [| project |] ->
                                                    linked.Token.ThrowIfCancellationRequested()

                                                    let addRequest =
                                                        { CommandId =
                                                            CommandId.Create "solution.project.add"
                                                          TargetWorkspaceNodeId =
                                                            request.TargetWorkspaceNodeId
                                                          Arguments =
                                                            CommandArguments.Create
                                                                [ { ParameterId =
                                                                      CommandParameterId.Create
                                                                          "path"
                                                                    Value =
                                                                      CommandParameterValue.Path(
                                                                          WorkspaceArtifactPath.Create
                                                                              project
                                                                      ) } ]
                                                          ExpectedRevision =
                                                            WorkspaceRevision.Create state.Revision }

                                                    let! planned =
                                                        SolutionEditor.PlanAsync(
                                                            workspace,
                                                            addRequest,
                                                            linked.Token
                                                        )

                                                    match planned with
                                                    | Failure failure ->
                                                        outcome <-
                                                            WorkspaceOperationCompletion.Failed(
                                                                failure.Code.Value,
                                                                failure.Diagnostic.Message
                                                            )
                                                    | Success plan ->
                                                        let actions =
                                                            plannedActions (SolutionPlan plan)

                                                        match
                                                            coordinator.Prepare(
                                                                plan.Request,
                                                                actions
                                                            )
                                                        with
                                                        | Failure failure ->
                                                            outcome <-
                                                                WorkspaceOperationCompletion.Failed(
                                                                    failure.Code.Value,
                                                                    failure.Diagnostic.Message
                                                                )
                                                        | Success preview ->
                                                            match
                                                                coordinator.Execute(
                                                                    plan.Request,
                                                                    actions,
                                                                    preview.Confirmation,
                                                                    linked.Token
                                                                )
                                                            with
                                                            | Success Applied ->
                                                                transitionPublished <- true

                                                                invalidationPaths <-
                                                                    [| WorkspaceArtifactPath.Create
                                                                           workspace.SolutionPath.Value
                                                                       WorkspaceArtifactPath.Create
                                                                           before.Root
                                                                       WorkspaceArtifactPath.Create
                                                                           project |]
                                                            | Success(RolledBack failure)
                                                            | Failure failure ->
                                                                outcome <-
                                                                    WorkspaceOperationCompletion
                                                                        .Failed(
                                                                            failure.Code.Value,
                                                                            failure.Diagnostic.Message
                                                                        )
                                                | projects ->
                                                    outcome <-
                                                        WorkspaceOperationCompletion.Failed(
                                                            "template_project_count_invalid",
                                                            templateCountMessage projects.Length
                                                        )
                                        | _, Some before, Some after ->
                                            match
                                                DotnetCommandCompensation.newProjectFiles
                                                    before
                                                    after
                                            with
                                            | [| project |] ->
                                                linked.Token.ThrowIfCancellationRequested()

                                                let addRequest =
                                                    { CommandId =
                                                        CommandId.Create "solution.project.add"
                                                      TargetWorkspaceNodeId =
                                                        request.TargetWorkspaceNodeId
                                                      Arguments =
                                                        CommandArguments.Create
                                                            [ { ParameterId =
                                                                  CommandParameterId.Create "path"
                                                                Value =
                                                                  CommandParameterValue.Path(
                                                                      WorkspaceArtifactPath.Create
                                                                          project
                                                                  ) } ]
                                                      ExpectedRevision =
                                                        WorkspaceRevision.Create state.Revision }

                                                let! planned =
                                                    SolutionEditor.PlanAsync(
                                                        workspace,
                                                        addRequest,
                                                        linked.Token
                                                    )

                                                match planned with
                                                | Failure failure ->
                                                    outcome <-
                                                        WorkspaceOperationCompletion.Failed(
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
                                                            WorkspaceOperationCompletion.Failed(
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
                                                                       workspace.SolutionPath.Value
                                                                   WorkspaceArtifactPath.Create
                                                                       before.Root
                                                                   WorkspaceArtifactPath.Create
                                                                       project |]
                                                        | Some failure ->
                                                            outcome <-
                                                                WorkspaceOperationCompletion.Failed(
                                                                    failure.Code.Value,
                                                                    failure.Diagnostic.Message
                                                                )
                                            | projects ->
                                                let failureMessage =
                                                    templateCountMessage projects.Length

                                                outcome <-
                                                    WorkspaceOperationCompletion.Failed(
                                                        "template_project_count",
                                                        failureMessage
                                                    )
                                        | _ ->
                                            outcome <-
                                                WorkspaceOperationCompletion.Failed(
                                                    "template_output_unavailable",
                                                    "The template output could not be inspected."
                                                )

                                    if
                                        outcome = WorkspaceOperationCompletion.Succeeded
                                        && DotnetCommandCatalog.isPackageMutation
                                            descriptor.Id.Value
                                    then
                                        let project =
                                            argv
                                            |> List.skipWhile ((<>) "--project")
                                            |> List.tryItem 1

                                        match project with
                                        | Some project ->
                                            let root =
                                                Path.GetDirectoryName workspace.SolutionPath.Value
                                                |> Option.ofObj
                                                |> Option.defaultValue (
                                                    Directory.GetCurrentDirectory()
                                                )

                                            match CentralPackageVersions.normalize root project with
                                            | Error message ->
                                                outcome <-
                                                    WorkspaceOperationCompletion.Failed(
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
                                                    { CommandId = descriptor.Id
                                                      Targets = targets
                                                      Arguments = request.Arguments
                                                      ExpectedRevision =
                                                        WorkspaceRevision.Create state.Revision
                                                      Intents =
                                                        ImmutableHashSet.Create
                                                            WorkspaceEditIntent.Overwrite
                                                      AuthorizedRoots =
                                                        ImmutableArray.Create(
                                                            WorkspaceArtifactPath.Create root
                                                        ) }

                                                let actions =
                                                    updates
                                                    |> Seq.map (fun (path, bytes) ->
                                                        WorkspaceEditAction.ReplaceFile(
                                                            path,
                                                            bytes
                                                        ))

                                                match
                                                    coordinator.Prepare(
                                                        normalizationRequest,
                                                        actions
                                                    )
                                                with
                                                | Failure failure ->
                                                    outcome <-
                                                        WorkspaceOperationCompletion.Failed(
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
                                                            WorkspaceOperationCompletion.Failed(
                                                                failure.Code.Value,
                                                                failure.Diagnostic.Message
                                                            )
                                        | None ->
                                            outcome <-
                                                WorkspaceOperationCompletion.Failed(
                                                    "internal_error",
                                                    "The project argument is unavailable."
                                                )

                                    if
                                        outcome = WorkspaceOperationCompletion.Succeeded
                                        && DotnetCommandCatalog.isMutation descriptor.Id.Value
                                        && not (DotnetCommandCatalog.isTemplateDryRun request)
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
                                                    let encoded =
                                                        MessagePackRpcCodec.encodeFrame
                                                            notification

                                                    encoded.Length > context.MaximumFrameBytes())

                                            let! effectiveInvalidation =
                                                if frameTooLarge then
                                                    task {
                                                        let! reset =
                                                            resetForFramePressure
                                                                state
                                                                CancellationToken.None

                                                        return
                                                            WorkspaceProjectInvalidationResult.Reset
                                                                reset
                                                    }
                                                else
                                                    Task.FromResult invalidated

                                            let reset =
                                                match effectiveInvalidation with
                                                | WorkspaceProjectInvalidationResult.Reset _ -> true
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
                                            outcome <- WorkspaceOperationCompletion.Cancelled
                        with
                        | :? OperationCanceledException ->
                            if operation.IsCancellationReserved then
                                do! operation.WaitForCancellationResponseAsync()

                            outcome <- WorkspaceOperationCompletion.Cancelled
                        | :? IOException as error ->
                            outcome <-
                                WorkspaceOperationCompletion.Failed("io_error", error.Message)
                        | error ->
                            outcome <-
                                WorkspaceOperationCompletion.Failed(
                                    "operation_failed",
                                    error.Message
                                )

                        compensateTemplate ()
                        compensateOwnedFiles ()
                        cleanupItemStaging ()

                        try
                            let! completedOutcome =
                                WorkspaceOperations.completedOutcome
                                    operation
                                    completionReserved
                                    outcome

                            do!
                                sink.WriteAsync(
                                    WorkspaceRpcNotifications.operationCompleted
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
                        { Result =
                            WorkspaceRpcResponses.commandOperationResult operationId state.Revision
                          Notifications = []
                          BackgroundWork = Some background
                          AfterResponse = None
                          StopAfterResponse = false }
        }
