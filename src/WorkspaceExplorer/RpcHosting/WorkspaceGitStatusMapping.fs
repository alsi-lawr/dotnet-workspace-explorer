namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Generic
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex

[<RequireQualifiedAccess>]
module internal WorkspaceGitStatusMapping =
    let mapDecorations
        workspacePath
        (nodes: WorkspaceGitNode array)
        (snapshot: WorkspaceGitPathSnapshot)
        =
        try
            let comparison =
                match
                    Workspaces.FileSystemCaseSensitivityDetector.DetectFromExistingPath(
                        workspacePath
                    )
                with
                | Workspaces.FileSystemCaseSensitivity.Insensitive ->
                    StringComparison.OrdinalIgnoreCase
                | _ -> StringComparison.Ordinal

            let same left right =
                let comparable path =
                    Path.GetFullPath path |> WorkspaceGitPaths.withoutTrailingDirectorySeparators

                String.Equals(comparable left, comparable right, comparison)

            let under directory path =
                let relative = Path.GetRelativePath(directory, path)

                relative = "."
                || (not (Path.IsPathRooted relative)
                    && relative <> ".."
                    && not (
                        relative.StartsWith(
                            $"..{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal
                        )
                    )
                    && not (
                        relative.StartsWith(
                            $"..{Path.AltDirectorySeparatorChar}",
                            StringComparison.Ordinal
                        )
                    ))

            let parents =
                nodes
                |> Seq.map (fun node -> node.NodeId.Value, node.ParentNodeId |> Option.map _.Value)
                |> dict

            let decorations =
                Dictionary<string, HashSet<GitStatusState>>(StringComparer.Ordinal)

            let addStates nodeId states =
                for state in states do
                    let target =
                        match decorations.TryGetValue nodeId with
                        | true, existing -> existing
                        | _ ->
                            let created = HashSet<GitStatusState>()
                            decorations.Add(nodeId, created)
                            created

                    target.Add state |> ignore

            for entry in snapshot.Entries do
                for node in nodes do
                    let direct =
                        node.PhysicalPath
                        |> Option.exists (fun candidate -> same candidate.Value entry.Path)

                    let exactContainer =
                        node.ContainerPath
                        |> Option.exists (fun candidate -> same candidate.Value entry.Path)

                    let contained =
                        node.ContainerPath
                        |> Option.exists (fun candidate -> under candidate.Value entry.Path)

                    if direct || contained then
                        entry.States
                        |> Seq.filter ((<>) GitStatusState.Ignored)
                        |> addStates node.NodeId.Value

                    if
                        (direct || exactContainer)
                        && (entry.States |> Array.contains GitStatusState.Ignored)
                    then
                        addStates node.NodeId.Value [ GitStatusState.Ignored ]

            for KeyValue(nodeId, states) in decorations |> Seq.toArray do
                let mutable parent =
                    match parents.TryGetValue nodeId with
                    | true, value -> value
                    | _ -> None

                while parent.IsSome do
                    states |> Seq.filter ((<>) GitStatusState.Ignored) |> addStates parent.Value

                    parent <-
                        match parents.TryGetValue parent.Value with
                        | true, value -> value
                        | _ -> None

            Ok
                { Available = true
                  Decorations =
                    decorations
                    |> Seq.map (fun (KeyValue(nodeId, states)) ->
                        nodeId, GitStatusStates.normalize states)
                    |> Seq.sortBy fst
                    |> Seq.toArray }
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException
        | :? IOException
        | :? UnauthorizedAccessException ->
            Error(
                RpcErrors.create
                    "git_mapping_failed"
                    "Git paths could not be mapped to workspace nodes."
                    None
            )
