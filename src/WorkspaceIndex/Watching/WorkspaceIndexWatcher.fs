namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks


[<RequireQualifiedAccess>]
module internal WorkspaceWatchHints =
    let renamePaths oldPath newPath = ImmutableArray.Create(oldPath, newPath)

type internal WorkspaceIndexWatcher
    (
        state: WorkspaceIndex,
        hintCapacity: int,
        getFrameLimit: unit -> int,
        publicationGate: SemaphoreSlim
    ) =
    let hints = WorkspaceChangeBuffer(hintCapacity, state.PathComparer)
    let rebuildGate = new SemaphoreSlim(1, 1)
    let lifecycleGate = obj ()
    let handoffGate = obj ()
    let wake = new SemaphoreSlim(0, 1)
    let mutable watchers: (WorkspaceWatch * FileSystemWatcher) array = Array.empty
    let mutable queuedHandoff: WorkspaceWatchHandoff option = None
    let mutable lifecycleGeneration = 0L
    let mutable stopRequested = 0L
    let mutable callbacksEnabled = 0
    let mutable closed = 0
    let mutable started = 0

    let signal () =
        if Volatile.Read(&closed) = 0 then
            try
                wake.Release() |> ignore
            with
            | :? SemaphoreFullException
            | :? ObjectDisposedException -> ()

    let retainUncertainty () =
        hints.Lose()
        signal ()

    let enqueue path =
        if Volatile.Read(&callbacksEnabled) <> 0 && hints.Add path then
            signal ()

    let dispose (value: (WorkspaceWatch * FileSystemWatcher) array) =
        for _, watcher in value do
            try
                watcher.EnableRaisingEvents <- false
            with _ ->
                ()

            try
                watcher.Dispose()
            with _ ->
                ()

    let stopWatchersUnsafe () =
        Volatile.Write(&callbacksEnabled, 0)
        dispose watchers
        watchers <- Array.empty

    let stopWatchers () = lock lifecycleGate stopWatchersUnsafe

    let pathIsBelow directory path =
        let relative = Path.GetRelativePath(directory, path)

        relative = "."
        || not (Path.IsPathRooted relative)
           && relative <> ".."
           && not (
               relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           )
           && not (
               relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
           )

    let samePath left right = state.PathComparer.Equals(left, right)

    let covers (oldSpec: WorkspaceWatch) (newSpec: WorkspaceWatch) =
        if oldSpec.IncludeSubdirectories && oldSpec.Filters |> Seq.exists ((=) "*") then
            pathIsBelow oldSpec.Directory newSpec.Directory
        elif
            oldSpec.IncludeSubdirectories <> newSpec.IncludeSubdirectories
            || not (samePath oldSpec.Directory newSpec.Directory)
        then
            false
        else
            newSpec.Filters
            |> Seq.forall (fun filter -> oldSpec.Filters |> Seq.exists ((=) filter))

    let uncoveredPaths
        (oldWatchers: (WorkspaceWatch * FileSystemWatcher) array)
        (plan: seq<WorkspaceWatch>)
        =
        let uncovered =
            plan
            |> Seq.filter (fun candidate ->
                oldWatchers
                |> Array.exists (fun (existing, _) -> covers existing candidate)
                |> not)
            |> Seq.toArray

        if uncovered |> Array.exists (fun spec -> spec.IncludeSubdirectories) then
            WorkspaceWatchHandoff.RevalidateWorkspace
        else
            let paths =
                uncovered
                |> Seq.collect (fun spec ->
                    spec.Filters
                    |> Seq.map (fun filter ->
                        if filter.IndexOfAny [| '*'; '?' |] >= 0 then
                            None
                        else
                            Some(Path.Combine(spec.Directory, filter))))
                |> Seq.toArray

            if paths |> Array.exists Option.isNone then
                WorkspaceWatchHandoff.Uncertain
            elif paths.Length = 0 then
                WorkspaceWatchHandoff.Complete
            else
                paths
                |> Seq.choose id
                |> Seq.map WorkspaceArtifactPath.Create
                |> ImmutableArray.CreateRange
                |> WorkspaceWatchHandoff.Revalidate

    let createWatcher (spec: WorkspaceWatch) =
        let watcher = new FileSystemWatcher(spec.Directory)

        for filter in spec.Filters do
            watcher.Filters.Add filter

        watcher.IncludeSubdirectories <- spec.IncludeSubdirectories

        watcher.NotifyFilter <-
            NotifyFilters.FileName
            ||| NotifyFilters.DirectoryName
            ||| NotifyFilters.LastWrite
            ||| NotifyFilters.CreationTime

        watcher.Changed.Add(fun args -> enqueue args.FullPath)
        watcher.Created.Add(fun args -> enqueue args.FullPath)
        watcher.Deleted.Add(fun args -> enqueue args.FullPath)

        watcher.Renamed.Add(fun args ->
            for path in WorkspaceWatchHints.renamePaths args.OldFullPath args.FullPath do
                enqueue path)

        watcher.Error.Add(fun _ ->
            if Volatile.Read(&callbacksEnabled) <> 0 then
                hints.Lose()
                signal ())

        watcher

    let rebuild cancellationToken =
        task {
            let replacements = ResizeArray<WorkspaceWatch * FileSystemWatcher>()

            try
                let! plan = state.WatchPlanAsync cancellationToken
                let oldWatchers = watchers

                for spec in plan do
                    replacements.Add(spec, createWatcher spec)

                lock lifecycleGate (fun () ->
                    Volatile.Write(&callbacksEnabled, 1)

                    for _, watcher in replacements do
                        watcher.EnableRaisingEvents <- true

                    watchers <- replacements.ToArray()
                    dispose oldWatchers)

                return uncoveredPaths oldWatchers plan
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException())
            | _ ->
                dispose (replacements.ToArray())
                retainUncertainty ()
                return WorkspaceWatchHandoff.Uncertain
        }

    let rebuildSerialized (cancellationToken: CancellationToken) =
        task {
            do! rebuildGate.WaitAsync cancellationToken

            try
                return! rebuild cancellationToken
            finally
                rebuildGate.Release() |> ignore
        }

    let clearQueuedHandoff () =
        lock handoffGate (fun () -> queuedHandoff <- None)

    let dequeueHandoff () =
        lock handoffGate (fun () ->
            let handoff = queuedHandoff
            queuedHandoff <- None
            handoff)

    let stopForReset () =
        clearQueuedHandoff ()

        lock lifecycleGate (fun () ->
            Interlocked.Increment(&lifecycleGeneration) |> ignore
            stopWatchersUnsafe ())

        hints.Stop()

    let resetForUncertainty publish cancellationToken =
        task {
            stopForReset ()

            let diagnostic =
                WorkspaceDiagnostic.CreateSimple(
                    WorkspaceDiagnosticSeverity.Warning,
                    WorkspaceDiagnosticCode.Create "workspace.watch_uncertain",
                    "File watcher activation could not be verified; "
                    + "request a fresh workspace graph.",
                    true,
                    CorrelationId.New()
                )

            let! reset = state.ResetAsync(diagnostic, cancellationToken)
            do! publish (WorkspaceProjectInvalidationResult.Reset reset)
        }

    let publishDeltaOrReset publish delta cancellationToken =
        task {
            let notification = WorkspaceRpcNotifications.workspaceDelta delta

            if (MessagePackRpcCodec.encodeFrame notification).Length > getFrameLimit () then
                stopForReset ()

                let diagnostic =
                    WorkspaceDiagnostic.CreateSimple(
                        WorkspaceDiagnosticSeverity.Warning,
                        WorkspaceDiagnosticCode.Create "workspace.delta_pressure",
                        "The verified delta exceeded delivery capacity; "
                        + "request a fresh workspace graph.",
                        true,
                        CorrelationId.New()
                    )

                let! reset = state.ResetAsync(diagnostic, cancellationToken)
                do! publish (WorkspaceProjectInvalidationResult.Reset reset)
                return false
            else
                do! publish (WorkspaceProjectInvalidationResult.Delta delta)
                return true
        }

    let rebuildAndRevalidate initialHandoff publish cancellationToken =
        task {
            let mutable continueHandoff = true
            let mutable active = true
            let mutable nextHandoff = initialHandoff

            while continueHandoff do
                let! handoff =
                    match nextHandoff with
                    | Some value ->
                        nextHandoff <- None
                        Task.FromResult value
                    | None -> rebuildSerialized cancellationToken

                match handoff with
                | WorkspaceWatchHandoff.Complete -> continueHandoff <- false
                | WorkspaceWatchHandoff.Revalidate paths ->
                    let! outcome = state.InvalidateAsync(paths, cancellationToken)

                    match outcome with
                    | WorkspaceProjectInvalidationResult.None -> continueHandoff <- false
                    | WorkspaceProjectInvalidationResult.Delta delta ->
                        let! delivered = publishDeltaOrReset publish delta cancellationToken
                        continueHandoff <- delivered
                        active <- delivered
                    | WorkspaceProjectInvalidationResult.Reset reset ->
                        stopForReset ()
                        do! publish (WorkspaceProjectInvalidationResult.Reset reset)
                        continueHandoff <- false
                        active <- false
                | WorkspaceWatchHandoff.RevalidateWorkspace ->
                    let! refreshed = state.RefreshAsync(None, cancellationToken)

                    match refreshed with
                    | Error _ ->
                        do! resetForUncertainty publish cancellationToken
                        continueHandoff <- false
                        active <- false
                    | Ok result when result.Reset ->
                        stopForReset ()

                        match result.ResetEvent with
                        | Some reset -> do! publish (WorkspaceProjectInvalidationResult.Reset reset)
                        | None -> do! resetForUncertainty publish cancellationToken

                        continueHandoff <- false
                        active <- false
                    | Ok result ->
                        match result.Delta with
                        | Some delta ->
                            let! delivered = publishDeltaOrReset publish delta cancellationToken
                            continueHandoff <- delivered
                            active <- delivered
                        | None -> continueHandoff <- false
                | WorkspaceWatchHandoff.Uncertain ->
                    retainUncertainty ()
                    do! resetForUncertainty publish cancellationToken
                    continueHandoff <- false
                    active <- false

            return active
        }

    member _.ActivateAsync(cancellationToken: CancellationToken) =
        task {
            let! handoff = rebuildSerialized cancellationToken

            if handoff = WorkspaceWatchHandoff.Uncertain then
                retainUncertainty ()

            return handoff
        }

    member _.QueueActivationHandoff(handoff: WorkspaceWatchHandoff) =
        match handoff with
        | WorkspaceWatchHandoff.Revalidate _
        | WorkspaceWatchHandoff.RevalidateWorkspace ->
            lock handoffGate (fun () -> queuedHandoff <- Some handoff)
            signal ()
        | WorkspaceWatchHandoff.Complete
        | WorkspaceWatchHandoff.Uncertain -> ()

    member _.RebuildAndRevalidateAsync
        (
            publish: WorkspaceProjectInvalidationResult -> Task<unit>,
            cancellationToken: CancellationToken
        ) =
        rebuildAndRevalidate None publish cancellationToken

    member _.ResolveActivationHandoffAsync
        (
            handoff: WorkspaceWatchHandoff,
            publish: WorkspaceProjectInvalidationResult -> Task<unit>,
            cancellationToken: CancellationToken
        ) =
        rebuildAndRevalidate (Some handoff) publish cancellationToken

    member _.Pause() =
        clearQueuedHandoff ()

        lock lifecycleGate (fun () ->
            Volatile.Write(&callbacksEnabled, 0)
            hints.Stop()
            let generation = Interlocked.Increment(&lifecycleGeneration)
            Interlocked.Exchange(&stopRequested, generation) |> ignore)

        signal ()

    member _.Resume() =
        lock lifecycleGate (fun () ->
            Interlocked.Increment(&lifecycleGeneration) |> ignore
            hints.Resume())

    member this.StartAsync(sink: RpcNotificationSink, sessionToken: CancellationToken) =
        task {
            if Interlocked.CompareExchange(&started, 1, 0) = 0 then
                try
                    try
                        let publish handoff =
                            match handoff with
                            | WorkspaceProjectInvalidationResult.Delta delta ->
                                sink.WriteAsync(WorkspaceRpcNotifications.workspaceDelta delta)
                            | WorkspaceProjectInvalidationResult.Reset reset ->
                                sink.WriteAsync(WorkspaceRpcNotifications.workspaceReset reset)
                            | WorkspaceProjectInvalidationResult.None -> Task.FromResult(())

                        let resolveQueued () =
                            task {
                                match dequeueHandoff () with
                                | Some handoff ->
                                    do! publicationGate.WaitAsync sessionToken

                                    try
                                        let! _ =
                                            this.ResolveActivationHandoffAsync(
                                                handoff,
                                                publish,
                                                sessionToken
                                            )

                                        ()
                                    finally
                                        publicationGate.Release() |> ignore
                                | None -> ()
                            }

                        while not sessionToken.IsCancellationRequested do
                            do! wake.WaitAsync sessionToken

                            let pendingStop = Interlocked.Exchange(&stopRequested, 0L)

                            if pendingStop <> 0L then
                                lock lifecycleGate (fun () ->
                                    if pendingStop = Volatile.Read(&lifecycleGeneration) then
                                        stopWatchersUnsafe ())

                            let drainedEpoch, drained =
                                lock lifecycleGate (fun () ->
                                    Volatile.Read(&lifecycleGeneration), hints.Drain())

                            match drained with
                            | WorkspaceChangeDrain.Empty -> do! resolveQueued ()
                            | WorkspaceChangeDrain.Lost ->
                                do! publicationGate.WaitAsync sessionToken

                                try
                                    if drainedEpoch = Volatile.Read(&lifecycleGeneration) then
                                        stopForReset ()

                                        let diagnostic =
                                            WorkspaceDiagnostic.CreateSimple(
                                                WorkspaceDiagnosticSeverity.Warning,
                                                WorkspaceDiagnosticCode.Create
                                                    "workspace.watch_overflow",
                                                "File watching lost changes; "
                                                + "request a fresh workspace graph.",
                                                true,
                                                CorrelationId.New()
                                            )

                                        let! reset = state.ResetAsync(diagnostic, sessionToken)

                                        do!
                                            sink.WriteAsync(
                                                WorkspaceRpcNotifications.workspaceReset reset
                                            )
                                finally
                                    publicationGate.Release() |> ignore
                            | WorkspaceChangeDrain.Hints paths ->
                                do! resolveQueued ()
                                do! publicationGate.WaitAsync sessionToken

                                try
                                    if drainedEpoch = Volatile.Read(&lifecycleGeneration) then
                                        let! outcome = state.InvalidateAsync(paths, sessionToken)
                                        sessionToken.ThrowIfCancellationRequested()

                                        match outcome with
                                        | WorkspaceProjectInvalidationResult.None -> ()
                                        | WorkspaceProjectInvalidationResult.Delta delta ->
                                            let! delivered =
                                                publishDeltaOrReset publish delta sessionToken

                                            if delivered then
                                                let! _ =
                                                    this.RebuildAndRevalidateAsync(
                                                        publish,
                                                        sessionToken
                                                    )

                                                ()
                                        | WorkspaceProjectInvalidationResult.Reset reset ->
                                            stopForReset ()

                                            do!
                                                sink.WriteAsync(
                                                    WorkspaceRpcNotifications.workspaceReset reset
                                                )
                                finally
                                    publicationGate.Release() |> ignore
                    with :? OperationCanceledException ->
                        ()
                finally
                    Interlocked.Exchange(&closed, 1) |> ignore
                    stopWatchers ()
                    hints.Stop()
                    rebuildGate.Dispose()
                    wake.Dispose()
        }
