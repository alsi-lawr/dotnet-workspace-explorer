namespace Dotnet.CLI.Plus

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Transport

[<RequireQualifiedAccess>]
type internal HintDrain =
    | Empty
    | Hints of ImmutableArray<WorkspaceArtifactPath>
    | Lost

[<RequireQualifiedAccess>]
type internal WatcherHandoff =
    | Complete
    | Revalidate of ImmutableArray<WorkspaceArtifactPath>
    | RevalidateWorkspace
    | Uncertain

type internal BoundedHintBuffer(capacity: int, comparer: StringComparer) =
    let sync = obj ()
    let paths = HashSet<string> comparer
    let mutable accepting = false
    let mutable lost = false

    do
        if capacity <= 0 then
            invalidArg (nameof capacity) "Hint capacity must be positive."

    member _.Add(path: string) =
        lock sync (fun () ->
            if not accepting || String.IsNullOrWhiteSpace path then
                false
            elif paths.Contains path then
                true
            elif paths.Count < capacity then
                paths.Add path
            else
                paths.Clear()
                lost <- true
                accepting <- false
                false)

    member _.Lose() =
        lock sync (fun () ->
            if accepting then
                paths.Clear()
                lost <- true
                accepting <- false)

    member _.Drain() =
        lock sync (fun () ->
            if lost then
                lost <- false
                HintDrain.Lost
            elif paths.Count = 0 then
                HintDrain.Empty
            else
                let result =
                    paths |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange

                paths.Clear()
                HintDrain.Hints result)

    member _.Pause() =
        lock sync (fun () -> accepting <- false)

    member _.Resume() = lock sync (fun () -> accepting <- true)

    member _.Stop() =
        lock sync (fun () ->
            paths.Clear()
            lost <- false
            accepting <- false)

[<RequireQualifiedAccess>]
module internal WatcherHints =
    let renamePaths oldPath newPath = ImmutableArray.Create(oldPath, newPath)

