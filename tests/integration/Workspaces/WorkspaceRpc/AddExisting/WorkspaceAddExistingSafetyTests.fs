namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

open System
open System.IO
open Dotnet.WorkspaceExplorer
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Xunit

type private AdjustableTimeProvider(initial: DateTimeOffset) =
    inherit TimeProvider()
    let mutable current = initial
    override _.GetUtcNow() = current
    member _.Advance(value: TimeSpan) = current <- current.Add value

[<Collection("Workspace scenarios")>]
type WorkspaceAddExistingSafetyTests() =
    [<Fact>]
    member _.``selector root labels never expose a filesystem-root path``() =
        let root =
            Path.GetPathRoot(Path.GetFullPath ".")
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "The filesystem root was unavailable.")

        let label = AddExistingSelectorPaths.rootDisplayName root

        label |> should equal "Filesystem Root"
        label |> should not' (equal root)

    [<Fact>]
    member _.``selector expiry revision replacement and close enforce the ten-minute single-session lifecycle``
        ()
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory "add-existing-lifecycle"
        let solution = Path.Combine(directory, "Demo.slnx")

        try
            WorkspaceRpcScenario.save
                solution
                (Microsoft.VisualStudio.SolutionPersistence.Model.SolutionModel())

            let workspace =
                match SolutionWorkspaceReader.OpenAsync(solution).Result with
                | Success value -> value
                | Failure failure -> failwithf "The test workspace did not open: %A" failure

            let state = WorkspaceIndex.CreateProduction(solution, workspace, 1)

            try
                let rootNode =
                    WorkspaceNode.Create(
                        workspace.Descriptor,
                        WorkspaceNodeKind.Workspace,
                        WorkspaceNodeIdentity.Create "root",
                        "Demo",
                        WorkspaceCapabilityProfile.Full
                    )

                let target: WorkspaceSemanticContext =
                    { Node = rootNode
                      ProjectId = None
                      ProjectPath = None
                      PhysicalPath = None
                      PhysicalDirectory = None
                      LogicalFolderId = None
                      LogicalFolderPath = None }

                let startedAt = DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)
                let clock = AdjustableTimeProvider startedAt
                use selector = new AddExistingSelector((fun () -> 1), clock)

                let start selectionId =
                    match
                        selector
                            .StartAsync(
                                workspace,
                                state,
                                target,
                                selectionId,
                                state.Revision,
                                Some 1,
                                Threading.CancellationToken.None
                            )
                            .Result
                    with
                    | Ok value -> value
                    | Error error -> failwithf "Selector start failed: %s" error.Message

                let selectorIdentity started =
                    WorkspaceRpcScenario.field "selectorId" started
                    |> RpcValue.requireString "selectorId"

                let rootIdentity started =
                    WorkspaceRpcScenario.field "root" started
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                let unavailable outcome =
                    match outcome with
                    | Error error -> error.Code |> should equal "selector_unavailable"
                    | Ok _ -> failwith "The invalid selector remained available."

                let first = start "first"

                WorkspaceRpcScenario.field "expiresAtUtc" first
                |> RpcValue.requireString "expiresAtUtc"
                |> DateTimeOffset.Parse
                |> should equal (startedAt.AddMinutes 10.0)

                let second = start "second"

                selector.Close(selectorIdentity first) |> unavailable

                match selector.Close(selectorIdentity second) with
                | Ok _ -> ()
                | Error error -> failwithf "Selector close failed: %s" error.Message

                let expiring = start "expiring"
                clock.Advance(TimeSpan.FromMinutes 10.0 + TimeSpan.FromTicks 1L)

                selector.Children(
                    selectorIdentity expiring,
                    rootIdentity expiring,
                    Some 1,
                    None,
                    state.Revision
                )
                |> unavailable

                let revised = start "revised"

                selector.Children(
                    selectorIdentity revised,
                    rootIdentity revised,
                    Some 1,
                    None,
                    state.Revision + 1L
                )
                |> unavailable
            finally
                state.DisposeAsync().GetAwaiter().GetResult()
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``a selector explicitly refuses a read-only solution filter before enumeration``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "add-existing-slnf-refusal"
        let solution = Path.Combine(directory, "Demo.slnx")
        let filter = Path.Combine(directory, "Demo.slnf")

        try
            WorkspaceRpcScenario.save
                solution
                (Microsoft.VisualStudio.SolutionPersistence.Model.SolutionModel())

            File.WriteAllText(filter, """{ "solution": { "path": "Demo.slnx" } }""")

            let workspace =
                match SolutionWorkspaceReader.OpenAsync(filter).Result with
                | Success value -> value
                | Failure failure -> failwithf "The solution filter did not open: %A" failure

            let state = WorkspaceIndex.CreateProduction(filter, workspace, 1)

            try
                let rootNode =
                    WorkspaceNode.Create(
                        workspace.Descriptor,
                        WorkspaceNodeKind.Workspace,
                        WorkspaceNodeIdentity.Create "root",
                        "Demo",
                        WorkspaceCapabilityProfile.Full
                    )

                let target: WorkspaceSemanticContext =
                    { Node = rootNode
                      ProjectId = None
                      ProjectPath = None
                      PhysicalPath = None
                      PhysicalDirectory = None
                      LogicalFolderId = None
                      LogicalFolderPath = None }

                use selector = new AddExistingSelector((fun () -> 1), TimeProvider.System)

                match
                    selector
                        .StartAsync(
                            workspace,
                            state,
                            target,
                            "selection",
                            state.Revision,
                            Some 1,
                            Threading.CancellationToken.None
                        )
                        .Result
                with
                | Error error -> error.Code |> should equal "unsupported_capability"
                | Ok _ -> failwith "The read-only solution filter started a selector."
            finally
                state.DisposeAsync().GetAwaiter().GetResult()
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``selector pages are directory-first ordinal bounded snapshots and nested directories remain lazy``
        ()
        =
        WorkspaceAddExistingScenario.withPreparedWorkspace
            "add-existing-ordinal-snapshot"
            ".slnx"
            (fun directory _ ->
                for name in [ "zeta.csproj"; "Alpha.csproj"; "beta.csproj" ] do
                    WorkspaceRpcScenario.writeProject (Path.Combine(directory, name))

                Directory.CreateDirectory(Path.Combine(directory, "Lazy")) |> ignore)
            (fun directory _ child ->
                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root
                let revision = WorkspaceAddExistingScenario.revision root

                let started =
                    WorkspaceAddExistingScenario.startSelectorWithPageSize
                        child
                        3u
                        rootId
                        revision
                        4096

                let selectorId =
                    WorkspaceRpcScenario.field "selectorId" started
                    |> RpcValue.requireString "selectorId"

                let parentId =
                    WorkspaceRpcScenario.field "root" started
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                WorkspaceRpcScenario.writeProject (
                    Path.Combine(directory, "Lazy", "Nested.csproj")
                )

                let entries = ResizeArray<RpcValue>()
                let mutable page = started
                let mutable requestId = 5u
                let mutable complete = false

                while not complete do
                    let pageEntries =
                        WorkspaceRpcScenario.field "entries" page
                        |> RpcValue.requireArray "entries"
                        |> Seq.toArray

                    pageEntries.Length |> should be (lessThanOrEqualTo 2)
                    entries.AddRange pageEntries

                    match
                        RpcValue.optionalField "nextToken" (WorkspaceRpcScenario.fields page)
                        |> Option.map (RpcValue.requireString "nextToken")
                    with
                    | None -> complete <- true
                    | Some token ->
                        page <-
                            WorkspaceAddExistingScenario.successful
                                child
                                requestId
                                "workspace/addExisting/children"
                                (WorkspaceRpcScenario.map
                                    [ "selectorId", RpcValue.String selectorId
                                      "parentEntryId", RpcValue.String parentId
                                      "pageSize", RpcValue.Integer 4096L
                                      "continuationToken", RpcValue.String token ])

                        requestId <- requestId + 1u

                let displayNames =
                    entries
                    |> Seq.map (fun entry ->
                        WorkspaceRpcScenario.field "displayName" entry
                        |> RpcValue.requireString "displayName")
                    |> Seq.toArray

                let expectedOrder =
                    entries
                    |> Seq.map (fun entry ->
                        WorkspaceRpcScenario.field "kind" entry = RpcValue.String "directory",
                        WorkspaceRpcScenario.field "displayName" entry
                        |> RpcValue.requireString "displayName")
                    |> Seq.sortWith (fun (leftDirectory, leftName) (rightDirectory, rightName) ->
                        let kind =
                            compare
                                (if leftDirectory then 0 else 1)
                                (if rightDirectory then 0 else 1)

                        if kind <> 0 then
                            kind
                        else
                            StringComparer.Ordinal.Compare(leftName, rightName))
                    |> Seq.map snd
                    |> Seq.toArray

                displayNames |> should equal expectedOrder

                for expected in [ "Alpha.csproj"; "Lazy"; "beta.csproj"; "zeta.csproj" ] do
                    displayNames |> should contain expected

                let lazyDirectoryId =
                    entries
                    |> Seq.find (fun entry ->
                        WorkspaceRpcScenario.field "displayName" entry = RpcValue.String "Lazy")
                    |> WorkspaceAddExistingScenario.entryId

                let nested =
                    WorkspaceAddExistingScenario.successful
                        child
                        requestId
                        "workspace/addExisting/children"
                        (WorkspaceRpcScenario.map
                            [ "selectorId", RpcValue.String selectorId
                              "parentEntryId", RpcValue.String lazyDirectoryId
                              "pageSize", RpcValue.Integer 4096L ])
                    |> WorkspaceRpcScenario.field "entries"
                    |> RpcValue.requireArray "entries"
                    |> Seq.toArray

                nested.Length |> should be (lessThanOrEqualTo 2)

                nested
                |> Seq.map (fun entry ->
                    WorkspaceRpcScenario.field "displayName" entry
                    |> RpcValue.requireString "displayName")
                |> should contain "Nested.csproj")

    [<Fact>]
    member _.``continuations reject a content-stale selector snapshot``() =
        WorkspaceAddExistingScenario.withPreparedWorkspace
            "add-existing-continuation-snapshot"
            ".slnx"
            (fun directory _ ->
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Alpha.csproj"))
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Beta.csproj")))
            (fun directory _ child ->
                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root
                let revision = WorkspaceAddExistingScenario.revision root

                let started =
                    WorkspaceAddExistingScenario.startSelectorWithPageSize
                        child
                        3u
                        rootId
                        revision
                        1

                let selectorId =
                    WorkspaceRpcScenario.field "selectorId" started
                    |> RpcValue.requireString "selectorId"

                let parentId =
                    WorkspaceRpcScenario.field "root" started
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                let token =
                    WorkspaceRpcScenario.field "nextToken" started
                    |> RpcValue.requireString "nextToken"

                File.AppendAllText(Path.Combine(directory, "Beta.csproj"), Environment.NewLine)

                let error, _ =
                    WorkspaceAddExistingScenario.call
                        child
                        5u
                        "workspace/addExisting/children"
                        (WorkspaceRpcScenario.map
                            [ "selectorId", RpcValue.String selectorId
                              "parentEntryId", RpcValue.String parentId
                              "pageSize", RpcValue.Integer 1L
                              "continuationToken", RpcValue.String token ])

                error |> Option.map _.Code |> should equal (Some "invalid_params"))

    [<Fact>]
    member _.``selector boundaries reject symlinks forged handles duplicate or oversized selections and stale source snapshots before mutation``
        ()
        =
        let mutable outside = None

        try
            WorkspaceAddExistingScenario.withPreparedWorkspace
                "add-existing-selector-safety"
                ".slnx"
                (fun directory _ ->
                    for index in 0..256 do
                        WorkspaceRpcScenario.writeProject (
                            Path.Combine(directory, $"Project{index:D3}.csproj")
                        )

                    let external =
                        WorkspaceRpcScenario.temporaryDirectory "add-existing-selector-outside"

                    outside <- Some external
                    let externalProject = Path.Combine(external, "External.csproj")
                    WorkspaceRpcScenario.writeProject externalProject

                    File.CreateSymbolicLink(
                        Path.Combine(directory, "Linked.csproj"),
                        externalProject
                    )
                    |> ignore

                    Directory.CreateSymbolicLink(
                        Path.Combine(directory, "LinkedDirectory"),
                        external
                    )
                    |> ignore)
                (fun directory _ child ->
                    let root = WorkspaceAddExistingScenario.root child
                    let rootId = WorkspaceAddExistingScenario.nodeId root
                    let revision = WorkspaceAddExistingScenario.revision root

                    let started =
                        WorkspaceAddExistingScenario.startSelector child 3u rootId revision

                    let selectorId =
                        WorkspaceRpcScenario.field "selectorId" started
                        |> RpcValue.requireString "selectorId"

                    let selectorRevision =
                        WorkspaceRpcScenario.field "revision" started
                        |> RpcValue.requireInteger "revision"

                    let selectorRootId =
                        WorkspaceRpcScenario.field "root" started
                        |> WorkspaceRpcScenario.field "entryId"
                        |> RpcValue.requireString "entryId"

                    let entries =
                        WorkspaceAddExistingScenario.allEntries
                            child
                            5u
                            selectorId
                            selectorRootId
                            started

                    for name in [ "Linked.csproj"; "LinkedDirectory" ] do
                        let linked =
                            entries
                            |> Seq.find (fun entry ->
                                WorkspaceRpcScenario.field "displayName" entry = RpcValue.String
                                    name)

                        WorkspaceRpcScenario.field "selectable" linked
                        |> should equal (RpcValue.Boolean false)

                        WorkspaceRpcScenario.field "expandable" linked
                        |> should equal (RpcValue.Boolean false)

                    let selectedIds =
                        entries
                        |> Seq.filter (fun entry ->
                            WorkspaceRpcScenario.field "selectable" entry = RpcValue.Boolean true)
                        |> Seq.map WorkspaceAddExistingScenario.entryId
                        |> Seq.toArray

                    selectedIds.Length |> should equal 257

                    let rejected requestId ids =
                        let error, _ =
                            WorkspaceAddExistingScenario.call
                                child
                                requestId
                                "workspace/commands/preview"
                                (WorkspaceAddExistingScenario.previewRequest
                                    rootId
                                    selectorRevision
                                    selectorId
                                    ids)

                        error |> Option.map _.Code |> should equal (Some "invalid_params")

                    rejected 20u [ "forged-out-of-root-entry" ]
                    rejected 21u [ selectedIds[0]; selectedIds[0] ]
                    rejected 22u selectedIds

                    let selectedPath = Path.Combine(directory, "Project000.csproj")

                    let selectedId =
                        entries
                        |> Seq.find (fun entry ->
                            WorkspaceRpcScenario.field "displayName" entry = RpcValue.String
                                "Project000.csproj")
                        |> WorkspaceAddExistingScenario.entryId

                    File.AppendAllText(selectedPath, Environment.NewLine)
                    rejected 23u [ selectedId ]

                    let replacement =
                        WorkspaceAddExistingScenario.startSelector
                            child
                            24u
                            rootId
                            selectorRevision

                    let replacementId =
                        WorkspaceRpcScenario.field "selectorId" replacement
                        |> RpcValue.requireString "selectorId"

                    let oldCloseError, _ =
                        WorkspaceAddExistingScenario.call
                            child
                            26u
                            "workspace/addExisting/close"
                            (WorkspaceRpcScenario.map [ "selectorId", RpcValue.String selectorId ])

                    oldCloseError
                    |> Option.map _.Code
                    |> should equal (Some "selector_unavailable")

                    WorkspaceAddExistingScenario.successful
                        child
                        27u
                        "workspace/addExisting/close"
                        (WorkspaceRpcScenario.map [ "selectorId", RpcValue.String replacementId ])
                    |> WorkspaceRpcScenario.field "closed"
                    |> should equal (RpcValue.Boolean true))
        finally
            outside
            |> Option.iter (fun path ->
                if Directory.Exists path then
                    Directory.Delete(path, true))

    [<Fact>]
    member _.``recursive directory selection enforces its result bound rejects nested links and revalidates every source before execution``
        ()
        =
        WorkspaceAddExistingScenario.withPreparedWorkspaceCapabilities
            "add-existing-recursive-safety"
            ".slnx"
            (fun directory _ ->
                let bulk = Path.Combine(directory, "Bulk")
                Directory.CreateDirectory bulk |> ignore

                for index in 0..256 do
                    WorkspaceRpcScenario.writeProject (
                        Path.Combine(bulk, $"Project{index:D3}.csproj")
                    )

                let linked = Path.Combine(directory, "LinkedSources")
                Directory.CreateDirectory linked |> ignore
                let shared = Path.Combine(directory, "Shared.csproj")
                WorkspaceRpcScenario.writeProject shared
                File.CreateSymbolicLink(Path.Combine(linked, "Linked.csproj"), shared) |> ignore

                let atomic = Path.Combine(directory, "Atomic")
                Directory.CreateDirectory atomic |> ignore
                WorkspaceRpcScenario.writeProject (Path.Combine(atomic, "Alpha.csproj"))
                WorkspaceRpcScenario.writeProject (Path.Combine(atomic, "Beta.csproj")))
            [ "workspace.addExisting.selector"; "workspace.addExisting.directories.v1" ]
            (fun directory solution child ->
                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root
                let revision = WorkspaceAddExistingScenario.revision root
                let started = WorkspaceAddExistingScenario.startSelector child 3u rootId revision

                let selectorId =
                    WorkspaceRpcScenario.field "selectorId" started
                    |> RpcValue.requireString "selectorId"

                let selectorRootId =
                    WorkspaceRpcScenario.field "root" started
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                let entries =
                    WorkspaceAddExistingScenario.allEntries
                        child
                        5u
                        selectorId
                        selectorRootId
                        started

                let entryId name =
                    entries
                    |> Seq.find (fun entry ->
                        WorkspaceRpcScenario.field "displayName" entry = RpcValue.String name)
                    |> WorkspaceAddExistingScenario.entryId

                let rejected requestId name expectedMessage =
                    let error, _ =
                        WorkspaceAddExistingScenario.call
                            child
                            requestId
                            "workspace/commands/preview"
                            (WorkspaceAddExistingScenario.previewRequest
                                rootId
                                revision
                                selectorId
                                [ entryId name ])

                    error |> Option.map _.Code |> should equal (Some "invalid_params")

                    error
                    |> Option.map _.Message
                    |> Option.defaultValue String.Empty
                    |> should haveSubstring expectedMessage

                rejected 20u "Bulk" "more than 256 eligible items"
                rejected 21u "LinkedSources" "contains a symbolic link"

                let atomicRequest =
                    WorkspaceAddExistingScenario.previewRequest
                        rootId
                        revision
                        selectorId
                        [ entryId "Atomic" ]

                let atomicPreview =
                    WorkspaceAddExistingScenario.successful
                        child
                        22u
                        "workspace/commands/preview"
                        atomicRequest

                File.AppendAllText(
                    Path.Combine(directory, "Atomic", "Alpha.csproj"),
                    Environment.NewLine
                )

                let execute =
                    match atomicRequest with
                    | RpcValue.Map fields ->
                        fields.Add(
                            "confirmationToken",
                            WorkspaceRpcScenario.field "confirmationToken" atomicPreview
                        )
                        |> RpcValue.Map
                    | _ -> failwith "The recursive safety request must be a map."

                let executionError, _ =
                    WorkspaceAddExistingScenario.call
                        child
                        23u
                        "workspace/commands/execute"
                        execute

                executionError |> Option.map _.Code |> should equal (Some "invalid_params")

                WorkspaceCommandScenario.openSolution solution
                |> _.SolutionProjects
                |> Seq.isEmpty
                |> should equal true)
