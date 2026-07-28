namespace Dotnet.CLI.Plus

open System
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport

type internal WorkspaceStateServices =
    { OpenAsync: string -> CancellationToken -> Task<WorkspaceOutcome<SolutionWorkspace>>
      EvaluateAsync:
          WorkspaceArtifactPath
              -> WorkspaceArtifactPath
              -> CancellationToken
              -> Task<WorkspaceOutcome<EvaluationSnapshot>>
      InvalidateAsync:
          ImmutableArray<WorkspaceArtifactPath>
              -> CancellationToken
              -> Task<WorkspaceOutcome<MsBuildInvalidationKind>>
      RefreshAsync: unit -> Task
      DisposeAsync: unit -> Task }

type internal WorkspaceStateOptions =
    { HydrationLimit: int
      TokenSecret: byte array }

type internal WorkspacePageResult =
    { Revision: int64
      ParentId: NodeId
      Nodes: ImmutableArray<WorkspaceNode>
      NextToken: ContinuationToken option
      Delta: WorkspaceDelta option }

type internal WorkspaceRefreshResult =
    { Revision: int64
      Reset: bool
      Delta: WorkspaceDelta option
      ResetEvent: WorkspaceReset option
      Diagnostics: ImmutableArray<WorkspaceDiagnostic> }

type internal WorkspaceExportBatch =
    { Nodes: WorkspaceNode array
      IsFinal: bool }

[<RequireQualifiedAccess>]
type internal WorkspaceInvalidationResult =
    | None
    | Delta of WorkspaceDelta
    | Reset of WorkspaceReset


