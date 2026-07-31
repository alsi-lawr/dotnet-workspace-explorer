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

        let raw =
            ResizeArray<
                IndexedNodeKey *
                WorkspaceNode *
                WorkspaceNodeId option *
                string option *
                string list
             >()

        let workspaceRoot = WorkspaceIndexPure.workspaceRoot data.Workspace.Descriptor

        raw.Add(IndexedNodeKey [ "workspace-root" ], workspaceRoot, None, None, [ "0" ])

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
                folderParent folder.ParentPath |> Option.orElse (Some workspaceRoot.Id),
                None,
                [ "1"; folder.Path ]
            )

        for item in root.Items do
            raw.Add(
                IndexedNodeKey
                    [ "solution-item"
                      item.FolderPath |> Option.defaultValue String.Empty
                      item.RelativePath ],
                item.Node,
                folderParent item.FolderPath |> Option.orElse (Some workspaceRoot.Id),
                None,
                [ "2"; item.RelativePath ]
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

            raw.Add(
                IndexedNodeKey [ "project"; key ],
                node,
                folderParent project.ParentFolderPath |> Option.orElse (Some workspaceRoot.Id),
                None,
                [ "3"; key ]
            )

            match data.Hydrated.TryFind key with
            | Some hydrated ->
                for placement in
                    WorkspaceIndexPure.projectBodyEntries
                        insensitive
                        data.Workspace
                        project
                        hydrated
                        node do
                    raw.Add(
                        placement.PlacementKey,
                        placement.PlacementNode,
                        Some placement.ParentNodeId,
                        placement.PhysicalRelativePath,
                        placement.SiblingOrder
                    )
            | None -> ()

        raw
        |> Seq.groupBy (fun (_, _, parentNodeId, _, _) ->
            parentNodeId |> Option.map _.Value |> Option.defaultValue String.Empty)
        |> Seq.collect (fun (_, siblings) ->
            siblings
            |> Seq.sortBy (fun (key, _, _, _, order) -> order, key)
            |> Seq.mapi (fun index (key, node, parentNodeId, physicalRelativePath, _) ->
                { Key = key
                  Node = node
                  ParentWorkspaceNodeId = parentNodeId
                  PhysicalRelativePath = physicalRelativePath
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

        let stableSameParent =
            oldByKey
            |> Seq.choose (fun (KeyValue(key, oldValue)) ->
                newByKey.TryFind key
                |> Option.bind (fun newValue ->
                    if
                        oldValue.Node.Id = newValue.Node.Id
                        && oldValue.ParentWorkspaceNodeId = newValue.ParentWorkspaceNodeId
                    then
                        Some(key, oldValue, newValue)
                    else
                        None))
            |> Seq.toArray

        let siblingRanks placement =
            stableSameParent
            |> Seq.groupBy (fun (_, oldValue, _) -> oldValue.ParentWorkspaceNodeId)
            |> Seq.collect (fun (_, siblings) ->
                siblings
                |> Seq.sortBy (fun (key, oldValue, newValue) ->
                    (placement oldValue newValue).Index, key)
                |> Seq.mapi (fun index (key, _, _) -> key, index))
            |> Map.ofSeq

        let oldSiblingRanks = siblingRanks (fun oldValue _ -> oldValue)
        let newSiblingRanks = siblingRanks (fun _ newValue -> newValue)

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
                                || (oldValue.Index <> newValue.Index
                                    && oldSiblingRanks[key] <> newSiblingRanks[key])
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
            | Replaced(_, node, _, _) ->
                node.Kind = WorkspaceNodeKind.ProjectFolder
                || node.Kind = WorkspaceNodeKind.ProjectFile
                || node.Kind = WorkspaceNodeKind.DependencyContainer
                || node.Kind = WorkspaceNodeKind.Dependency
                || node.Kind = WorkspaceNodeKind.DependencyProperty
            | Removed(nodeId, _, _) ->
                match oldKinds.TryGetValue nodeId with
                | true, kind ->
                    kind = WorkspaceNodeKind.ProjectFolder
                    || kind = WorkspaceNodeKind.ProjectFile
                    || kind = WorkspaceNodeKind.DependencyContainer
                    || kind = WorkspaceNodeKind.Dependency
                    || kind = WorkspaceNodeKind.DependencyProperty
                | _ -> false
            | Moved(nodeId, _, _, _, _) ->
                match oldKinds.TryGetValue nodeId with
                | true, kind ->
                    kind = WorkspaceNodeKind.ProjectFolder
                    || kind = WorkspaceNodeKind.ProjectFile
                    || kind = WorkspaceNodeKind.DependencyContainer
                    || kind = WorkspaceNodeKind.Dependency
                    || kind = WorkspaceNodeKind.DependencyProperty
                | _ -> false

        { delta with
            Changes = delta.Changes |> Seq.filter (isBody >> not) |> ImmutableArray.CreateRange }