type internal WorkspaceWatcher
    (
        state: WorkspaceState,
        hintCapacity: int,
        getFrameLimit: unit -> int,
        publicationGate: SemaphoreSlim
    ) =
    let hints = BoundedHintBuffer(hintCapacity, state.PathComparer)
    let rebuildGate = new SemaphoreSlim(1, 1)
    let lifecycleGate = obj ()
    let handoffGate = obj ()
    let wake = new SemaphoreSlim(0, 1)
    let mutable watchers: (WatchSpec * FileSystemWatcher) array = Array.empty
    let mutable queuedHandoff: WatcherHandoff option = None
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

    let dispose (value: (WatchSpec * FileSystemWatcher) array) =
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

    let covers (oldSpec: WatchSpec) (newSpec: WatchSpec) =
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

    let uncoveredPaths (oldWatchers: (WatchSpec * FileSystemWatcher) array) (plan: seq<WatchSpec>) =
        let uncovered =
            plan
            |> Seq.filter (fun candidate ->
                oldWatchers
                |> Array.exists (fun (existing, _) -> covers existing candidate)
                |> not)
            |> Seq.toArray

        if uncovered |> Array.exists (fun spec -> spec.IncludeSubdirectories) then
            WatcherHandoff.RevalidateWorkspace
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
                WatcherHandoff.Uncertain
            elif paths.Length = 0 then
                WatcherHandoff.Complete
            else
                paths
                |> Seq.choose id
                |> Seq.map WorkspaceArtifactPath.Create
                |> ImmutableArray.CreateRange
                |> WatcherHandoff.Revalidate

    let createWatcher (spec: WatchSpec) =
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
            for path in WatcherHints.renamePaths args.OldFullPath args.FullPath do
                enqueue path)

        watcher.Error.Add(fun _ ->
            if Volatile.Read(&callbacksEnabled) <> 0 then
                hints.Lose()
                signal ())

        watcher

    let rebuild cancellationToken =
        task {
            let replacements = ResizeArray<WatchSpec * FileSystemWatcher>()

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
                return WatcherHandoff.Uncertain
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
            do! publish (WorkspaceInvalidationResult.Reset reset)
        }

    let publishDeltaOrReset publish delta cancellationToken =
        task {
            let notification = PublicProtocol.workspaceDelta delta

            if (RpcCodec.encodeFrame notification).Length > getFrameLimit () then
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
                do! publish (WorkspaceInvalidationResult.Reset reset)
                return false
            else
                do! publish (WorkspaceInvalidationResult.Delta delta)
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
                | WatcherHandoff.Complete -> continueHandoff <- false
                | WatcherHandoff.Revalidate paths ->
                    let! outcome = state.InvalidateAsync(paths, cancellationToken)

                    match outcome with
                    | WorkspaceInvalidationResult.None -> continueHandoff <- false
                    | WorkspaceInvalidationResult.Delta delta ->
                        let! delivered = publishDeltaOrReset publish delta cancellationToken
                        continueHandoff <- delivered
                        active <- delivered
                    | WorkspaceInvalidationResult.Reset reset ->
                        stopForReset ()
                        do! publish (WorkspaceInvalidationResult.Reset reset)
                        continueHandoff <- false
                        active <- false
                | WatcherHandoff.RevalidateWorkspace ->
                    let! refreshed = state.RefreshAsync(None, cancellationToken)

                    match refreshed with
                    | Error _ ->
                        do! resetForUncertainty publish cancellationToken
                        continueHandoff <- false
                        active <- false
                    | Ok result when result.Reset ->
                        stopForReset ()

                        match result.ResetEvent with
                        | Some reset -> do! publish (WorkspaceInvalidationResult.Reset reset)
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
                | WatcherHandoff.Uncertain ->
                    retainUncertainty ()
                    do! resetForUncertainty publish cancellationToken
                    continueHandoff <- false
                    active <- false

            return active
        }

    member _.ActivateAsync(cancellationToken: CancellationToken) =
        task {
            let! handoff = rebuildSerialized cancellationToken

            if handoff = WatcherHandoff.Uncertain then
                retainUncertainty ()

            return handoff
        }

    member _.QueueActivationHandoff(handoff: WatcherHandoff) =
        match handoff with
        | WatcherHandoff.Revalidate _
        | WatcherHandoff.RevalidateWorkspace ->
            lock handoffGate (fun () -> queuedHandoff <- Some handoff)
            signal ()
        | WatcherHandoff.Complete
        | WatcherHandoff.Uncertain -> ()

    member _.RebuildAndRevalidateAsync
        (publish: WorkspaceInvalidationResult -> Task<unit>, cancellationToken: CancellationToken)
        =
        rebuildAndRevalidate None publish cancellationToken

    member _.ResolveActivationHandoffAsync
        (
            handoff: WatcherHandoff,
            publish: WorkspaceInvalidationResult -> Task<unit>,
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
                            | WorkspaceInvalidationResult.Delta delta ->
                                sink.WriteAsync(PublicProtocol.workspaceDelta delta)
                            | WorkspaceInvalidationResult.Reset reset ->
                                sink.WriteAsync(PublicProtocol.workspaceReset reset)
                            | WorkspaceInvalidationResult.None -> Task.FromResult(())

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
                            | HintDrain.Empty -> do! resolveQueued ()
                            | HintDrain.Lost ->
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
                                        do! sink.WriteAsync(PublicProtocol.workspaceReset reset)
                                finally
                                    publicationGate.Release() |> ignore
                            | HintDrain.Hints paths ->
                                do! resolveQueued ()
                                do! publicationGate.WaitAsync sessionToken

                                try
                                    if drainedEpoch = Volatile.Read(&lifecycleGeneration) then
                                        let! outcome = state.InvalidateAsync(paths, sessionToken)
                                        sessionToken.ThrowIfCancellationRequested()

                                        match outcome with
                                        | WorkspaceInvalidationResult.None -> ()
                                        | WorkspaceInvalidationResult.Delta delta ->
                                            let! delivered =
                                                publishDeltaOrReset publish delta sessionToken

                                            if delivered then
                                                let! _ =
                                                    this.RebuildAndRevalidateAsync(
                                                        publish,
                                                        sessionToken
                                                    )

                                                ()
                                        | WorkspaceInvalidationResult.Reset reset ->
                                            stopForReset ()
                                            do! sink.WriteAsync(PublicProtocol.workspaceReset reset)
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
