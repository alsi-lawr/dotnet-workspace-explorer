namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.Collections.Generic
open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces

module internal WorkspaceIndexDiff =
    let private nodeEqual (left: WorkspaceNode) (right: WorkspaceNode) =
        left.Id = right.Id
        && left.Kind = right.Kind
        && left.Identity = right.Identity
        && left.Name = right.Name
        && left.CapabilityProfile = right.CapabilityProfile
        && left.LoadState = right.LoadState
        && left.Capabilities.Length = right.Capabilities.Length
        && Seq.forall2 (=) left.Capabilities right.Capabilities

    let private pathIdentity insensitive (path: string) =
        if insensitive then path.ToUpperInvariant() else path

    let placements insensitive (data: IndexedWorkspace) =
        let root = data.Workspace.Contents
        let raw = ResizeArray<IndexedNodeKey * WorkspaceNode * WorkspaceNodeId option>()

        let folderIds =
            Dictionary<string, WorkspaceNodeId>(
                if insensitive then
                    StringComparer.OrdinalIgnoreCase
                else
                    StringComparer.Ordinal
            )

        for folder in root.Folders do
            folderIds[folder.Path] <- folder.Node.Id

        let folderParent path =
            path
            |> Option.bind (fun value ->
                match folderIds.TryGetValue value with
                | true, nodeId -> Some nodeId
                | _ -> None)

        for folder in root.Folders do
            raw.Add(
                IndexedNodeKey [ "folder"; folder.Path ],
                folder.Node,
                folderParent folder.ParentPath
            )

        for item in root.Items do
            raw.Add(
                IndexedNodeKey
                    [ "solution-item"
                      item.FolderPath |> Option.defaultValue String.Empty
                      item.RelativePath ],
                item.Node,
                folderParent item.FolderPath
            )

        for project in root.Projects do
            let key = pathIdentity insensitive project.Path.AbsolutePath.Value

            let node =
                match data.Hydrated.TryFind key with
                | Some hydrated ->
                    WorkspaceNode.CreateWithLoadState(
                        data.Workspace.Descriptor,
                        WorkspaceNodeKind.Project,
                        project.Node.Identity,
                        project.Node.Name,
                        hydrated.Snapshot.CapabilityProfile,
                        WorkspaceNodeLoadState.Hydrated
                    )
                | None -> project.Node

            raw.Add(IndexedNodeKey [ "project"; key ], node, folderParent project.ParentFolderPath)

            match data.Hydrated.TryFind key with
            | Some hydrated ->
                for placement, child in
                    WorkspaceIndexPure.projectBodyEntries data.Workspace.Descriptor project hydrated do
                    raw.Add(IndexedNodeKey("project-body" :: key :: placement), child, Some node.Id)
            | None -> ()

        for node in root.BuildTypes do
            raw.Add(IndexedNodeKey [ "configuration"; node.Identity.Value ], node, None)

        for node in root.Platforms do
            raw.Add(IndexedNodeKey [ "platform"; node.Identity.Value ], node, None)

        for dependency in root.Dependencies do
            raw.Add(
                IndexedNodeKey
                    [ "dependency"
                      dependency.ProjectId.Value
                      dependency.DependsOnProjectId.Value ],
                dependency.Node,
                Some dependency.ProjectId
            )

        raw
        |> Seq.groupBy (fun (_, _, parentNodeId) ->
            parentNodeId |> Option.map _.Value |> Option.defaultValue String.Empty)
        |> Seq.collect (fun (_, siblings) ->
            siblings
            |> Seq.sortBy (fun (key, _, _) -> key)
            |> Seq.mapi (fun index (key, node, parentNodeId) ->
                { Key = key
                  Node = node
                  ParentWorkspaceNodeId = parentNodeId
                  Index = index }))
        |> Seq.sortBy _.Key
        |> Seq.toArray

    let diff workspaceId baseRevision oldIndexedNodes newIndexedNodes =
        let oldByKey =
            oldIndexedNodes |> Seq.map (fun value -> value.Key, value) |> Map.ofSeq

        let newByKey =
            newIndexedNodes |> Seq.map (fun value -> value.Key, value) |> Map.ofSeq

        let depths (placements: IndexedNode array) =
            let byId = placements |> Seq.map (fun value -> value.Node.Id, value) |> dict
            let values = Dictionary<WorkspaceNodeId, int>()

            let rec depth (visiting: Set<string>) (nodeId: WorkspaceNodeId) =
                match values.TryGetValue nodeId with
                | true, value -> value
                | _ when visiting |> Set.contains nodeId.Value -> 0
                | _ ->
                    let value =
                        match byId.TryGetValue nodeId with
                        | true, placement ->
                            placement.ParentWorkspaceNodeId
                            |> Option.map (fun parentNodeId ->
                                1 + depth (visiting |> Set.add nodeId.Value) parentNodeId)
                            |> Option.defaultValue 0
                        | _ -> 0

                    values[nodeId] <- value
                    value

            for placement in placements do
                depth Set.empty placement.Node.Id |> ignore

            fun nodeId ->
                match values.TryGetValue nodeId with
                | true, value -> value
                | _ -> 0

        let oldDepth = depths oldIndexedNodes
        let newDepth = depths newIndexedNodes

        let removals =
            oldByKey
            |> Seq.choose (fun (KeyValue(key, oldValue)) ->
                if newByKey.ContainsKey key then
                    None
                else
                    Some(key, oldValue))
            |> Seq.sortBy (fun (key, value) ->
                -oldDepth value.Node.Id,
                value.ParentWorkspaceNodeId |> Option.map _.Value,
                -value.Index,
                key)
            |> Seq.map (fun (_, value) ->
                Removed(value.Node.Id, value.ParentWorkspaceNodeId, value.Index))

        let replacements, moves, updates =
            oldByKey
            |> Seq.choose (fun (KeyValue(key, oldValue)) ->
                newByKey.TryFind key |> Option.map (fun newValue -> key, oldValue, newValue))
            |> Seq.fold
                (fun (replaceValues, moveValues, updateValues) (key, oldValue, newValue) ->
                    if oldValue.Node.Id <> newValue.Node.Id then
                        (key,
                         newValue,
                         Replaced(
                             oldValue.Node.Id,
                             newValue.Node,
                             newValue.ParentWorkspaceNodeId,
                             newValue.Index
                         ))
                        :: replaceValues,
                        moveValues,
                        updateValues
                    else
                        let nextMoves =
                            if
                                oldValue.ParentWorkspaceNodeId <> newValue.ParentWorkspaceNodeId
                            then
                                (key,
                                 newValue,
                                 Moved(
                                     newValue.Node.Id,
                                     oldValue.ParentWorkspaceNodeId,
                                     oldValue.Index,
                                     newValue.ParentWorkspaceNodeId,
                                     newValue.Index
                                 ))
                                :: moveValues
                            else
                                moveValues

                        let nextUpdates =
                            if not (nodeEqual oldValue.Node newValue.Node) then
                                (key,
                                 newValue,
                                 Updated(
                                     newValue.Node,
                                     newValue.ParentWorkspaceNodeId,
                                     newValue.Index
                                 ))
                                :: updateValues
                            else
                                updateValues

                        replaceValues, nextMoves, nextUpdates)
                ([], [], [])

        let ordered values =
            values
            |> Seq.sortBy (fun (key, placement, _) -> newDepth placement.Node.Id, key)
            |> Seq.map (fun (_, _, change) -> change)

        let additions =
            newByKey
            |> Seq.choose (fun (KeyValue(key, newValue)) ->
                if oldByKey.ContainsKey key then
                    None
                else
                    Some(key, newValue))
            |> Seq.sortBy (fun (key, value) ->
                newDepth value.Node.Id,
                value.ParentWorkspaceNodeId |> Option.map _.Value,
                value.Index,
                key)
            |> Seq.map (fun (_, value) ->
                Added(value.Node, value.ParentWorkspaceNodeId, value.Index))

        let changes =
            Seq.concat [ removals; ordered replacements; ordered moves; ordered updates; additions ]
            |> ImmutableArray.CreateRange

        if changes.IsEmpty then
            None
        else
            Some
                { WorkspaceId = workspaceId
                  BaseRevision = WorkspaceRevision.Create baseRevision
                  NewRevision = WorkspaceRevision.Create(baseRevision + 1L)
                  Changes = changes
                  Diagnostics = ImmutableArray<WorkspaceDiagnostic>.Empty }

    let omitLazyBodyChanges (oldIndexedNodes: IndexedNode array) (delta: WorkspaceDelta) =
        let oldKinds =
            oldIndexedNodes |> Seq.map (fun value -> value.Node.Id, value.Node.Kind) |> dict

        let isBody =
            function
            | Added(node, _, _)
            | Updated(node, _, _)
            | Replaced(_, node, _, _) -> node.Kind = WorkspaceNodeKind.ProjectItem
            | Removed(nodeId, _, _) ->
                match oldKinds.TryGetValue nodeId with
                | true, kind -> kind = WorkspaceNodeKind.ProjectItem
                | _ -> false
            | Moved(nodeId, _, _, _, _) ->
                match oldKinds.TryGetValue nodeId with
                | true, kind -> kind = WorkspaceNodeKind.ProjectItem
                | _ -> false

        { delta with
            Changes = delta.Changes |> Seq.filter (isBody >> not) |> ImmutableArray.CreateRange }
