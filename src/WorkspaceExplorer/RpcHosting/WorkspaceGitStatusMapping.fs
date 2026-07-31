namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Generic
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex

[<RequireQualifiedAccess>]
module internal WorkspaceGitStatusMapping =
    let private stronger left right =
        match left, right with
        | Added, _
        | _, Added -> Added
        | _ -> Changed

    let mapDecorations workspacePath (nodes: WorkspaceGitNode array) changes =
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
                String.Equals(Path.GetFullPath left, Path.GetFullPath right, comparison)

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

            let decorations = Dictionary<string, GitDecorationState>(StringComparer.Ordinal)

            let add nodeId state =
                match decorations.TryGetValue nodeId with
                | true, existing -> decorations[nodeId] <- stronger existing state
                | _ -> decorations[nodeId] <- state

            for state, path in changes do
                for node in nodes do
                    let direct =
                        node.PhysicalPath
                        |> Option.exists (fun candidate -> same candidate.Value path)

                    let contained =
                        node.ContainerPath
                        |> Option.exists (fun candidate -> under candidate.Value path)

                    if direct || contained then
                        add node.NodeId.Value state

            for KeyValue(nodeId, state) in decorations |> Seq.toArray do
                let mutable parent =
                    match parents.TryGetValue nodeId with
                    | true, value -> value
                    | _ -> None

                while parent.IsSome do
                    add parent.Value state

                    parent <-
                        match parents.TryGetValue parent.Value with
                        | true, value -> value
                        | _ -> None

            decorations
            |> Seq.map (fun (KeyValue(nodeId, state)) -> nodeId, state)
            |> Seq.sortBy fst
            |> Seq.toArray
            |> Ok
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
