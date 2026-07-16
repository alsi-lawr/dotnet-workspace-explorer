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
        lock sync (fun () ->
            accepting <- false
            lost <- false
            paths.Clear())

    member _.Resume() =
        lock sync (fun () ->
            paths.Clear()
            lost <- false
            accepting <- true)

[<RequireQualifiedAccess>]
module internal WatcherHints =
    let renamePaths oldPath newPath = ImmutableArray.Create(oldPath, newPath)

type internal WorkspaceWatcher(state: WorkspaceState, hintCapacity: int, getFrameLimit: unit -> int) =
    let hints = BoundedHintBuffer(hintCapacity, state.PathComparer)
    let rebuildSync = obj ()
    let mutable watchers: FileSystemWatcher array = Array.empty
    let mutable rebuildRequested = 0
    let mutable stopRequested = 0
    let mutable callbacksEnabled = 0
    let mutable started = 0
    let mutable rebuildCompletion: TaskCompletionSource<unit> option = None

    let enqueue path =
        if Volatile.Read(&callbacksEnabled) <> 0 then
            hints.Add path |> ignore

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
                hints.Lose())

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

    let stopForReset () =
        disposeWatchers ()
        hints.Pause()

    member _.RequestRebuild() =
        Interlocked.Exchange(&rebuildRequested, 1) |> ignore

    member _.RequestRebuildAsync() =
        lock rebuildSync (fun () ->
            match rebuildCompletion with
            | Some completion -> completion.Task
            | None ->
                let completion =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                rebuildCompletion <- Some completion
                Interlocked.Exchange(&rebuildRequested, 1) |> ignore
                completion.Task)

    member _.Pause() =
        Volatile.Write(&callbacksEnabled, 0)
        hints.Pause()
        Interlocked.Exchange(&stopRequested, 1) |> ignore

    member this.Resume() =
        hints.Resume()
        this.RequestRebuild()

    member _.StartAsync(sink: RpcNotificationSink, sessionToken: CancellationToken) =
        task {
            if Interlocked.CompareExchange(&started, 1, 0) = 0 then
                try
                    try
                        do! rebuild sessionToken

                        while not sessionToken.IsCancellationRequested do
                            do! Task.Delay(50, sessionToken)

                            if Interlocked.Exchange(&stopRequested, 0) <> 0 then
                                disposeWatchers ()
                            elif Interlocked.Exchange(&rebuildRequested, 0) <> 0 then
                                do! rebuild sessionToken

                                lock rebuildSync (fun () ->
                                    rebuildCompletion
                                    |> Option.iter (fun completion -> completion.TrySetResult() |> ignore)

                                    rebuildCompletion <- None)

                            match hints.Drain() with
                            | HintDrain.Empty -> ()
                            | HintDrain.Lost ->
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
                            | HintDrain.Hints paths ->
                                let! outcome = state.InvalidateAsync(paths, sessionToken)

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
                                        do! sink.WriteAsync notification
                                        do! rebuild sessionToken
                                | WorkspaceInvalidationResult.Reset reset ->
                                    stopForReset ()
                                    do! sink.WriteAsync(PublicProtocol.workspaceReset reset)
                    with :? OperationCanceledException ->
                        ()
                finally
                    disposeWatchers ()
                    hints.Pause()

                    lock rebuildSync (fun () ->
                        rebuildCompletion
                        |> Option.iter (fun completion -> completion.TrySetCanceled() |> ignore)

                        rebuildCompletion <- None)
        }
