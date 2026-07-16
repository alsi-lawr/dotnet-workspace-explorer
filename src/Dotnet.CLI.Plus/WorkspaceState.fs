namespace Dotnet.CLI.Plus

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport

type internal WorkspaceEntry =
    { Node: WorkspaceNode
      ParentId: NodeId option
      PlacementKey: string }

type internal MaterializedProject =
    { Root: WorkspaceNode
      Children: ImmutableArray<WorkspaceNode>
      Snapshot: EvaluationSnapshot }

[<RequireQualifiedAccess>]
type internal WorkspaceWatchOutcome =
    | None
    | Delta of WorkspaceDelta
    | Reset of WorkspaceReset

module private WorkspaceStateSupport =
    let diagnostic code message retryable =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Warning,
            WorkspaceDiagnosticCode.Create code,
            message,
            retryable,
            CorrelationId.New()
        )

    let canonicalBytes (values: seq<string>) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)

        for value in values do
            let bytes = Encoding.UTF8.GetBytes value
            writer.Write bytes.Length
            writer.Write bytes

        writer.Flush()
        stream.ToArray()

    let snapshotSignature (snapshot: EvaluationSnapshot) =
        let values = ResizeArray<string>()
        values.Add snapshot.ProjectPath.Value

        for dimension in snapshot.Dimensions do
            values.Add(
                if dimension.TargetFramework.HasValue then
                    $"framework:{dimension.TargetFramework.Value.Value}"
                else
                    "framework:outer"
            )

            for property in dimension.Properties do
                values.Add($"property:{property.Name}:{property.Value}")

            for item in dimension.Items do
                values.Add($"item:{item.ItemType}:{item.EvaluatedInclude}")

            for reference in dimension.ProjectReferences do
                values.Add($"project-reference:{reference.Include}")

            for reference in dimension.References do
                values.Add($"reference:{reference.Include}")

            for package in dimension.Packages do
                values.Add($"package:{package.Id}:{Option.ofObj package.Version |> Option.defaultValue String.Empty}")

            for analyzer in dimension.Analyzers do
                values.Add($"analyzer:{analyzer.Value}")

        for path in snapshot.Imports do
            values.Add($"import:{path.Value}")

        for path in snapshot.WatchInputs do
            values.Add($"watch:{path.Value}")

        for path in snapshot.GlobRoots do
            values.Add($"glob:{path.Value}")

        canonicalBytes values

    let sameSnapshot left right =
        snapshotSignature left
        |> fun bytes -> bytes.AsSpan().SequenceEqual(snapshotSignature right)

    let nodeEqual (left: WorkspaceNode) (right: WorkspaceNode) =
        left.NodeId = right.NodeId
        && left.Name = right.Name
        && left.NodeKind = right.NodeKind
        && left.NodeLoadState = right.NodeLoadState
        && left.Profile = right.Profile
        && Seq.forall2 (=) left.AvailableCapabilities right.AvailableCapabilities

    let isCovered root path =
        let relative = Path.GetRelativePath(root, path)

        relative = "."
        || (not (Path.IsPathRooted relative)
            && relative <> ".."
            && not (relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            && not (relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)))

