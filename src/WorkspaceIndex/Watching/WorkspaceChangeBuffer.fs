namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.Collections.Generic
open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
type internal WorkspaceChangeDrain =
    | Empty
    | Hints of ImmutableArray<WorkspaceArtifactPath>
    | Lost

[<RequireQualifiedAccess>]
type internal WorkspaceWatchHandoff =
    | Complete
    | Revalidate of ImmutableArray<WorkspaceArtifactPath>
    | RevalidateWorkspace
    | Uncertain

type internal WorkspaceChangeBuffer(capacity: int, comparer: StringComparer) =
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
                WorkspaceChangeDrain.Lost
            elif paths.Count = 0 then
                WorkspaceChangeDrain.Empty
            else
                let result =
                    paths |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange

                paths.Clear()
                WorkspaceChangeDrain.Hints result)

    member _.Pause() =
        lock sync (fun () -> accepting <- false)

    member _.Resume() = lock sync (fun () -> accepting <- true)

    member _.Stop() =
        lock sync (fun () ->
            paths.Clear()
            lost <- false
            accepting <- false)