type internal WorkspaceState
    private
    (
        target: string,
        services: WorkspaceStateServices,
        options: WorkspaceStateOptions,
        initial: WorkspaceData
    ) =
    let gate = new SemaphoreSlim(1, 1)

    let caseSemantics =
        HostFileSystemCaseDetector.DetectFromExistingPath
            initial.Workspace.WorkspaceDescriptor.Path.Value


    let insensitive = caseSemantics = HostFileSystemCaseSemantics.Insensitive

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
            PublicProtocol.failureError failure

    let projectByKey (workspace: SolutionWorkspace) (key: string) =
        workspace.RootProjection.Projects
        |> Seq.tryFind (fun project -> pathKey project.Path.AbsolutePath.Value = key)

    let touch (key: string) (recency: string list) =
        key :: (recency |> List.filter ((<>) key))

    let evaluate
        (workspace: SolutionWorkspace)
        (project: SolutionProjectProjection)
        (cancellationToken: CancellationToken)
        =
        task {
            let! outcome =
                services.EvaluateAsync
                    project.Path.AbsolutePath
                    workspace.BackingPath
                    cancellationToken

            return
                match outcome with
                | Success snapshot ->
                    ProjectPropertyRegistry.declarations workspace.BackingPath snapshot
                    |> Result.map (fun declared ->
                        { Snapshot = snapshot
                          DeclaredProperties = declared })
                    |> Result.mapError (fun message -> RpcErrors.create "project" message None)
                | Failure failure -> Error(failureError failure)
        }

    let invalidateProject
        (project: SolutionProjectProjection)
        (cancellationToken: CancellationToken)
        =
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
        (source: WorkspaceData)
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

    let applyCandidate (candidate: WorkspaceData) =
        let oldPlacements = WorkspaceStatePure.placements insensitive current
        let newPlacements = WorkspaceStatePure.placements insensitive candidate

        let semanticChanged =
            current.Hydrated.Count <> candidate.Hydrated.Count
            || current.Hydrated
               |> Seq.exists (fun (KeyValue(key, snapshot)) ->
                   match candidate.Hydrated.TryFind key with
                   | Some next ->
                       not (
                           WorkspaceStatePure.sameSnapshot
                               current.Workspace.WorkspaceDescriptor
                               snapshot
                               next
                       )
                   | None -> true)

        let diagnostics =
            candidate.Hydrated.Values
            |> Seq.collect (fun hydrated -> hydrated.Snapshot.Diagnostics)
            |> Seq.sortBy (fun value -> value.DiagnosticCode.Value, value.Message)
            |> ImmutableArray.CreateRange

        match
            WorkspaceStatePure.diff
                current.Workspace.WorkspaceDescriptor.WorkspaceId
                current.Revision
                oldPlacements
                newPlacements
        with
        | None when not semanticChanged ->
            current <-
                { candidate with
                    Revision = current.Revision }

            None
        | None ->
            let delta =
                { WorkspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId
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
        |> Option.map (WorkspaceStatePure.omitLazyBodyChanges before)

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
                { WorkspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId
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

    member _.Descriptor = current.Workspace.WorkspaceDescriptor
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
                            current.Workspace.RootProjection.Projects
                            |> Seq.choose (fun project ->
                                let key = pathKey project.Path.AbsolutePath.Value

                                current.Hydrated.TryFind key
                                |> Option.map (fun hydrated ->
                                    { ProjectId = project.Node.NodeId
                                      CapabilityProfile = hydrated.Snapshot.CapabilityProfile }))

                        SolutionProjection.EnrichProjectCapabilities(
                            current.Workspace,
                            enrichments
                        ))
            finally
                gate.Release() |> ignore
        }

    member _.ProjectAsync(projectId: NodeId, cancellationToken: CancellationToken) =
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
                        current.Workspace.RootProjection.Projects
                        |> Seq.tryFind (fun project ->
                            project.Node.NodeId = projectId && not project.IsFilteredOut)
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
                            WorkspaceStatePure.placements insensitive current
                            |> Seq.filter (fun value -> value.ParentId.IsNone)
                            |> Seq.sortBy _.Index
                            |> Seq.map _.Node
                            |> ImmutableArray.CreateRange

                        current.Revision, nodes)
            finally
                gate.Release() |> ignore
        }

    member _.ChildrenAsync
        (
            parentIdText: string,
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
                    let workspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId.Value

                    let offset =
                        match continuation with
                        | None -> Ok 0
                        | Some value ->
                            match ContinuationTokens.tryParse options.TokenSecret value with
                            | Some payload when payload.Revision <> current.Revision ->
                                Error(PublicProtocol.workspaceConflict current.Revision)
                            | Some payload when
                                payload.WorkspaceId = workspaceId && payload.ParentId = parentIdText
                                ->
                                Ok payload.Offset
                            | _ ->
                                Error(RpcErrors.invalidParams "The continuation token is invalid.")

                    match offset with
                    | Error error -> return Error error
                    | Ok pageOffset ->
                        let before = WorkspaceStatePure.placements insensitive current

                        match
                            before
                            |> Array.tryFind (fun value -> value.Node.NodeId.Value = parentIdText)
                        with
                        | None ->
                            return
                                Error(
                                    RpcErrors.invalidParams
                                        "The requested workspace parent does not exist."
                                )
                        | Some parent ->
                            let project =
                                current.Workspace.RootProjection.Projects
                                |> Seq.tryFind (fun value -> value.Node.NodeId = parent.Node.NodeId)

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
                                let placements = WorkspaceStatePure.placements insensitive current

                                let actualParent =
                                    placements
                                    |> Array.find (fun value ->
                                        value.Node.NodeId.Value = parentIdText)

                                let children =
                                    placements
                                    |> Seq.filter (fun value ->
                                        value.ParentId = Some actualParent.Node.NodeId)
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
                                        let workspaceId =
                                            current.Workspace.WorkspaceDescriptor.WorkspaceId.Value

                                        ContinuationTokens.create
                                            options.TokenSecret
                                            { WorkspaceId = workspaceId
                                              ParentId = actualParent.Node.NodeId.Value
                                              Offset = pageOffset + page.Length
                                              Revision = current.Revision }
                                        |> ContinuationToken.Create
                                        |> Some
                                    else
                                        None

                                return
                                    Ok
                                        { Revision = current.Revision
                                          ParentId = actualParent.Node.NodeId
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
                    return Error(PublicProtocol.workspaceConflict current.Revision)
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

    member _.InvalidateAsync
        (paths: ImmutableArray<WorkspaceArtifactPath>, cancellationToken: CancellationToken)
        =
        task {
            do! gate.WaitAsync cancellationToken

            try
                if current.NeedsRebase then
                    return WorkspaceInvalidationResult.None
                else
                    let! invalidated = services.InvalidateAsync paths cancellationToken

                    match invalidated with
                    | Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                        return WorkspaceInvalidationResult.None
                    | Failure failure ->
                        let! reset = resetUnsafe failure.Diagnostic
                        return WorkspaceInvalidationResult.Reset reset
                    | Success MsBuildInvalidationKind.ToolsetSelection ->
                        let! reset =
                            resetUnsafe (
                                uncertainty
                                    "workspace.toolset_changed"
                                    "The selected SDK changed; request a fresh workspace graph."
                            )

                        return WorkspaceInvalidationResult.Reset reset
                    | Success kind ->
                        let touchesSolution =
                            paths
                            |> Seq.exists (fun path ->
                                pathKey path.Value = pathKey
                                    current.Workspace.WorkspaceDescriptor.Path.Value
                                || pathKey path.Value = pathKey current.Workspace.BackingPath.Value)

                        if kind = MsBuildInvalidationKind.None && not touchesSolution then
                            return WorkspaceInvalidationResult.None
                        else
                            let! opened = services.OpenAsync target cancellationToken

                            match opened with
                            | Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                                return WorkspaceInvalidationResult.None
                            | Failure failure ->
                                let! reset = resetUnsafe failure.Diagnostic
                                return WorkspaceInvalidationResult.Reset reset
                            | Success workspace ->
                                let! hydrated =
                                    stageMaterialized current workspace cancellationToken

                                match hydrated with
                                | Error error when error.Code = "cancelled" ->
                                    return WorkspaceInvalidationResult.None
                                | Error _ ->
                                    let! reset =
                                        resetUnsafe (
                                            uncertainty
                                                "workspace.watch_unverified"
                                                "The workspace change could not be verified."
                                        )

                                    return WorkspaceInvalidationResult.Reset reset
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
                                        |> Option.map WorkspaceInvalidationResult.Delta
                                        |> Option.defaultValue WorkspaceInvalidationResult.None
            finally
                gate.Release() |> ignore
        }

    member this.InvalidateFromTransactionAsync
        (paths: seq<WorkspaceArtifactPath>, cancellationToken: CancellationToken)
        =
        task {
            let! invalidated =
                this.InvalidateAsync(ImmutableArray.CreateRange paths, cancellationToken)

            match invalidated with
            | WorkspaceInvalidationResult.Delta _
            | WorkspaceInvalidationResult.Reset _ -> return invalidated
            | WorkspaceInvalidationResult.None ->
                do! gate.WaitAsync cancellationToken

                try
                    if current.NeedsRebase then
                        return WorkspaceInvalidationResult.None
                    else
                        let nextRevision = current.Revision + 1L

                        current <- { current with Revision = nextRevision }

                        return
                            WorkspaceInvalidationResult.Delta
                                { WorkspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId
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
                    return Error(PublicProtocol.workspaceConflict current.Revision)
                else
                    let projects =
                        current.Workspace.RootProjection.Projects
                        |> Seq.sortBy (fun project -> pathKey project.Path.AbsolutePath.Value)
                        |> Seq.toArray

                    let staticBatchSize = 256
                    let staticBatch = ResizeArray<WorkspaceNode> staticBatchSize

                    for node in WorkspaceStatePure.exportStaticNodes current.Workspace do
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

                    let mutable failure = None

                    for index in 0 .. projects.Length - 1 do
                        let project = projects[index]

                        if failure.IsNone && not project.IsFilteredOut then
                            if not (File.Exists project.Path.AbsolutePath.Value) then
                                let message =
                                    $"Project '{project.Path.AbsolutePath.Value}' was not found."

                                failure <- Some(RpcErrors.create "not_found" message None)
                            else
                                let! evaluated =
                                    evaluate current.Workspace project cancellationToken

                                match evaluated with
                                | Ok snapshot ->
                                    do!
                                        writeBatch
                                            { Nodes =
                                                WorkspaceStatePure.exportProjectNodes
                                                    current.Workspace.WorkspaceDescriptor
                                                    project
                                                    (Some snapshot)
                                              IsFinal = index = projects.Length - 1 }
                                | Error error -> failure <- Some error
                        elif failure.IsNone then
                            do!
                                writeBatch
                                    { Nodes =
                                        WorkspaceStatePure.exportProjectNodes
                                            current.Workspace.WorkspaceDescriptor
                                            project
                                            None
                                      IsFinal = index = projects.Length - 1 }

                    match failure with
                    | Some error -> return Error error
                    | None -> return Ok()
            finally
                gate.Release() |> ignore
        }

    member _.WatchPlanAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                return WorkspaceStatePure.watchPlan insensitive current
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
            || isNull (box options.TokenSecret)
            || options.TokenSecret.Length < 16
        then
            invalidArg
                (nameof options)
                "Workspace options require a positive hydration limit and a token secret."

        WorkspaceState(
            target,
            services,
            options,
            { Workspace = workspace
              Hydrated = Map.empty
              Recency = []
              Revision = workspace.WorkspaceDescriptor.WorkspaceRevision.Value
              NeedsRebase = false }
        )

    static member CreateProduction(target, workspace) =
        let evaluator = new MsBuildEvaluationClient()

        let services =
            { OpenAsync =
                fun path cancellationToken -> SolutionStore.OpenAsync(path, cancellationToken)
              EvaluateAsync =
                fun project workspace cancellationToken ->
                    evaluator.EvaluateAsync(project, workspace, cancellationToken)
              InvalidateAsync =
                fun paths cancellationToken -> evaluator.InvalidateAsync(paths, cancellationToken)
              RefreshAsync = evaluator.RefreshAsync
              DisposeAsync = fun () -> evaluator.DisposeAsync().AsTask() }

        WorkspaceState.Create(
            target,
            workspace,
            services,
            { HydrationLimit = 32
              TokenSecret = RandomNumberGenerator.GetBytes 32 }
        )