type internal WorkspaceState
    private
    (target: string, initialWorkspace: SolutionWorkspace, evaluator: MsBuildEvaluationClient, hydrationLimit: int) =
    let gate = new SemaphoreSlim(1, 1)
    let secret = RandomNumberGenerator.GetBytes 32
    let hydrated = Dictionary<string, MaterializedProject>(StringComparer.Ordinal)
    let recency = LinkedList<string>()

    let recencyNodes =
        Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal)

    let mutable workspace = initialWorkspace
    let mutable revision = initialWorkspace.WorkspaceDescriptor.WorkspaceRevision.Value
    let mutable disposed = false

    let projectKey (project: SolutionProjectProjection) = project.Path.AbsolutePath.Value

    let rootForProject (project: SolutionProjectProjection) =
        match hydrated.TryGetValue(projectKey project) with
        | true, materialized -> materialized.Root
        | _ -> project.Node

    let entries () =
        let root = workspace.RootProjection

        let folders =
            root.Folders |> Seq.map (fun folder -> folder.Path, folder.Node.NodeId) |> dict

        let values = ResizeArray<WorkspaceEntry>()

        for folder in root.Folders do
            let parent =
                folder.ParentPath
                |> Option.bind (fun path ->
                    folders.TryGetValue path
                    |> function
                        | true, value -> Some value
                        | _ -> None)

            values.Add
                { Node = folder.Node
                  ParentId = parent
                  PlacementKey = $"folder:{folder.Path}" }

        for item in root.Items do
            let parent =
                item.FolderPath
                |> Option.bind (fun path ->
                    folders.TryGetValue path
                    |> function
                        | true, value -> Some value
                        | _ -> None)

            values.Add
                { Node = item.Node
                  ParentId = parent
                  PlacementKey = $"solution-item:{item.RelativePath}" }

        for project in root.Projects do
            let parent =
                project.ParentFolderPath
                |> Option.bind (fun path ->
                    folders.TryGetValue path
                    |> function
                        | true, value -> Some value
                        | _ -> None)

            values.Add
                { Node = rootForProject project
                  ParentId = parent
                  PlacementKey = $"project:{project.Path.AbsolutePath.Value}" }

        for node in root.BuildTypes do
            values.Add
                { Node = node
                  ParentId = None
                  PlacementKey = $"configuration:{node.Identity.Value}" }

        for node in root.Platforms do
            values.Add
                { Node = node
                  ParentId = None
                  PlacementKey = $"platform:{node.Identity.Value}" }

        for dependency in root.Dependencies do
            values.Add
                { Node = dependency.Node
                  ParentId = Some dependency.ProjectId
                  PlacementKey = $"dependency:{dependency.Node.Identity.Value}" }

        for project in root.Projects do
            match hydrated.TryGetValue(projectKey project) with
            | true, materialized ->
                for node in materialized.Children do
                    values.Add
                        { Node = node
                          ParentId = Some materialized.Root.NodeId
                          PlacementKey = $"body:{node.Identity.Value}" }
            | _ -> ()

        values |> Seq.sortBy (fun entry -> entry.PlacementKey) |> Seq.toArray

    let advance oldEntries diagnostics =
        let nextRevision = WorkspaceRevision.Create(revision + 1L)
        let newEntries = entries ()

        let oldByPlacement =
            oldEntries |> Seq.map (fun entry -> entry.PlacementKey, entry) |> dict

        let newByPlacement =
            newEntries |> Seq.map (fun entry -> entry.PlacementKey, entry) |> dict

        let changes = ResizeArray<WorkspaceChange>()

        for KeyValue(key, oldEntry) in oldByPlacement do
            match newByPlacement.TryGetValue key with
            | false, _ -> changes.Add(WorkspaceChange.Removed(oldEntry.Node.NodeId, oldEntry.ParentId, key))
            | true, nextEntry when oldEntry.Node.NodeId <> nextEntry.Node.NodeId ->
                changes.Add(
                    WorkspaceChange.Replaced(
                        { OldId = oldEntry.Node.NodeId
                          NewId = nextEntry.Node.NodeId },
                        nextEntry.ParentId,
                        key
                    )
                )
            | true, nextEntry when oldEntry.ParentId <> nextEntry.ParentId ->
                changes.Add(WorkspaceChange.Moved(nextEntry.Node.NodeId, oldEntry.ParentId, nextEntry.ParentId, key))
            | true, nextEntry when not (WorkspaceStateSupport.nodeEqual oldEntry.Node nextEntry.Node) ->
                changes.Add(WorkspaceChange.Updated(nextEntry.Node, nextEntry.ParentId, key))
            | _ -> ()

        for KeyValue(key, nextEntry) in newByPlacement do
            if not (oldByPlacement.ContainsKey key) then
                changes.Add(WorkspaceChange.Added(nextEntry.Node, nextEntry.ParentId, key))

        revision <- nextRevision.Value

        { WorkspaceId = workspace.WorkspaceDescriptor.WorkspaceId
          BaseRevision = WorkspaceRevision.Create(nextRevision.Value - 1L)
          NewRevision = nextRevision
          Changes =
            ImmutableArray.CreateRange(
                changes
                |> Seq.sortBy (fun change ->
                    match change with
                    | WorkspaceChange.Added(_, _, key)
                    | WorkspaceChange.Removed(_, _, key)
                    | WorkspaceChange.Updated(_, _, key)
                    | WorkspaceChange.Moved(_, _, _, key)
                    | WorkspaceChange.Replaced(_, _, key) -> key)
            )
          Diagnostics = ImmutableArray.CreateRange diagnostics }

    let touch key =
        match recencyNodes.TryGetValue key with
        | true, node -> recency.Remove node
        | _ -> ()

        recencyNodes[key] <- recency.AddFirst key

    let evict () =
        if hydrated.Count > hydrationLimit then
            let key = recency |> Seq.last
            recency.RemoveLast()
            recencyNodes.Remove key |> ignore
            hydrated.Remove key |> ignore
            true
        else
            false

    let projectBody (project: SolutionProjectProjection) (snapshot: EvaluationSnapshot) =
        let semantic = HashSet<string>(StringComparer.Ordinal)
        let nodes = ResizeArray<WorkspaceNode>()

        let add identity name =
            if semantic.Add identity then
                nodes.Add(
                    WorkspaceNode.Create(
                        workspace.WorkspaceDescriptor,
                        WorkspaceNodeKind.ProjectItem,
                        NodeSemanticIdentity.Create $"project-body:{project.Node.Identity.Value}:{identity}",
                        name,
                        snapshot.CapabilityProfile
                    )
                )

        for dimension in snapshot.Dimensions do
            let dimensionKey =
                if dimension.TargetFramework.HasValue then
                    dimension.TargetFramework.Value.Value
                else
                    "outer"

            for property in dimension.Properties do
                add $"property:{dimensionKey}:{property.Name}:{property.Value}" $"{property.Name} = {property.Value}"

            for item in dimension.Items do
                add
                    $"item:{dimensionKey}:{item.ItemType}:{item.EvaluatedInclude}"
                    $"{item.ItemType}: {item.EvaluatedInclude}"

            for reference in dimension.ProjectReferences do
                add $"project-reference:{dimensionKey}:{reference.Include}" $"Project reference: {reference.Include}"

            for reference in dimension.References do
                add $"reference:{dimensionKey}:{reference.Include}" $"Reference: {reference.Include}"

            for package in dimension.Packages do
                let version = Option.ofObj package.Version |> Option.defaultValue String.Empty
                add $"package:{dimensionKey}:{package.Id}:{version}" ($"Package: {package.Id} {version}".Trim())

            for analyzer in dimension.Analyzers do
                add $"analyzer:{dimensionKey}:{analyzer.Value}" $"Analyzer: {analyzer.Value}"

        nodes
        |> Seq.sortBy (fun node -> node.Identity.Value)
        |> ImmutableArray.CreateRange

    let materialize project fresh cancellationToken =
        task {
            let key = projectKey project

            match hydrated.TryGetValue key with
            | true, current when not fresh ->
                touch key
                return Ok current
            | _ when project.IsFilteredOut ->
                return Error(RpcErrors.invalidParams "Filtered-out projects cannot be hydrated.")
            | _ ->
                let! outcome =
                    evaluator.EvaluateAsync(project.Path.AbsolutePath, workspace.BackingPath, cancellationToken)

                match outcome with
                | WorkspaceOutcome.Success snapshot when not cancellationToken.IsCancellationRequested ->
                    let root =
                        WorkspaceNode.CreateWithLoadState(
                            workspace.WorkspaceDescriptor,
                            WorkspaceNodeKind.Project,
                            project.Node.Identity,
                            project.Node.Name,
                            snapshot.CapabilityProfile,
                            WorkspaceNodeLoadState.Hydrated
                        )

                    let value =
                        { Root = root
                          Children = projectBody project snapshot
                          Snapshot = snapshot }

                    return Ok value
                | WorkspaceOutcome.Success _ ->
                    return Error(RpcErrors.invalidParams "The workspace operation was cancelled.")
                | WorkspaceOutcome.Failure failure -> return Error(PublicProtocol.failureError failure)
        }

    let token (parentId: NodeId) (offset: int) (tokenRevision: int64) =
        let payload =
            $"{workspace.WorkspaceDescriptor.WorkspaceId.Value}|{parentId.Value}|{offset}|{tokenRevision}"

        use hmac = new HMACSHA256(secret)

        let signature =
            hmac.ComputeHash(Encoding.UTF8.GetBytes payload) |> Convert.ToHexString

        Convert.ToBase64String(Encoding.UTF8.GetBytes payload) + "." + signature

    let parseToken (parentId: NodeId) (value: string) =
        try
            let pieces = value.Split('.', StringSplitOptions.None)

            if pieces.Length <> 2 then
                None
            else
                let payload = Encoding.UTF8.GetString(Convert.FromBase64String pieces[0])
                use hmac = new HMACSHA256(secret)

                let expected =
                    hmac.ComputeHash(Encoding.UTF8.GetBytes payload) |> Convert.ToHexString

                if
                    not (
                        CryptographicOperations.FixedTimeEquals(
                            Encoding.ASCII.GetBytes expected,
                            Encoding.ASCII.GetBytes pieces[1]
                        )
                    )
                then
                    None
                else
                    let fields = payload.Split('|')

                    match fields with
                    | [| workspaceId; tokenParent; offset; tokenRevision |] when
                        workspaceId = workspace.WorkspaceDescriptor.WorkspaceId.Value
                        && tokenParent = parentId.Value
                        ->
                        match Int32.TryParse offset, Int64.TryParse tokenRevision with
                        | (true, parsedOffset), (true, parsedRevision) when parsedOffset >= 0 ->
                            Some(parsedOffset, parsedRevision)
                        | _ -> None
                    | _ -> None
        with :? FormatException ->
            None

    member _.Descriptor = workspace.WorkspaceDescriptor
    member _.Revision = revision

    member this.RootAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                cancellationToken.ThrowIfCancellationRequested()

                return
                    revision,
                    entries ()
                    |> Seq.filter (fun entry -> entry.ParentId.IsNone)
                    |> Seq.map _.Node
                    |> ImmutableArray.CreateRange
            finally
                gate.Release() |> ignore
        }

    member this.ChildrenAsync
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
                cancellationToken.ThrowIfCancellationRequested()
                let allBefore = entries ()

                let parent =
                    allBefore |> Array.tryFind (fun entry -> entry.Node.NodeId.Value = parentIdText)

                match parent with
                | None -> return Error(RpcErrors.invalidParams "The requested workspace parent does not exist.")
                | Some parentEntry ->
                    let pageSize =
                        requestedPageSize
                        |> Option.defaultValue 256
                        |> min 4096
                        |> min negotiatedPageSize

                    let offsetResult =
                        match continuation with
                        | None -> Ok 0
                        | Some value ->
                            match parseToken parentEntry.Node.NodeId value with
                            | Some(offset, tokenRevision) when tokenRevision = revision -> Ok offset
                            | Some _ -> Error(PublicProtocol.workspaceConflict revision)
                            | None -> Error(RpcErrors.invalidParams "The continuation token is invalid.")

                    match offsetResult with
                    | Error error -> return Error error
                    | Ok offset ->
                        let project =
                            workspace.RootProjection.Projects
                            |> Seq.tryFind (fun item -> item.Node.NodeId.Value = parentIdText)

                        match project with
                        | Some item when not item.IsFilteredOut && not (hydrated.ContainsKey(projectKey item)) ->
                            let! evaluated = materialize item false cancellationToken

                            match evaluated with
                            | Error error -> return Error error
                            | Ok value ->
                                cancellationToken.ThrowIfCancellationRequested()
                                let old = entries ()
                                hydrated[projectKey item] <- value
                                touch (projectKey item)
                                evict () |> ignore
                                let delta = advance old Seq.empty

                                let children =
                                    entries ()
                                    |> Seq.filter (fun entry -> entry.ParentId = Some value.Root.NodeId)
                                    |> Seq.toArray

                                let page =
                                    children |> Array.skip (min offset children.Length) |> Array.truncate pageSize

                                let next =
                                    if offset + page.Length < children.Length then
                                        Some(
                                            ContinuationToken.Create(
                                                token value.Root.NodeId (offset + page.Length) revision
                                            )
                                        )
                                    else
                                        None

                                return
                                    Ok(
                                        revision,
                                        value.Root.NodeId,
                                        page |> Seq.map _.Node |> ImmutableArray.CreateRange,
                                        next,
                                        Some delta
                                    )
                        | _ ->
                            let children =
                                entries ()
                                |> Seq.filter (fun entry -> entry.ParentId = Some parentEntry.Node.NodeId)
                                |> Seq.toArray

                            let page =
                                children |> Array.skip (min offset children.Length) |> Array.truncate pageSize

                            let next =
                                if offset + page.Length < children.Length then
                                    Some(
                                        ContinuationToken.Create(
                                            token parentEntry.Node.NodeId (offset + page.Length) revision
                                        )
                                    )
                                else
                                    None

                            return
                                Ok(
                                    revision,
                                    parentEntry.Node.NodeId,
                                    page |> Seq.map _.Node |> ImmutableArray.CreateRange,
                                    next,
                                    None
                                )
            finally
                gate.Release() |> ignore
        }

    member this.ExportAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let staged = ResizeArray<string * MaterializedProject>()
                let mutable failure = None

                for project in workspace.RootProjection.Projects do
                    cancellationToken.ThrowIfCancellationRequested()

                    if
                        failure.IsNone
                        && not project.IsFilteredOut
                        && File.Exists(project.Path.AbsolutePath.Value)
                        && not (hydrated.ContainsKey(projectKey project))
                    then
                        let! value = materialize project false cancellationToken

                        match value with
                        | Ok item -> staged.Add(projectKey project, item)
                        | Error error -> failure <- Some error

                cancellationToken.ThrowIfCancellationRequested()

                match failure with
                | Some rpcError -> return Error rpcError
                | None ->
                    if staged.Count > 0 then
                        let old = entries ()

                        for key, value in staged do
                            hydrated[key] <- value
                            touch key

                        advance old Seq.empty |> ignore

                    return Ok(revision, entries () |> Seq.map _.Node |> ImmutableArray.CreateRange)
            finally
                gate.Release() |> ignore
        }

    member this.RefreshAsync(expectedRevision: int64 option, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                match expectedRevision with
                | Some expected when expected <> revision -> return Error(PublicProtocol.workspaceConflict revision)
                | _ ->
                    let! opened = SolutionStore.OpenAsync(target, cancellationToken)

                    match opened with
                    | WorkspaceOutcome.Failure failure -> return Error(PublicProtocol.failureError failure)
                    | WorkspaceOutcome.Success next ->
                        let old = entries ()
                        let oldWorkspace = workspace
                        workspace <- next
                        hydrated.Clear()
                        recency.Clear()
                        recencyNodes.Clear()
                        let delta = advance old Seq.empty

                        if delta.Changes.IsEmpty then
                            workspace <- oldWorkspace
                            revision <- revision - 1L
                            return Ok(revision, false, None)
                        else
                            return Ok(revision, true, Some delta)
            finally
                gate.Release() |> ignore
        }

    member this.InvalidateAsync(paths: seq<WorkspaceArtifactPath>, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let changed = paths |> Seq.toArray

                let touchesSolution =
                    changed
                    |> Array.exists (fun path ->
                        String.Equals(path.Value, workspace.WorkspaceDescriptor.Path.Value, StringComparison.Ordinal)
                        || String.Equals(path.Value, workspace.BackingPath.Value, StringComparison.Ordinal))

                if touchesSolution then
                    let! opened = SolutionStore.OpenAsync(target, cancellationToken)

                    match opened with
                    | WorkspaceOutcome.Failure _ ->
                        return
                            WorkspaceWatchOutcome.Reset
                                { WorkspaceId = workspace.WorkspaceDescriptor.WorkspaceId
                                  Revision = WorkspaceRevision.Create revision
                                  Diagnostics =
                                    ImmutableArray.Create(
                                        WorkspaceStateSupport.diagnostic
                                            "workspace.watch_unverified"
                                            "The solution change could not be verified."
                                            true
                                    ) }
                    | WorkspaceOutcome.Success next ->
                        let old = entries ()
                        workspace <- next
                        hydrated.Clear()
                        recency.Clear()
                        recencyNodes.Clear()
                        let delta = advance old Seq.empty
                        return WorkspaceWatchOutcome.Delta delta
                else
                    let! outcome = evaluator.InvalidateAsync(changed, cancellationToken)

                    match outcome with
                    | WorkspaceOutcome.Failure _ ->
                        return
                            WorkspaceWatchOutcome.Reset
                                { WorkspaceId = workspace.WorkspaceDescriptor.WorkspaceId
                                  Revision = WorkspaceRevision.Create revision
                                  Diagnostics =
                                    ImmutableArray.Create(
                                        WorkspaceStateSupport.diagnostic
                                            "workspace.watch_unverified"
                                            "The workspace change could not be verified."
                                            true
                                    ) }
                    | WorkspaceOutcome.Success MsBuildInvalidationKind.None -> return WorkspaceWatchOutcome.None
                    | WorkspaceOutcome.Success MsBuildInvalidationKind.ToolsetSelection ->
                        return
                            WorkspaceWatchOutcome.Reset
                                { WorkspaceId = workspace.WorkspaceDescriptor.WorkspaceId
                                  Revision = WorkspaceRevision.Create revision
                                  Diagnostics =
                                    ImmutableArray.Create(
                                        WorkspaceStateSupport.diagnostic
                                            "workspace.toolset_changed"
                                            "The selected SDK changed; request a fresh workspace graph."
                                            true
                                    ) }
                    | WorkspaceOutcome.Success _ ->
                        let old = entries ()
                        let mutable changedAny = false

                        for project in workspace.RootProjection.Projects do
                            match hydrated.TryGetValue(projectKey project) with
                            | true, prior ->
                                let! next = materialize project true cancellationToken

                                match next with
                                | Ok value when not (WorkspaceStateSupport.sameSnapshot prior.Snapshot value.Snapshot) ->
                                    hydrated[projectKey project] <- value
                                    changedAny <- true
                                | Ok _ -> ()
                                | Error _ -> changedAny <- false
                            | _ -> ()

                        if changedAny then
                            return WorkspaceWatchOutcome.Delta(advance old Seq.empty)
                        else
                            return WorkspaceWatchOutcome.None
            finally
                gate.Release() |> ignore
        }

    member this.InvalidateFromTransactionAsync(cancellationToken: CancellationToken) =
        this.InvalidateAsync(Seq.empty, cancellationToken)

    member _.WatchDirectories() =
        let paths = ResizeArray<string>()

        let addFile path =
            if not (String.IsNullOrWhiteSpace path) then
                Path.GetDirectoryName path |> Option.ofObj |> Option.iter paths.Add

        addFile workspace.WorkspaceDescriptor.Path.Value
        addFile workspace.BackingPath.Value

        for project in hydrated.Values do
            for path in project.Snapshot.WatchInputs do
                addFile path.Value

            for path in project.Snapshot.Imports do
                addFile path.Value

            for path in project.Snapshot.GlobRoots do
                paths.Add path.Value

            for dimension in project.Snapshot.Dimensions do
                for item in dimension.Items do
                    item.ResolvedPath
                    |> Option.ofObj
                    |> Option.iter (fun path -> addFile path.Value)

        paths
        |> Seq.filter Directory.Exists
        |> Seq.map Path.GetFullPath
        |> Seq.distinct
        |> Seq.sort
        |> Seq.filter (fun path ->
            not (
                paths
                |> Seq.exists (fun root -> root <> path && WorkspaceStateSupport.isCovered root path)
            ))
        |> Seq.toArray

    member _.DisposeAsync() =
        task {
            if not disposed then
                disposed <- true
                do! evaluator.DisposeAsync().AsTask()
                gate.Dispose()
        }

    static member CreateAsync(target: string, workspace: SolutionWorkspace, cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            let capacity =
                match Int32.TryParse(Environment.GetEnvironmentVariable "DOTNET_PLUS_HYDRATION_LIMIT") with
                | true, value when value > 0 -> value
                | _ -> 32

            return new WorkspaceState(target, workspace, new MsBuildEvaluationClient(), capacity)
        }
