namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc

type private WorkspaceExportAdmission =
    { Cancellation: CancellationTokenSource
      Completion: Task<Result<EvaluatedWorkspaceProject option, RpcError>> }

type private WorkspaceHydrationFailure =
    | StageFailure of RpcError
    | InvalidationFailure of WorkspaceFailure
    | ToolsetChanged

type internal WorkspaceIndex
    private
    (
        target: string,
        services: WorkspaceIndexServices,
        options: WorkspaceIndexOptions,
        initial: IndexedWorkspace
    ) =
    let gate = new SemaphoreSlim(1, 1)

    let caseSemantics =
        FileSystemCaseSensitivityDetector.DetectFromExistingPath
            initial.Workspace.Descriptor.Path.Value


    let insensitive = caseSemantics = FileSystemCaseSensitivity.Insensitive

    let pathKey (path: string) =
        let value = Path.GetFullPath path
        if insensitive then value.ToUpperInvariant() else value

    let mutable current = initial
    let mutable disposed = false

    let cancelledError =
        RpcErrors.create "cancelled" "The workspace operation was cancelled." None

    let failureError (failure: WorkspaceFailure) : RpcError =
        if failure.Code = WorkspaceErrorCode.Cancelled then
            cancelledError
        else
            WorkspaceRpcResponses.failureError failure

    let projectByKey (workspace: SolutionWorkspace) (key: string) =
        workspace.Contents.Projects
        |> Seq.tryFind (fun project -> pathKey project.Path.AbsolutePath.Value = key)

    let touch (key: string) (recency: string list) =
        key :: (recency |> List.filter ((<>) key))

    let evaluateWith
        evaluateAsync
        (workspace: SolutionWorkspace)
        (project: SolutionProject)
        (cancellationToken: CancellationToken)
        =
        task {
            let! outcome = evaluateAsync project.Path.AbsolutePath cancellationToken

            return
                match outcome with
                | Success snapshot ->
                    ExploredProjectProperties.declarations workspace.SolutionPath snapshot
                    |> Result.map (fun declared ->
                        { Snapshot = snapshot
                          DeclaredProperties = declared })
                    |> Result.mapError (fun message -> RpcErrors.create "project" message None)
                | Failure failure -> Error(failureError failure)
        }

    let evaluate
        (workspace: SolutionWorkspace)
        (project: SolutionProject)
        (cancellationToken: CancellationToken)
        =
        evaluateWith
            (fun projectPath token ->
                services.EvaluateAsync projectPath workspace.SolutionPath token)
            workspace
            project
            cancellationToken

    let invalidateProject (project: SolutionProject) (cancellationToken: CancellationToken) =
        task {
            let paths = ImmutableArray.Create project.Path.AbsolutePath
            let! outcome = services.InvalidateAsync paths cancellationToken
            let cancelled = cancellationToken.IsCancellationRequested

            return
                match outcome with
                | Success _ when cancelled -> Error cancelledError
                | Success _ -> Ok()
                | Failure failure -> Error(failureError failure)
        }

    let stageMaterialized
        (source: IndexedWorkspace)
        (workspace: SolutionWorkspace)
        (cancellationToken: CancellationToken)
        =
        task {
            let mutable result = Ok Map.empty

            for key in source.Recency |> List.rev do
                match result, projectByKey workspace key with
                | Ok values, Some project when not project.IsFilteredOut ->
                    let! evaluated = evaluate workspace project cancellationToken
                    result <- evaluated |> Result.map (fun snapshot -> values.Add(key, snapshot))
                | _ -> ()

            return result
        }

    let stageMaterializedAfterProjectInvalidation
        (source: IndexedWorkspace)
        (workspace: SolutionWorkspace)
        (cancellationToken: CancellationToken)
        =
        let retryPaths =
            source.Hydrated.Keys
            |> Seq.choose (projectByKey workspace)
            |> Seq.map _.Path.AbsolutePath
            |> ImmutableArray.CreateRange

        let rec stage retriesRemaining =
            task {
                try
                    let! staged = stageMaterialized source workspace cancellationToken

                    match staged with
                    | Error error when
                        (error.Code = WorkspaceErrorCode.InternalError.Value
                         || error.Code = WorkspaceErrorCode.InvalidInput.Value)
                        && retriesRemaining > 0
                        ->
                        if cancellationToken.IsCancellationRequested then
                            return Error(StageFailure cancelledError)
                        else
                            do! Task.Delay(10, cancellationToken)

                            let! invalidated = services.InvalidateAsync retryPaths cancellationToken

                            if cancellationToken.IsCancellationRequested then
                                return Error(StageFailure cancelledError)
                            else
                                match invalidated with
                                | Success ProjectEvaluationInvalidationKind.None
                                | Success ProjectEvaluationInvalidationKind.ProjectOrImport ->
                                    return! stage (retriesRemaining - 1)
                                | Success _ -> return Error ToolsetChanged
                                | Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                                    return Error(StageFailure cancelledError)
                                | Failure failure -> return Error(InvalidationFailure failure)
                    | Error error -> return Error(StageFailure error)
                    | Ok values -> return Ok values
                with :? OperationCanceledException ->
                    return Error(StageFailure cancelledError)
            }

        stage 20

    let applyCandidate (candidate: IndexedWorkspace) =
        let oldIndexedNodes = WorkspaceIndexDiff.placements insensitive current
        let newIndexedNodes = WorkspaceIndexDiff.placements insensitive candidate

        let semanticChanged =
            current.Hydrated.Count <> candidate.Hydrated.Count
            || current.Hydrated
               |> Seq.exists (fun (KeyValue(key, snapshot)) ->
                   match candidate.Hydrated.TryFind key with
                   | Some next ->
                       not (
                           WorkspaceIndexPure.sameSnapshot
                               current.Workspace.Descriptor
                               snapshot
                               next
                       )
                   | None -> true)

        let diagnostics =
            candidate.Hydrated.Values
            |> Seq.collect (fun hydrated -> hydrated.Snapshot.Diagnostics)
            |> Seq.sortBy (fun value -> value.Code.Value, value.Message)
            |> ImmutableArray.CreateRange

        match
            WorkspaceIndexDiff.diff
                current.Workspace.Descriptor.Id
                current.Revision
                oldIndexedNodes
                newIndexedNodes
        with
        | None when not semanticChanged ->
            current <-
                { candidate with
                    Revision = current.Revision }

            None
        | None ->
            let delta =
                { WorkspaceId = current.Workspace.Descriptor.Id
                  BaseRevision = WorkspaceRevision.Create current.Revision
                  NewRevision = WorkspaceRevision.Create(current.Revision + 1L)
                  Changes = ImmutableArray<WorkspaceChange>.Empty
                  Diagnostics = diagnostics }

            current <-
                { candidate with
                    Revision = delta.NewRevision.Value }

            Some delta
        | Some delta ->
            let withDiagnostics = { delta with Diagnostics = diagnostics }

            current <-
                { candidate with
                    Revision = withDiagnostics.NewRevision.Value }

            Some withDiagnostics

    let applyCandidateWithoutLazyBody before candidate =
        applyCandidate candidate
        |> Option.map (WorkspaceIndexDiff.omitLazyBodyChanges before)

    let resetUnsafe (diagnostic: WorkspaceDiagnostic) =
        task {
            try
                do! services.RefreshAsync()
            with _ ->
                ()

            let resetRevision = current.Revision + 1L

            current <-
                { current with
                    Hydrated = Map.empty
                    Recency = []
                    Revision = resetRevision
                    NeedsRebase = true }

            return
                { WorkspaceId = current.Workspace.Descriptor.Id
                  Revision = WorkspaceRevision.Create resetRevision
                  Diagnostics = ImmutableArray.Create diagnostic }
        }

    let ensureReadyUnsafe (cancellationToken: CancellationToken) =
        task {
            if not current.NeedsRebase then
                return Ok()
            else
                try
                    do! services.RefreshAsync()
                    cancellationToken.ThrowIfCancellationRequested()
                    let! opened = services.OpenAsync target cancellationToken

                    match opened with
                    | Success workspace ->
                        current <-
                            { Workspace = workspace
                              Hydrated = Map.empty
                              Recency = []
                              Revision = current.Revision
                              NeedsRebase = false }

                        return Ok()
                    | Failure failure -> return Error(failureError failure)
                with :? OperationCanceledException ->
                    return Error cancelledError
        }

    let uncertainty (code: string) (message: string) =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Warning,
            WorkspaceDiagnosticCode.Create code,
            message,
            true,
            CorrelationId.New()
        )

    member _.Descriptor = current.Workspace.Descriptor
    member _.Revision = current.Revision

    member _.WorkspaceAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let! ready = ensureReadyUnsafe cancellationToken

                return
                    ready
                    |> Result.map (fun () ->
                        let enrichments =
                            current.Workspace.Contents.Projects
                            |> Seq.choose (fun project ->
                                let key = pathKey project.Path.AbsolutePath.Value

                                current.Hydrated.TryFind key
                                |> Option.map (fun hydrated ->
                                    { ProjectId = project.Node.Id
                                      CapabilityProfile = hydrated.Snapshot.CapabilityProfile }))

                        SolutionWorkspaceCapabilities.EnrichProjectCapabilities(
                            current.Workspace,
                            enrichments
                        ))
            finally
                gate.Release() |> ignore
        }

    member _.ProjectAsync(projectId: WorkspaceNodeId, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let! ready = ensureReadyUnsafe cancellationToken

                match ready with
                | Error error ->
                    return
                        Failure(
                            Internal(
                                WorkspaceDiagnostic.CreateSimple(
                                    WorkspaceDiagnosticSeverity.Error,
                                    WorkspaceDiagnosticCode.Create error.Code,
                                    error.Message,
                                    false,
                                    CorrelationId.New()
                                )
                            )
                        )
                | Ok() ->
                    match
                        current.Workspace.Contents.Projects
                        |> Seq.tryFind (fun project ->
                            project.Node.Id = projectId && not project.IsFilteredOut)
                    with
                    | None ->
                        return
                            Failure(
                                NotFound(
                                    projectId.Value,
                                    WorkspaceDiagnostic.CreateSimple(
                                        WorkspaceDiagnosticSeverity.Error,
                                        WorkspaceDiagnosticCode.Create "not_found",
                                        "The project target was not found.",
                                        false,
                                        CorrelationId.New()
                                    )
                                )
                            )
                    | Some project ->
                        let! evaluated = evaluate current.Workspace project cancellationToken

                        return
                            evaluated
                            |> Result.map (fun hydrated ->
                                current.Workspace, project, hydrated.Snapshot)
                            |> function
                                | Ok value -> Success value
                                | Error error ->
                                    Failure(
                                        Internal(
                                            WorkspaceDiagnostic.CreateSimple(
                                                WorkspaceDiagnosticSeverity.Error,
                                                WorkspaceDiagnosticCode.Create error.Code,
                                                error.Message,
                                                false,
                                                CorrelationId.New()
                                            )
                                        )
                                    )
            finally
                gate.Release() |> ignore
        }

    member _.PathComparer =
        if insensitive then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    member _.RootAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let! ready = ensureReadyUnsafe cancellationToken

                return
                    ready
                    |> Result.map (fun () ->
                        let nodes =
                            WorkspaceIndexDiff.placements insensitive current
                            |> Seq.filter (fun value -> value.ParentWorkspaceNodeId.IsNone)
                            |> Seq.sortBy _.Index
                            |> Seq.map _.Node
                            |> ImmutableArray.CreateRange

                        current.Revision, nodes)
            finally
                gate.Release() |> ignore
        }

    member _.ChildrenAsync
        (
            parentNodeIdText: string,
            requestedPageSize: int option,
            negotiatedPageSize: int,
            continuation: string option,
            cancellationToken: CancellationToken
        ) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let! ready = ensureReadyUnsafe cancellationToken

                match ready with
                | Error error -> return Error error
                | Ok() ->
                    let workspaceId = current.Workspace.Descriptor.Id.Value

                    let offset =
                        match continuation with
                        | None -> Ok 0
                        | Some value ->
                            match WorkspacePageTokens.tryParse options.TokenSecret value with
                            | Some payload when payload.Revision <> current.Revision ->
                                Error(WorkspaceRpcResponses.workspaceConflict current.Revision)
                            | Some payload when
                                payload.WorkspaceId = workspaceId
                                && payload.ParentWorkspaceNodeId = parentNodeIdText
                                ->
                                Ok payload.Offset
                            | _ ->
                                Error(RpcErrors.invalidParams "The continuation token is invalid.")

                    match offset with
                    | Error error -> return Error error
                    | Ok pageOffset ->
                        let before = WorkspaceIndexDiff.placements insensitive current

                        match
                            before
                            |> Array.tryFind (fun value -> value.Node.Id.Value = parentNodeIdText)
                        with
                        | None ->
                            return
                                Error(
                                    RpcErrors.invalidParams
                                        "The requested workspace parent does not exist."
                                )
                        | Some parent ->
                            let project =
                                current.Workspace.Contents.Projects
                                |> Seq.tryFind (fun value -> value.Node.Id = parent.Node.Id)

                            let hydrate () =
                                task {
                                    match project with
                                    | Some value when not value.IsFilteredOut ->
                                        let key = pathKey value.Path.AbsolutePath.Value

                                        match current.Hydrated.TryFind key with
                                        | Some _ ->
                                            current <-
                                                { current with
                                                    Recency = touch key current.Recency }

                                            return Ok None
                                        | None ->
                                            let! evaluated =
                                                evaluate current.Workspace value cancellationToken

                                            match evaluated with
                                            | Error error -> return Error error
                                            | Ok snapshot ->
                                                if cancellationToken.IsCancellationRequested then
                                                    return Error cancelledError
                                                else
                                                    let hydrated =
                                                        current.Hydrated.Add(key, snapshot)

                                                    let recency = touch key current.Recency

                                                    let evicted =
                                                        if
                                                            hydrated.Count > options.HydrationLimit
                                                        then
                                                            Some(List.last recency)
                                                        else
                                                            None

                                                    let! invalidation =
                                                        match
                                                            evicted
                                                            |> Option.bind (
                                                                projectByKey current.Workspace
                                                            )
                                                        with
                                                        | Some evictedProject ->
                                                            invalidateProject
                                                                evictedProject
                                                                cancellationToken
                                                        | None -> Task.FromResult(Ok())

                                                    match invalidation with
                                                    | Error error -> return Error error
                                                    | Ok() ->
                                                        let candidate =
                                                            { current with
                                                                Hydrated =
                                                                    evicted
                                                                    |> Option.map hydrated.Remove
                                                                    |> Option.defaultValue hydrated
                                                                Recency =
                                                                    evicted
                                                                    |> Option.map (fun item ->
                                                                        recency
                                                                        |> List.filter ((<>) item))
                                                                    |> Option.defaultValue recency }

                                                        return
                                                            applyCandidateWithoutLazyBody
                                                                before
                                                                candidate
                                                            |> Ok
                                    | _ -> return Ok None
                                }

                            let! hydrated = hydrate ()

                            match hydrated with
                            | Error error -> return Error error
                            | Ok delta ->
                                let placements = WorkspaceIndexDiff.placements insensitive current

                                let actualParent =
                                    placements
                                    |> Array.find (fun value ->
                                        value.Node.Id.Value = parentNodeIdText)

                                let children =
                                    placements
                                    |> Seq.filter (fun value ->
                                        value.ParentWorkspaceNodeId = Some actualParent.Node.Id)
                                    |> Seq.sortBy _.Index
                                    |> Seq.toArray

                                let pageSize =
                                    requestedPageSize
                                    |> Option.defaultValue 256
                                    |> min 4096
                                    |> min negotiatedPageSize

                                let page =
                                    children
                                    |> Array.skip (min pageOffset children.Length)
                                    |> Array.truncate pageSize

                                let next =
                                    if pageOffset + page.Length < children.Length then
                                        let workspaceId = current.Workspace.Descriptor.Id.Value

                                        WorkspacePageTokens.create
                                            options.TokenSecret
                                            { WorkspaceId = workspaceId
                                              ParentWorkspaceNodeId = actualParent.Node.Id.Value
                                              Offset = pageOffset + page.Length
                                              Revision = current.Revision }
                                        |> WorkspacePageToken.Create
                                        |> Some
                                    else
                                        None

                                return
                                    Ok
                                        { Revision = current.Revision
                                          ParentWorkspaceNodeId = actualParent.Node.Id
                                          Nodes =
                                            page |> Seq.map _.Node |> ImmutableArray.CreateRange
                                          NextToken = next
                                          Delta = delta }
            finally
                gate.Release() |> ignore
        }

    member _.RefreshAsync(expectedRevision: int64 option, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                match expectedRevision with
                | Some expected when expected <> current.Revision ->
                    return Error(WorkspaceRpcResponses.workspaceConflict current.Revision)
                | _ ->
                    try
                        do! services.RefreshAsync()
                        cancellationToken.ThrowIfCancellationRequested()
                        let! opened = services.OpenAsync target cancellationToken

                        match opened with
                        | Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                            return Error cancelledError
                        | Failure failure ->
                            let! reset = resetUnsafe failure.Diagnostic

                            return
                                Ok
                                    { Revision = reset.Revision.Value
                                      Reset = true
                                      Delta = None
                                      ResetEvent = Some reset
                                      Diagnostics = reset.Diagnostics }
                        | Success workspace ->
                            let! hydrated = stageMaterialized current workspace cancellationToken

                            match hydrated with
                            | Error error when error.Code = "cancelled" -> return Error error
                            | Error _ ->
                                let! reset =
                                    resetUnsafe (
                                        uncertainty
                                            "workspace.refresh_unverified"
                                            "The workspace refresh could not be verified."
                                    )

                                return
                                    Ok
                                        { Revision = reset.Revision.Value
                                          Reset = true
                                          Delta = None
                                          ResetEvent = Some reset
                                          Diagnostics = reset.Diagnostics }
                            | Ok values ->
                                let recency = current.Recency |> List.filter values.ContainsKey

                                let delta =
                                    applyCandidate
                                        { Workspace = workspace
                                          Hydrated = values
                                          Recency = recency
                                          Revision = current.Revision
                                          NeedsRebase = false }

                                return
                                    Ok
                                        { Revision = current.Revision
                                          Reset = false
                                          Delta = delta
                                          ResetEvent = None
                                          Diagnostics = ImmutableArray<WorkspaceDiagnostic>.Empty }
                    with :? OperationCanceledException ->
                        return Error cancelledError
            finally
                gate.Release() |> ignore
        }

    member private _.InvalidateAsyncCore
        (retryMaterialized: bool)
        (paths: ImmutableArray<WorkspaceArtifactPath>, cancellationToken: CancellationToken)
        =
        task {
            do! gate.WaitAsync cancellationToken

            try
                if current.NeedsRebase then
                    return WorkspaceProjectInvalidationResult.None
                else
                    let! invalidated = services.InvalidateAsync paths cancellationToken

                    match invalidated with
                    | Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                        return WorkspaceProjectInvalidationResult.None
                    | Failure failure ->
                        let! reset = resetUnsafe failure.Diagnostic
                        return WorkspaceProjectInvalidationResult.Reset reset
                    | Success ProjectEvaluationInvalidationKind.DotnetSdkSelection ->
                        let! reset =
                            resetUnsafe (
                                uncertainty
                                    "workspace.toolset_changed"
                                    "The selected SDK changed; request a fresh workspace graph."
                            )

                        return WorkspaceProjectInvalidationResult.Reset reset
                    | Success kind ->
                        let touchesSolution =
                            paths
                            |> Seq.exists (fun path ->
                                pathKey path.Value = pathKey
                                    current.Workspace.Descriptor.Path.Value
                                || pathKey path.Value = pathKey
                                    current.Workspace.SolutionPath.Value)

                        if kind = ProjectEvaluationInvalidationKind.None && not touchesSolution then
                            return WorkspaceProjectInvalidationResult.None
                        else
                            let! opened =
                                if
                                    kind = ProjectEvaluationInvalidationKind.ProjectOrImport
                                    && not touchesSolution
                                then
                                    Task.FromResult(Success current.Workspace)
                                else
                                    services.OpenAsync target cancellationToken

                            match opened with
                            | Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                                return WorkspaceProjectInvalidationResult.None
                            | Failure failure ->
                                let! reset = resetUnsafe failure.Diagnostic
                                return WorkspaceProjectInvalidationResult.Reset reset
                            | Success workspace ->
                                let! hydrated =
                                    if
                                        retryMaterialized
                                        && kind = ProjectEvaluationInvalidationKind.ProjectOrImport
                                        && not touchesSolution
                                    then
                                        stageMaterializedAfterProjectInvalidation
                                            current
                                            workspace
                                            cancellationToken
                                    else
                                        task {
                                            let! staged =
                                                stageMaterialized
                                                    current
                                                    workspace
                                                    cancellationToken

                                            return staged |> Result.mapError StageFailure
                                        }

                                match hydrated with
                                | Error(StageFailure error) when error.Code = "cancelled" ->
                                    return WorkspaceProjectInvalidationResult.None
                                | Error(InvalidationFailure failure) ->
                                    let! reset = resetUnsafe failure.Diagnostic
                                    return WorkspaceProjectInvalidationResult.Reset reset
                                | Error ToolsetChanged ->
                                    let! reset =
                                        resetUnsafe (
                                            uncertainty
                                                "workspace.toolset_changed"
                                                "The selected SDK changed; request a fresh workspace graph."
                                        )

                                    return WorkspaceProjectInvalidationResult.Reset reset
                                | Error(StageFailure _) ->
                                    let! reset =
                                        resetUnsafe (
                                            uncertainty
                                                "workspace.watch_unverified"
                                                "The workspace change could not be verified."
                                        )

                                    return WorkspaceProjectInvalidationResult.Reset reset
                                | Ok values ->
                                    let recency = current.Recency |> List.filter values.ContainsKey

                                    let delta =
                                        applyCandidate
                                            { Workspace = workspace
                                              Hydrated = values
                                              Recency = recency
                                              Revision = current.Revision
                                              NeedsRebase = false }

                                    return
                                        delta
                                        |> Option.map WorkspaceProjectInvalidationResult.Delta
                                        |> Option.defaultValue
                                            WorkspaceProjectInvalidationResult.None
            finally
                gate.Release() |> ignore
        }

    member this.InvalidateAsync
        (paths: ImmutableArray<WorkspaceArtifactPath>, cancellationToken: CancellationToken)
        =
        this.InvalidateAsyncCore true (paths, cancellationToken)

    member this.InvalidateFromTransactionAsync
        (paths: seq<WorkspaceArtifactPath>, cancellationToken: CancellationToken)
        =
        task {
            let! invalidated =
                this.InvalidateAsyncCore false (ImmutableArray.CreateRange paths, cancellationToken)

            match invalidated with
            | WorkspaceProjectInvalidationResult.Delta _
            | WorkspaceProjectInvalidationResult.Reset _ -> return invalidated
            | WorkspaceProjectInvalidationResult.None ->
                do! gate.WaitAsync cancellationToken

                try
                    if current.NeedsRebase then
                        return WorkspaceProjectInvalidationResult.None
                    else
                        let nextRevision = current.Revision + 1L

                        current <- { current with Revision = nextRevision }

                        return
                            WorkspaceProjectInvalidationResult.Delta
                                { WorkspaceId = current.Workspace.Descriptor.Id
                                  BaseRevision = WorkspaceRevision.Create(current.Revision - 1L)
                                  NewRevision = WorkspaceRevision.Create nextRevision
                                  Changes = ImmutableArray<WorkspaceChange>.Empty
                                  Diagnostics = ImmutableArray<WorkspaceDiagnostic>.Empty }
                finally
                    gate.Release() |> ignore
        }

    member _.ResetAsync(diagnostic: WorkspaceDiagnostic, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                return! resetUnsafe diagnostic
            finally
                gate.Release() |> ignore
        }

    member _.ExportAsync
        (
            expectedRevision: int64,
            writeBatch: WorkspaceExportBatch -> Task<unit>,
            cancellationToken: CancellationToken
        ) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                if current.NeedsRebase || current.Revision <> expectedRevision then
                    return Error(WorkspaceRpcResponses.workspaceConflict current.Revision)
                else
                    let projects =
                        current.Workspace.Contents.Projects
                        |> Seq.sortBy (fun project -> pathKey project.Path.AbsolutePath.Value)
                        |> Seq.toArray

                    let staticBatchSize = 256
                    let staticBatch = ResizeArray<WorkspaceNode> staticBatchSize

                    for node in WorkspaceIndexPure.exportStaticNodes current.Workspace do
                        cancellationToken.ThrowIfCancellationRequested()

                        if staticBatch.Count = staticBatchSize then
                            do!
                                writeBatch
                                    { Nodes = staticBatch.ToArray()
                                      IsFinal = false }

                            staticBatch.Clear()

                        staticBatch.Add node

                    if staticBatch.Count > 0 then
                        do!
                            writeBatch
                                { Nodes = staticBatch.ToArray()
                                  IsFinal = projects.Length = 0 }

                    if staticBatch.Count = 0 && projects.Length = 0 then
                        do! writeBatch { Nodes = Array.empty; IsFinal = true }

                    let firstEvaluable =
                        projects
                        |> Array.tryFindIndex (fun project ->
                            not project.IsFilteredOut
                            && File.Exists project.Path.AbsolutePath.Value)

                    let firstMissing =
                        projects
                        |> Array.tryFindIndex (fun project ->
                            not project.IsFilteredOut
                            && not (File.Exists project.Path.AbsolutePath.Value))

                    let needsSession =
                        match firstEvaluable, firstMissing with
                        | Some evaluated, Some missing -> evaluated < missing
                        | Some _, None -> true
                        | None, _ -> false

                    let! openedSession =
                        task {
                            if needsSession then
                                let! opened =
                                    services.OpenExportSessionAsync
                                        current.Workspace.SolutionPath
                                        options.ExportCapacity
                                        cancellationToken

                                return
                                    match opened with
                                    | Success session -> Success(Some session)
                                    | Failure failure -> Failure failure
                            else
                                return Success None
                        }

                    match openedSession with
                    | Failure failure -> return Error(failureError failure)
                    | Success session ->
                        let admissions = Dictionary<int, WorkspaceExportAdmission>()
                        let admissionGate = obj ()
                        let mutable earliestFailure = Int32.MaxValue
                        let mutable nextAdmission = 0
                        let mutable nextEmission = 0

                        let recordFailure ordinal =
                            let later =
                                lock admissionGate (fun () ->
                                    if ordinal < earliestFailure then
                                        earliestFailure <- ordinal

                                        admissions
                                        |> Seq.choose (fun (KeyValue(index, admission)) ->
                                            if index > ordinal then
                                                Some admission.Cancellation
                                            else
                                                None)
                                        |> Seq.toArray
                                    else
                                        Array.empty)

                            for cancellation in later do
                                cancellation.Cancel()

                        let cancelAll () =
                            let cancellations =
                                lock admissionGate (fun () ->
                                    admissions.Values |> Seq.map _.Cancellation |> Seq.toArray)

                            for cancellation in cancellations do
                                cancellation.Cancel()

                        let admit ordinal =
                            let project = projects[ordinal]

                            let projectCancellation =
                                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                            let completion =
                                if project.IsFilteredOut then
                                    Task.FromResult(Ok None)
                                elif not (File.Exists project.Path.AbsolutePath.Value) then
                                    let message =
                                        $"Project '{project.Path.AbsolutePath.Value}' was not found."

                                    Task.FromResult(
                                        Error(RpcErrors.create "not_found" message None)
                                    )
                                else
                                    task {
                                        let! evaluated =
                                            evaluateWith
                                                session.Value.EvaluateAsync
                                                current.Workspace
                                                project
                                                projectCancellation.Token

                                        return evaluated |> Result.map Some
                                    }

                            let admission =
                                { Cancellation = projectCancellation
                                  Completion = completion }

                            lock admissionGate (fun () -> admissions.Add(ordinal, admission))

                            completion.ContinueWith(
                                (fun
                                    (completed:
                                        Task<Result<EvaluatedWorkspaceProject option, RpcError>>) ->
                                    match completed.Result with
                                    | Error _ -> recordFailure ordinal
                                    | Ok _ -> ()),
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default
                            )
                            |> ignore

                        let fillWindow () =
                            let admissionOpen () =
                                lock admissionGate (fun () -> earliestFailure = Int32.MaxValue)

                            let canAdmit () =
                                nextAdmission < projects.Length
                                && admissions.Count < options.ExportCapacity
                                && admissionOpen ()
                                && not cancellationToken.IsCancellationRequested

                            while canAdmit () do
                                admit nextAdmission
                                nextAdmission <- nextAdmission + 1

                        let runScheduler =
                            task {
                                fillWindow ()
                                let mutable result = Ok()

                                while nextEmission < projects.Length && result.IsOk do
                                    if cancellationToken.IsCancellationRequested then
                                        result <- Error cancelledError
                                    else
                                        let admission = admissions[nextEmission]
                                        let! completed = admission.Completion

                                        match completed with
                                        | Error error -> result <- Error error
                                        | Ok snapshot ->
                                            cancellationToken.ThrowIfCancellationRequested()

                                            do!
                                                writeBatch
                                                    { Nodes =
                                                        WorkspaceIndexPure.exportProjectNodes
                                                            current.Workspace.Descriptor
                                                            projects[nextEmission]
                                                            snapshot
                                                      IsFinal = nextEmission = projects.Length - 1 }

                                            lock admissionGate (fun () ->
                                                admissions.Remove nextEmission |> ignore)

                                            admission.Cancellation.Dispose()
                                            nextEmission <- nextEmission + 1
                                            fillWindow ()

                                return result
                            }

                        let! schedulerAttempt =
                            task {
                                try
                                    let! result = runScheduler
                                    return Choice1Of2 result
                                with exceptionValue ->
                                    return Choice2Of2 exceptionValue
                            }

                        cancelAll ()

                        let pending =
                            lock admissionGate (fun () ->
                                admissions.Values |> Seq.map _.Completion |> Seq.toArray)

                        let! settlementException =
                            task {
                                try
                                    if pending.Length > 0 then
                                        do! Task.WhenAll pending :> Task

                                    return None
                                with exceptionValue ->
                                    return Some exceptionValue
                            }

                        for admission in admissions.Values do
                            admission.Cancellation.Dispose()

                        admissions.Clear()

                        let! disposalException =
                            task {
                                try
                                    match session with
                                    | Some active -> do! active.DisposeAsync()
                                    | None -> ()

                                    return None
                                with exceptionValue ->
                                    return Some exceptionValue
                            }

                        match schedulerAttempt, settlementException, disposalException with
                        | Choice1Of2 result, None, None -> return result
                        | Choice2Of2(:? OperationCanceledException), _, _ ->
                            return Error cancelledError
                        | Choice2Of2 exceptionValue, _, _ -> return raise exceptionValue
                        | _, Some exceptionValue, _ -> return raise exceptionValue
                        | _, _, Some exceptionValue -> return raise exceptionValue
            finally
                gate.Release() |> ignore
        }

    member _.WatchPlanAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                return WorkspaceWatchPlan.watchPlan insensitive current
            finally
                gate.Release() |> ignore
        }

    member _.DisposeAsync() =
        task {
            do! gate.WaitAsync()

            try
                if not disposed then
                    disposed <- true
                    do! services.DisposeAsync()
            finally
                gate.Release() |> ignore
        }

    static member Create(target, workspace, services, options) =
        if
            options.HydrationLimit <= 0
            || options.ExportCapacity <= 0
            || isNull (box options.TokenSecret)
            || options.TokenSecret.Length < 16
        then
            invalidArg
                (nameof options)
                "Workspace options require positive hydration and export limits and a token secret."

        WorkspaceIndex(
            target,
            services,
            options,
            { Workspace = workspace
              Hydrated = Map.empty
              Recency = []
              Revision = workspace.Descriptor.Revision.Value
              NeedsRebase = false }
        )

    static member CreateProduction(target, workspace, exportCapacity) =
        let evaluator = new ProjectEvaluator()

        let services =
            { OpenAsync =
                fun path cancellationToken ->
                    SolutionWorkspaceReader.OpenAsync(path, cancellationToken)
              EvaluateAsync =
                fun project workspace cancellationToken ->
                    evaluator.EvaluateAsync(project, workspace, cancellationToken)
              InvalidateAsync =
                fun paths cancellationToken -> evaluator.InvalidateAsync(paths, cancellationToken)
              OpenExportSessionAsync =
                fun workspacePath capacity cancellationToken ->
                    task {
                        let! opened =
                            evaluator.OpenExportSessionAsync(
                                workspacePath,
                                capacity,
                                cancellationToken
                            )

                        return
                            match opened with
                            | Success session ->
                                Success
                                    { EvaluateAsync =
                                        fun projectPath token ->
                                            session.EvaluateAsync(projectPath, token)
                                      DisposeAsync = fun () -> session.DisposeAsync().AsTask() }
                            | Failure failure -> Failure failure
                    }
              RefreshAsync = evaluator.RefreshAsync
              DisposeAsync = fun () -> evaluator.DisposeAsync().AsTask() }

        if exportCapacity <= 0 then
            invalidArg (nameof exportCapacity) "Export capacity must be positive."

        WorkspaceIndex.Create(
            target,
            workspace,
            services,
            { HydrationLimit = 32
              ExportCapacity = exportCapacity
              TokenSecret = RandomNumberGenerator.GetBytes 32 }
        )
