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

type internal BoundedHintBuffer(capacity: int, comparer: StringComparer) =
    let sync = obj ()
    let paths = HashSet<string>(comparer)
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
    (state: WorkspaceState, hintCapacity: int, getFrameLimit: unit -> int, publicationGate: SemaphoreSlim) =
    let hints = BoundedHintBuffer(hintCapacity, state.PathComparer)
    let rebuildGate = new SemaphoreSlim(1, 1)
    let wake = new SemaphoreSlim(0, 1)
    let mutable watchers: FileSystemWatcher array = Array.empty
    let mutable stopRequested = 0
    let mutable callbacksEnabled = 0
    let mutable started = 0

    let signal () =
        try
            wake.Release() |> ignore
        with :? SemaphoreFullException ->
            ()

    let enqueue path =
        if Volatile.Read(&callbacksEnabled) <> 0 && hints.Add path then
            signal ()

    let disposeWatchers () =
        Volatile.Write(&callbacksEnabled, 0)

        for watcher in watchers do
            watcher.EnableRaisingEvents <- false
            watcher.Dispose()

        watchers <- Array.empty

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
            disposeWatchers ()
            let! plan = state.WatchPlanAsync cancellationToken
            watchers <- plan |> Seq.map createWatcher |> Seq.toArray
            Volatile.Write(&callbacksEnabled, 1)

            for watcher in watchers do
                watcher.EnableRaisingEvents <- true
        }

    let rebuildSerialized (cancellationToken: CancellationToken) =
        task {
            do! rebuildGate.WaitAsync cancellationToken

            try
                do! rebuild cancellationToken
            finally
                rebuildGate.Release() |> ignore
        }

    let stopForReset () =
        disposeWatchers ()
        hints.Stop()

    member _.RebuildAsync(cancellationToken: CancellationToken) = rebuildSerialized cancellationToken

    member _.Pause() =
        Volatile.Write(&callbacksEnabled, 0)
        hints.Pause()
        Interlocked.Exchange(&stopRequested, 1) |> ignore
        signal ()

    member _.Resume() = hints.Resume()

    member _.StartAsync(sink: RpcNotificationSink, sessionToken: CancellationToken) =
        task {
            if Interlocked.CompareExchange(&started, 1, 0) = 0 then
                try
                    try
                        do! rebuildSerialized sessionToken

                        while not sessionToken.IsCancellationRequested do
                            do! wake.WaitAsync sessionToken

                            if Interlocked.Exchange(&stopRequested, 0) <> 0 then
                                disposeWatchers ()

                            match hints.Drain() with
                            | HintDrain.Empty -> ()
                            | HintDrain.Lost ->
                                do! publicationGate.WaitAsync sessionToken

                                try
                                    stopForReset ()

                                    let diagnostic =
                                        WorkspaceDiagnostic.CreateSimple(
                                            WorkspaceDiagnosticSeverity.Warning,
                                            WorkspaceDiagnosticCode.Create "workspace.watch_overflow",
                                            "File watching lost changes; request a fresh workspace graph.",
                                            true,
                                            CorrelationId.New()
                                        )

                                    let! reset = state.ResetAsync(diagnostic, sessionToken)
                                    do! sink.WriteAsync(PublicProtocol.workspaceReset reset)
                                finally
                                    publicationGate.Release() |> ignore
                            | HintDrain.Hints paths ->
                                do! publicationGate.WaitAsync sessionToken

                                try
                                    let! outcome = state.InvalidateAsync(paths, sessionToken)
                                    sessionToken.ThrowIfCancellationRequested()

                                    match outcome with
                                    | WorkspaceInvalidationResult.None -> ()
                                    | WorkspaceInvalidationResult.Delta delta ->
                                        let notification = PublicProtocol.workspaceDelta delta

                                        if (RpcCodec.encodeFrame notification).Length > getFrameLimit () then
                                            stopForReset ()

                                            let diagnostic =
                                                WorkspaceDiagnostic.CreateSimple(
                                                    WorkspaceDiagnosticSeverity.Warning,
                                                    WorkspaceDiagnosticCode.Create "workspace.delta_pressure",
                                                    "The verified delta exceeded delivery capacity; request a fresh workspace graph.",
                                                    true,
                                                    CorrelationId.New()
                                                )

                                            let! reset = state.ResetAsync(diagnostic, sessionToken)
                                            do! sink.WriteAsync(PublicProtocol.workspaceReset reset)
                                        else
                                            do! rebuildSerialized sessionToken
                                            do! sink.WriteAsync notification
                                    | WorkspaceInvalidationResult.Reset reset ->
                                        stopForReset ()
                                        do! sink.WriteAsync(PublicProtocol.workspaceReset reset)
                                finally
                                    publicationGate.Release() |> ignore
                    with :? OperationCanceledException ->
                        ()
                finally
                    disposeWatchers ()
                    hints.Stop()
                    rebuildGate.Dispose()
                    wake.Dispose()
        }
