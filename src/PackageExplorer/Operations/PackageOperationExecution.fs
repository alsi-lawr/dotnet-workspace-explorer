namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.WorkspaceEditing

type private FileImage =
    | Missing
    | Contents of byte array
    | Unsupported

type private OwnerSnapshot = { Path: string; Before: FileImage }

type private PlannedEntry =
    { Contract: PackageExecutionEntry
      OwnerFiles: string list
      Arguments: string array
      WorkingDirectory: string }

type private PlannedCommand =
    { Arguments: string array
      WorkingDirectory: string
      Entries: PlannedEntry list }

[<RequireQualifiedAccess>]
module internal PackageOperationExecution =
    type Ports =
        { ReadPrecondition: ReadPackagePreviewPrecondition
          ReadUpdateBatchPrecondition: ReadPackageUpdateBatchPrecondition
          RefreshInstalled: RefreshInstalledPackages
          RunCommand: RunDotnetPackageCommand }

    type ExecutionPorts =
        { Execute: ExecutePackageOperation
          ExecuteUpdateBatch: ExecutePackageUpdateBatch
          Cancel: CancelPackageWork }

    let private failure kind message retry recovery =
        PackageFailure.create kind message retry
        |> Result.defaultWith (failwithf "%A")
        |> PackageFailure.withRecovery recovery

    let private packageOf =
        function
        | RequestedPackageOperation.InstallLatest package
        | RequestedPackageOperation.InstallVersion(package, _)
        | RequestedPackageOperation.UpdateLatest package
        | RequestedPackageOperation.UpdateVersion(package, _)
        | RequestedPackageOperation.Uninstall package
        | RequestedPackageOperation.ConsolidateVersion(package, _) -> package

    let private targetSelector operation scope =
        match operation, scope with
        | _, PackageTargetScope.Project project -> Ok(project, None)
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          PackageTargetScope.Framework(project, framework) -> Ok(project, Some framework)
        | _, PackageTargetScope.Framework _ ->
            Error(
                PackageFailureKind.Unsupported,
                "Framework-specific package mutation is not supported for this operation by the selected SDK.",
                PackageFailureRetry.AfterUserAction
            )
        | _, PackageTargetScope.Runtime _ ->
            Error(
                PackageFailureKind.Unsupported,
                "Runtime-specific package mutation is not supported by the selected SDK.",
                PackageFailureRetry.AfterUserAction
            )

    let private selectedVersion target =
        match PackageTargetPreview.change target with
        | PackageTargetChange.Install(_, ProposedPackageState.Direct version)
        | PackageTargetChange.Install(_, ProposedPackageState.CentrallyManaged(version, _))
        | PackageTargetChange.Update(_, ProposedPackageState.Direct version)
        | PackageTargetChange.Update(_, ProposedPackageState.CentrallyManaged(version, _)) ->
            Some version
        | PackageTargetChange.Consolidate(_, _, Some(ProposedPackageState.Direct version))
        | PackageTargetChange.Consolidate(_,
                                          _,
                                          Some(ProposedPackageState.CentrallyManaged(version, _))) ->
            Some version
        | PackageTargetChange.Uninstall _
        | PackageTargetChange.Consolidate(_, _, None) -> None

    let private command
        (operation: RequestedPackageOperation)
        (package: PackageId)
        (target: PackageTargetPreview)
        =
        let scope = PackageTargetPreview.target target

        targetSelector operation scope
        |> Result.bind (fun (project, framework) ->
            let workingDirectory =
                Path.GetDirectoryName project.Value
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let version = selectedVersion target

            let arguments =
                match operation with
                | RequestedPackageOperation.InstallLatest _
                | RequestedPackageOperation.InstallVersion _ ->
                    version
                    |> Option.map (fun selected ->
                        [ yield "package"
                          yield "add"
                          yield package.Value
                          yield "--version"
                          yield selected.Value

                          match framework with
                          | Some value ->
                              yield "--framework"
                              yield value.Value
                          | None -> ()

                          yield "--project"
                          yield project.Value ]
                        |> List.toArray)
                | RequestedPackageOperation.UpdateLatest _
                | RequestedPackageOperation.UpdateVersion _
                | RequestedPackageOperation.ConsolidateVersion _ ->
                    version
                    |> Option.map (fun selected ->
                        [| "package"
                           "update"
                           $"{package.Value}@{selected.Value}"
                           "--project"
                           project.Value |])
                | RequestedPackageOperation.Uninstall _ ->
                    Some [| "package"; "remove"; package.Value; "--project"; project.Value |]

            arguments
            |> Option.map (fun argv ->
                { Contract =
                    { Package = package
                      Target = scope
                      State = PackageExecutionState.Unchanged }
                  OwnerFiles =
                    target
                    |> PackageTargetPreview.ownerFiles
                    |> NonEmptyList.toList
                    |> List.map Path.GetFullPath
                  Arguments = argv
                  WorkingDirectory = workingDirectory })
            |> Ok)

    let private commandKey (entry: PlannedEntry) =
        entry.WorkingDirectory, entry.Arguments |> String.concat "\u0000"

    let private groupCommands (entries: PlannedEntry option list) =
        entries
        |> List.choose id
        |> List.fold
            (fun groups entry ->
                match
                    groups
                    |> List.tryFindIndex (fun group ->
                        commandKey group.Entries.Head = commandKey entry)
                with
                | Some index ->
                    groups
                    |> List.mapi (fun candidate group ->
                        if candidate = index then
                            { group with
                                Entries = group.Entries @ [ entry ] }
                        else
                            group)
                | None ->
                    groups
                    @ [ { Arguments = entry.Arguments
                          WorkingDirectory = entry.WorkingDirectory
                          Entries = [ entry ] } ])
            []

    let private singlePlan preview =
        let operation = PackagePreview.operation preview
        let package = packageOf operation

        let targets = preview |> PackagePreview.targets |> NonEmptyList.toList

        let entries = targets |> List.map (command operation package)

        match
            entries
            |> List.tryPick (function
                | Error error -> Some error
                | _ -> None)
        with
        | Some error -> Error error
        | None ->
            let values =
                entries
                |> List.map (function
                    | Ok value -> value
                    | Error _ -> invalidOp "A validated package plan contains an error.")

            let unchanged =
                List.zip targets values
                |> List.choose (fun (target, entry: PlannedEntry option) ->
                    if entry.IsSome then
                        None
                    else
                        Some
                            { Package = package
                              Target = PackageTargetPreview.target target
                              State = PackageExecutionState.Unchanged })

            Ok(values |> groupCommands, unchanged)

    let private batchPlan preview =
        let updates = preview |> PackageUpdateBatchPreview.updates |> NonEmptyList.toList

        let entries =
            updates
            |> List.map (fun update ->
                let package = PackageUpdateTargetPreview.package update

                let operation =
                    match PackageUpdateTargetPreview.requestedVersion update with
                    | Some version -> RequestedPackageOperation.UpdateVersion(package, version)
                    | None -> RequestedPackageOperation.UpdateLatest package

                command operation package (PackageUpdateTargetPreview.target update))

        match
            entries
            |> List.tryPick (function
                | Error error -> Some error
                | _ -> None)
        with
        | Some error -> Error error
        | None ->
            let values =
                entries
                |> List.map (function
                    | Ok value -> value
                    | Error _ -> invalidOp "A validated package plan contains an error.")

            let unchanged =
                List.zip updates values
                |> List.choose (fun (update, entry: PlannedEntry option) ->
                    if entry.IsSome then
                        None
                    else
                        Some
                            { Package = PackageUpdateTargetPreview.package update
                              Target =
                                update
                                |> PackageUpdateTargetPreview.target
                                |> PackageTargetPreview.target
                              State = PackageExecutionState.Unchanged })

            Ok(values |> groupCommands, unchanged)

    let private allContracts (commands: PlannedCommand list) unchanged =
        (commands |> List.collect _.Entries |> List.map _.Contract) @ unchanged

    let private image path =
        if ArtifactFiles.isLink path then Unsupported
        elif File.Exists path then Contents(File.ReadAllBytes path)
        elif ArtifactFiles.exists path then Unsupported
        else Missing

    let private snapshotOwners paths =
        let snapshots =
            paths
            |> List.map Path.GetFullPath
            |> List.distinctBy ArtifactFiles.identity
            |> List.sortBy ArtifactFiles.identity
            |> List.map (fun path -> { Path = path; Before = image path })

        if snapshots |> List.exists (fun snapshot -> snapshot.Before = Unsupported) then
            Error "A previewed package owner is not a regular file."
        else
            Ok snapshots

    let private projectPath =
        function
        | PackageTargetScope.Project project
        | PackageTargetScope.Framework(project, _)
        | PackageTargetScope.Runtime(project, _, _) -> project.Value

    let private expectedRestorePaths commands =
        commands
        |> List.collect _.Entries
        |> List.map (_.Contract.Target >> projectPath >> Path.GetFullPath)
        |> List.distinctBy ArtifactFiles.identity
        |> List.collect (fun project ->
            let directory =
                Path.GetDirectoryName project
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let intermediate = Path.Combine(directory, "obj")
            let projectFileName = Path.GetFileName project

            [ Path.Combine(directory, "packages.lock.json")
              Path.Combine(intermediate, "project.assets.json")
              Path.Combine(intermediate, "project.nuget.cache")
              Path.Combine(intermediate, $"{projectFileName}.nuget.dgspec.json")
              Path.Combine(intermediate, $"{projectFileName}.nuget.g.props")
              Path.Combine(intermediate, $"{projectFileName}.nuget.g.targets") ])
        |> List.map ArtifactFiles.identity
        |> Set.ofList

    let private ignoredSourcePath root expectedOutputs path =
        let relative = Path.GetRelativePath(root, path)

        expectedOutputs |> Set.contains (ArtifactFiles.identity path)
        || relative.Split(
            [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
            StringSplitOptions.RemoveEmptyEntries
           )
           |> Array.exists (fun segment -> segment = ".git" || segment = ".agent-workspace")

    let private sourceSnapshot root ownerPaths expectedOutputs =
        try
            let ownerKeys = ownerPaths |> Seq.map ArtifactFiles.identity |> Set.ofSeq

            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            |> Seq.filter (ignoredSourcePath root expectedOutputs >> not)
            |> Seq.filter (fun path -> ownerKeys.Contains(ArtifactFiles.identity path) |> not)
            |> Seq.map (fun path ->
                ArtifactFiles.fingerprint path
                |> Result.map (fun value -> ArtifactFiles.identity path, value))
            |> Seq.fold
                (fun state item ->
                    state
                    |> Result.bind (fun values ->
                        item |> Result.map (fun value -> value :: values)))
                (Ok [])
            |> Result.map Map.ofList
        with
        | :? IOException
        | :? UnauthorizedAccessException ->
            Error "Workspace source files could not be inspected safely."

    let private stalePrecondition
        (expectedRevision: string)
        (expectedFingerprints: Map<string, string>)
        (current: PackagePreviewPrecondition)
        =
        if current.WorkspaceRevision <> expectedRevision then
            true
        else
            expectedFingerprints
            |> Map.exists (fun path expected ->
                match ArtifactFiles.fingerprint path with
                | Ok actual -> actual <> expected
                | Error _ -> true)

    let private writeImage snapshot =
        match snapshot.Before with
        | Unsupported -> false
        | Missing ->
            ArtifactFiles.remove snapshot.Path
            not (ArtifactFiles.exists snapshot.Path)
        | Contents contents ->
            let temporary = ArtifactFiles.temporaryBeside snapshot.Path "package-recovery"

            try
                File.WriteAllBytes(temporary, contents)
                File.Move(temporary, snapshot.Path, true)
                image snapshot.Path = snapshot.Before
            finally
                if File.Exists temporary then
                    File.Delete temporary

    let private recoverOwners snapshots =
        snapshots
        |> List.map (fun snapshot ->
            let before = snapshot.Before
            let current = image snapshot.Path

            if current = before then
                snapshot.Path, true
            elif current = Unsupported then
                snapshot.Path, false
            else
                try
                    snapshot.Path, writeImage snapshot
                with
                | :? IOException
                | :? UnauthorizedAccessException -> snapshot.Path, false)
        |> Map.ofList

    let private entryTouches (changedOwners: Set<string>) (entry: PlannedEntry) =
        entry.OwnerFiles
        |> List.exists (fun path -> changedOwners.Contains(ArtifactFiles.identity path))

    let private recoveryEntries
        commands
        unchanged
        executedCommandCount
        failedCommandIndex
        changedOwners
        recovery
        =
        commands
        |> List.mapi (fun commandIndex command ->
            command.Entries
            |> List.map (fun entry ->
                let wasAttempted =
                    commandIndex < executedCommandCount || Some commandIndex = failedCommandIndex

                let state =
                    if not wasAttempted then
                        PackageExecutionState.Unchanged
                    elif entryTouches changedOwners entry then
                        let safelyRecovered =
                            entry.OwnerFiles
                            |> List.forall (fun path ->
                                recovery
                                |> Map.tryFind (Path.GetFullPath path)
                                |> Option.defaultValue false)

                        if safelyRecovered then
                            PackageExecutionState.Compensated
                        else
                            PackageExecutionState.Uncertain
                    elif commandIndex < executedCommandCount then
                        PackageExecutionState.Compensated
                    else
                        PackageExecutionState.Unchanged

                { entry.Contract with State = state }))
        |> List.concat
        |> fun entries -> entries @ unchanged

    let private completedEntries commands unchanged =
        (commands
         |> List.collect _.Entries
         |> List.map (fun entry ->
             { entry.Contract with
                 State = PackageExecutionState.Completed }))
        @ unchanged

    let private changedOwners snapshots =
        snapshots
        |> List.choose (fun snapshot ->
            if image snapshot.Path = snapshot.Before then
                None
            else
                Some(ArtifactFiles.identity snapshot.Path))
        |> Set.ofList

    let private changedOwnerFiles snapshots =
        snapshots
        |> List.choose (fun snapshot ->
            if image snapshot.Path = snapshot.Before then
                None
            else
                Some snapshot.Path)

    let private mapCommandFailure
        (recovery: PackageExecutionEntry list)
        (commandFailure: DotnetPackageCommandFailure)
        =
        if
            recovery
            |> List.exists (fun entry -> entry.State = PackageExecutionState.Uncertain)
        then
            failure
                PackageFailureKind.PartialRecoveryRequired
                "A package owner could not be restored to its previewed state."
                PackageFailureRetry.AfterUserAction
                recovery
        else
            match commandFailure with
            | DotnetPackageCommandFailure.AuthenticationRequired ->
                failure
                    PackageFailureKind.AuthenticationRequired
                    "The configured package source requires non-interactive credentials."
                    PackageFailureRetry.AfterUserAction
                    recovery
            | DotnetPackageCommandFailure.Unauthorized ->
                failure
                    PackageFailureKind.Unauthorized
                    "The configured credentials are not authorized for the package source."
                    PackageFailureRetry.AfterUserAction
                    recovery
            | DotnetPackageCommandFailure.Cancelled ->
                failure
                    PackageFailureKind.Cancelled
                    "The package operation was cancelled."
                    PackageFailureRetry.Never
                    recovery
            | DotnetPackageCommandFailure.TerminationUncertain ->
                failure
                    PackageFailureKind.PartialRecoveryRequired
                    "The package command exited, but child-process termination could not be confirmed."
                    PackageFailureRetry.AfterUserAction
                    recovery
            | DotnetPackageCommandFailure.Failed ->
                failure
                    PackageFailureKind.ExternalToolFailed
                    "The stock dotnet package command failed."
                    PackageFailureRetry.Transient
                    recovery
            | DotnetPackageCommandFailure.HostUnavailable ->
                failure
                    PackageFailureKind.ExternalToolFailed
                    "The dotnet host could not be started."
                    PackageFailureRetry.Transient
                    recovery

    let private report progress value =
        try
            progress value
        with _ ->
            ()

    let private progress operation stage completed total =
        PackageProgress.determinate operation stage completed total
        |> Result.defaultWith (failwithf "%A")

    let private executePlan
        (ports: Ports)
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (operations: ConcurrentDictionary<PackageOperationId, CancellationTokenSource>)
        (readCurrent: unit -> Async<Result<PackagePreviewPrecondition, PackageFailure>>)
        (expectedRevision: string)
        (expectedFingerprints: Map<string, string>)
        (workspaceTarget: PackageWorkspaceTarget)
        (requestId: PackageRequestId)
        (progressSink: PackageProgress -> unit)
        (commands: PlannedCommand list)
        (unchanged: PackageExecutionEntry list)
        =
        async {
            let operationId = PackageOperationId.newId ()
            use cancellation = new CancellationTokenSource()
            let unchangedAtFailure = allContracts commands unchanged
            let mutable cancellationRecovery = unchangedAtFailure

            if
                not (requests.TryAdd(requestId, cancellation))
                || not (operations.TryAdd(operationId, cancellation))
            then
                requests.TryRemove requestId |> ignore
                operations.TryRemove operationId |> ignore

                return
                    Error(
                        failure
                            PackageFailureKind.InvalidRequest
                            "The package request identifier is already active."
                            PackageFailureRetry.Never
                            unchangedAtFailure
                    )
            else
                try
                    try
                        report
                            progressSink
                            (PackageProgress.indeterminate
                                operationId
                                PackageOperationStage.Preparing)

                        cancellation.Token.ThrowIfCancellationRequested()
                        let! current = readCurrent ()
                        cancellation.Token.ThrowIfCancellationRequested()

                        match current with
                        | Error error ->
                            return Error(PackageFailure.withRecovery unchangedAtFailure error)
                        | Ok precondition when
                            stalePrecondition expectedRevision expectedFingerprints precondition
                            ->
                            return
                                Error(
                                    failure
                                        PackageFailureKind.StaleState
                                        "The package preview is stale; refresh and preview the operation again."
                                        PackageFailureRetry.AfterUserAction
                                        unchangedAtFailure
                                )
                        | Ok _ ->
                            if commands.IsEmpty then
                                report
                                    progressSink
                                    (PackageProgress.indeterminate
                                        operationId
                                        PackageOperationStage.Completed)

                                return
                                    Ok
                                        { Operation = operationId
                                          Entries = unchanged
                                          ChangedFiles = []
                                          Restore = PackageRestoreOutcome.NotRequired }
                            else
                                let ownerPaths =
                                    expectedFingerprints
                                    |> Map.keys
                                    |> Seq.map Path.GetFullPath
                                    |> Seq.toList

                                match snapshotOwners ownerPaths with
                                | Error message ->
                                    return
                                        Error(
                                            failure
                                                PackageFailureKind.Unsupported
                                                message
                                                PackageFailureRetry.AfterUserAction
                                                unchangedAtFailure
                                        )
                                | Ok ownerSnapshots ->
                                    let workspacePath = PackageWorkspaceTarget.path workspaceTarget

                                    let workspaceRoot =
                                        if Directory.Exists workspacePath then
                                            Path.GetFullPath workspacePath
                                        else
                                            Path.GetDirectoryName(Path.GetFullPath workspacePath)
                                            |> Option.ofObj
                                            |> Option.defaultValue (Directory.GetCurrentDirectory())

                                    let expectedOutputs = expectedRestorePaths commands

                                    match
                                        sourceSnapshot workspaceRoot ownerPaths expectedOutputs
                                    with
                                    | Error message ->
                                        return
                                            Error(
                                                failure
                                                    PackageFailureKind.Unsupported
                                                    message
                                                    PackageFailureRetry.AfterUserAction
                                                    unchangedAtFailure
                                            )
                                    | Ok sourceBefore ->
                                        let total = max 1 commands.Length
                                        let mutable completed = 0
                                        let mutable commandFailure = None
                                        let mutable unexpectedChange = None

                                        for index, command in commands |> List.indexed do
                                            if commandFailure.IsNone && unexpectedChange.IsNone then
                                                report
                                                    progressSink
                                                    (progress
                                                        operationId
                                                        PackageOperationStage.Applying
                                                        completed
                                                        total)

                                                let! result =
                                                    ports.RunCommand
                                                        command.WorkingDirectory
                                                        command.Arguments
                                                        cancellation.Token

                                                match result with
                                                | Ok() -> completed <- completed + 1
                                                | Error error ->
                                                    commandFailure <- Some(index, error)

                                                match
                                                    sourceSnapshot
                                                        workspaceRoot
                                                        ownerPaths
                                                        expectedOutputs
                                                with
                                                | Error _ -> unexpectedChange <- Some index
                                                | Ok currentSources when
                                                    currentSources <> sourceBefore
                                                    ->
                                                    unexpectedChange <- Some index
                                                | Ok _ -> ()

                                        match unexpectedChange, commandFailure with
                                        | Some attemptedIndex, _ ->
                                            let changed = changedOwners ownerSnapshots
                                            let recovery = recoverOwners ownerSnapshots

                                            let entries =
                                                recoveryEntries
                                                    commands
                                                    unchanged
                                                    completed
                                                    (Some attemptedIndex)
                                                    changed
                                                    recovery

                                            return
                                                Error(
                                                    failure
                                                        PackageFailureKind.PartialRecoveryRequired
                                                        "The package command changed an unpreviewed workspace source file."
                                                        PackageFailureRetry.AfterUserAction
                                                        entries
                                                )
                                        | None, Some(failedIndex, commandError) ->
                                            let changed = changedOwners ownerSnapshots
                                            let recovery = recoverOwners ownerSnapshots

                                            let entries =
                                                recoveryEntries
                                                    commands
                                                    unchanged
                                                    completed
                                                    (Some failedIndex)
                                                    changed
                                                    recovery

                                            return Error(mapCommandFailure entries commandError)
                                        | None, None ->
                                            cancellation.Token.ThrowIfCancellationRequested()

                                            cancellationRecovery <-
                                                completedEntries commands unchanged

                                            report
                                                progressSink
                                                (PackageProgress.indeterminate
                                                    operationId
                                                    PackageOperationStage.Restoring)

                                            let refreshRequest =
                                                { Id = PackageRequestId.newId ()
                                                  Target = workspaceTarget
                                                  Value = () }

                                            let refresh =
                                                Async.StartAsTask(
                                                    ports.RefreshInstalled
                                                        refreshRequest
                                                        (fun _ _ -> async.Return()),
                                                    cancellationToken = cancellation.Token
                                                )

                                            let! refreshed = refresh |> Async.AwaitTask
                                            cancellation.Token.ThrowIfCancellationRequested()
                                            let entries = completedEntries commands unchanged

                                            match refreshed with
                                            | Error error ->
                                                return
                                                    Error(PackageFailure.withRecovery entries error)
                                            | Ok _ ->
                                                report
                                                    progressSink
                                                    (PackageProgress.indeterminate
                                                        operationId
                                                        PackageOperationStage.Refreshing)

                                                report
                                                    progressSink
                                                    (PackageProgress.indeterminate
                                                        operationId
                                                        PackageOperationStage.Completed)

                                                return
                                                    Ok
                                                        { Operation = operationId
                                                          Entries = entries
                                                          ChangedFiles =
                                                            changedOwnerFiles ownerSnapshots
                                                          Restore = PackageRestoreOutcome.Completed }
                    with :? OperationCanceledException ->
                        return
                            Error(
                                failure
                                    PackageFailureKind.Cancelled
                                    "The package operation was cancelled."
                                    PackageFailureRetry.Never
                                    cancellationRecovery
                            )
                finally
                    requests.TryRemove requestId |> ignore
                    operations.TryRemove operationId |> ignore
        }

    let createWith
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (operations: ConcurrentDictionary<PackageOperationId, CancellationTokenSource>)
        (ports: Ports)
        =
        let execute (request: PackageRequest<PackageConfirmation>) progressSink =
            async {
                let preview = PackageConfirmation.preview request.Value

                match singlePlan preview with
                | Error(kind, message, retry) ->
                    let entries =
                        preview
                        |> PackagePreview.targets
                        |> NonEmptyList.toList
                        |> List.map (fun target ->
                            { Package = preview |> PackagePreview.operation |> packageOf
                              Target = PackageTargetPreview.target target
                              State = PackageExecutionState.Unchanged })

                    return Error(failure kind message retry entries)
                | Ok(commands, unchanged) ->
                    let targets =
                        preview
                        |> PackagePreview.targets
                        |> NonEmptyList.map PackageTargetPreview.target

                    let readCurrent () =
                        ports.ReadPrecondition
                            { Id = PackageRequestId.newId ()
                              Target = request.Target
                              Value =
                                { Operation = PackagePreview.operation preview
                                  Targets = targets
                                  BrowseSource = None } }

                    return!
                        executePlan
                            ports
                            requests
                            operations
                            readCurrent
                            (PackagePreview.workspaceRevision preview)
                            (PackagePreview.fileFingerprints preview)
                            request.Target
                            request.Id
                            progressSink
                            commands
                            unchanged
            }

        let executeUpdateBatch
            (request: PackageRequest<PackageUpdateBatchConfirmation>)
            progressSink
            =
            async {
                let preview = PackageUpdateBatchConfirmation.preview request.Value

                match batchPlan preview with
                | Error(kind, message, retry) ->
                    let entries =
                        preview
                        |> PackageUpdateBatchPreview.updates
                        |> NonEmptyList.toList
                        |> List.map (fun update ->
                            { Package = PackageUpdateTargetPreview.package update
                              Target =
                                update
                                |> PackageUpdateTargetPreview.target
                                |> PackageTargetPreview.target
                              State = PackageExecutionState.Unchanged })

                    return Error(failure kind message retry entries)
                | Ok(commands, unchanged) ->
                    let updates =
                        preview
                        |> PackageUpdateBatchPreview.updates
                        |> NonEmptyList.map (fun update ->
                            match PackageUpdateTargetPreview.requestedVersion update with
                            | Some version ->
                                PackageUpdateSelection.version
                                    (PackageUpdateTargetPreview.package update)
                                    version
                                    (update
                                     |> PackageUpdateTargetPreview.target
                                     |> PackageTargetPreview.target)
                            | None ->
                                PackageUpdateSelection.latest
                                    (PackageUpdateTargetPreview.package update)
                                    (update
                                     |> PackageUpdateTargetPreview.target
                                     |> PackageTargetPreview.target))

                    let readCurrent () =
                        ports.ReadUpdateBatchPrecondition
                            { Id = PackageRequestId.newId ()
                              Target = request.Target
                              Value =
                                { Updates = updates
                                  BrowseSource = None } }

                    return!
                        executePlan
                            ports
                            requests
                            operations
                            readCurrent
                            (PackageUpdateBatchPreview.workspaceRevision preview)
                            (PackageUpdateBatchPreview.fileFingerprints preview)
                            request.Target
                            request.Id
                            progressSink
                            commands
                            unchanged
            }

        let cancel cancellation =
            async {
                match cancellation with
                | PackageCancellation.Request request ->
                    match requests.TryGetValue request with
                    | true, active -> active.Cancel()
                    | _ -> ()
                | PackageCancellation.Operation operation ->
                    match operations.TryGetValue operation with
                    | true, active -> active.Cancel()
                    | _ -> ()
            }

        { Execute = execute
          ExecuteUpdateBatch = executeUpdateBatch
          Cancel = cancel }
