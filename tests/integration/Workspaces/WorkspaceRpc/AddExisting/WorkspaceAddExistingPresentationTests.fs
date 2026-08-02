namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Microsoft.VisualStudio.SolutionPersistence.Model
open Xunit

module private WorkspaceAddExistingPresentationScenario =
    let withWorkspace (alias: string) setup action =
        let directory = WorkspaceRpcScenario.temporaryDirectory alias
        let solution = Path.Combine(directory, "Demo.slnx")

        try
            let model = SolutionModel()
            setup directory model
            WorkspaceRpcScenario.save solution model

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

                action directory workspace state target
            finally
                state.DisposeAsync().GetAwaiter().GetResult()
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    let start (selector: AddExistingSelector) workspace state target presentationVersion2 pageSize =
        match
            selector
                .StartAsync(
                    workspace,
                    state,
                    target,
                    "add-existing",
                    state.Revision,
                    Some pageSize,
                    presentationVersion2,
                    CancellationToken.None
                )
                .Result
        with
        | Ok value -> value
        | Error error -> failwithf "Selector start failed: %s: %s" error.Code error.Message

    let mapKeys label value =
        value |> RpcValue.requireMap label |> _.Keys |> Seq.sort |> Seq.toList

    let entries page =
        WorkspaceRpcScenario.field "entries" page
        |> RpcValue.requireArray "entries"
        |> Seq.toArray

    let selectorId started =
        WorkspaceRpcScenario.field "selectorId" started
        |> RpcValue.requireString "selectorId"

    let rootEntryId started =
        WorkspaceRpcScenario.field "root" started
        |> WorkspaceRpcScenario.field "entryId"
        |> RpcValue.requireString "entryId"

    let nextToken page =
        RpcValue.optionalField "nextToken" (WorkspaceRpcScenario.fields page)
        |> Option.map (RpcValue.requireString "nextToken")

    let collectRootPages (selector: AddExistingSelector) revision started =
        let collected = ResizeArray<RpcValue>()
        collected.AddRange(entries started)

        let selectorId = selectorId started
        let rootEntryId = rootEntryId started
        let mutable token = nextToken started

        while token.IsSome do
            let page =
                match selector.Children(selectorId, rootEntryId, Some 4096, token, revision) with
                | Ok value -> value
                | Error error ->
                    failwithf "Selector continuation failed: %s: %s" error.Code error.Message

            collected.AddRange(entries page)
            token <- nextToken page

        collected.ToArray()

    let fieldStrings name value =
        WorkspaceRpcScenario.field name value
        |> RpcValue.requireArray name
        |> Seq.map (RpcValue.requireString name)
        |> Seq.toArray

    let entryNamed name entries =
        entries
        |> Array.find (fun entry ->
            WorkspaceRpcScenario.field "displayName" entry = RpcValue.String name)

[<Collection("Workspace scenarios")>]
type WorkspaceAddExistingPresentationTests() =
    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``selector presentation fields appear through RPC only when the separate v2 capability is negotiated``
        (presentationVersion2: bool)
        =
        WorkspaceAddExistingScenario.withPreparedWorkspaceCapabilities
            $"add-existing-presentation-negotiation-{presentationVersion2}"
            ".slnx"
            (fun directory _ ->
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Available.csproj")))
            ([ "workspace.addExisting.selector"
               if presentationVersion2 then
                   "workspace.addExisting.presentation.v2" ])
            (fun _ _ child ->
                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root
                let revision = WorkspaceAddExistingScenario.revision root

                let started = WorkspaceAddExistingScenario.startSelector child 3u rootId revision

                let assertPresentationFields label value =
                    let keys = WorkspaceAddExistingPresentationScenario.mapKeys label value

                    keys |> Seq.contains "availability" |> should equal presentationVersion2
                    keys |> Seq.contains "gitStates" |> should equal presentationVersion2

                assertPresentationFields
                    "negotiated.root"
                    (WorkspaceRpcScenario.field "root" started)

                WorkspaceAddExistingPresentationScenario.entries started
                |> Array.iter (assertPresentationFields "negotiated.entry"))

    [<Fact>]
    member _.``a legacy Add Existing selector preserves the exact original root and entry maps without acquiring Git``
        ()
        =
        WorkspaceAddExistingPresentationScenario.withWorkspace
            "add-existing-legacy-presentation"
            (fun directory _ ->
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Available.csproj")))
            (fun _ workspace state target ->
                let unexpectedGitRead (_: CancellationToken) =
                    failwith "A legacy selector must not acquire Git presentation."

                use selector =
                    new AddExistingSelector(
                        (fun () -> 4096),
                        TimeProvider.System,
                        unexpectedGitRead
                    )

                let started =
                    WorkspaceAddExistingPresentationScenario.start
                        selector
                        workspace
                        state
                        target
                        false
                        4096

                WorkspaceAddExistingPresentationScenario.mapKeys "legacy.start" started
                |> should
                    equal
                    [ "entries"
                      "expiresAtUtc"
                      "maxSelectionCount"
                      "revision"
                      "root"
                      "selectorId" ]

                let root = WorkspaceRpcScenario.field "root" started

                WorkspaceAddExistingPresentationScenario.mapKeys "legacy.root" root
                |> should
                    equal
                    [ "displayName"; "entryId"; "expandable"; "iconHint"; "kind"; "selectable" ]

                let available =
                    WorkspaceAddExistingPresentationScenario.entries started
                    |> WorkspaceAddExistingPresentationScenario.entryNamed "Available.csproj"

                WorkspaceAddExistingPresentationScenario.mapKeys "legacy.entry" available
                |> should
                    equal
                    [ "displayName"; "entryId"; "expandable"; "iconHint"; "kind"; "selectable" ])

    [<Fact>]
    member _.``a presentation v2 selector exposes stable availability and ordered direct and descendant Git state while globally paging directories first``
        ()
        =
        WorkspaceAddExistingPresentationScenario.withWorkspace
            "add-existing-presentation-v2"
            (fun directory model ->
                let existing = Path.Combine(directory, "Existing.csproj")
                WorkspaceRpcScenario.writeProject existing
                model.AddProject("Existing.csproj", "Existing", null) |> ignore

                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Available.csproj"))
                File.WriteAllText(Path.Combine(directory, "README.txt"), "unsupported")

                let firstDirectory = Path.Combine(directory, "A-Directory")
                let lastDirectory = Path.Combine(directory, "Z-Directory")
                Directory.CreateDirectory firstDirectory |> ignore
                Directory.CreateDirectory lastDirectory |> ignore

                WorkspaceRpcScenario.writeProject (Path.Combine(firstDirectory, "Nested.csproj"))

                File.CreateSymbolicLink(
                    Path.Combine(directory, "Linked.csproj"),
                    Path.Combine(directory, "Available.csproj")
                )
                |> ignore)
            (fun directory workspace state target ->
                let available = Path.Combine(directory, "Available.csproj")
                let existing = Path.Combine(directory, "Existing.csproj")
                let firstDirectory = Path.Combine(directory, "A-Directory")
                let nested = Path.Combine(firstDirectory, "Nested.csproj")
                let deleted = Path.Combine(firstDirectory, "Deleted.csproj")
                let mutable snapshotReads = 0

                let readGitSnapshot (_: CancellationToken) =
                    snapshotReads <- snapshotReads + 1

                    Task.FromResult(
                        Ok(
                            Some
                                { RepositoryRoot = directory
                                  Entries =
                                    [| { Path = available
                                         States =
                                           [| GitStatusState.Untracked
                                              GitStatusState.Staged
                                              GitStatusState.Staged |]
                                         LegacyState = Some GitDecorationState.Added }
                                       { Path = existing
                                         States = [| GitStatusState.Renamed |]
                                         LegacyState = Some GitDecorationState.Changed }
                                       { Path = nested
                                         States = [| GitStatusState.Ignored |]
                                         LegacyState = None }
                                       { Path = deleted
                                         States =
                                           [| GitStatusState.Deleted; GitStatusState.Unstaged |]
                                         LegacyState = Some GitDecorationState.Changed } |] }
                        )
                    )

                use selector =
                    new AddExistingSelector((fun () -> 2), TimeProvider.System, readGitSnapshot)

                let started =
                    WorkspaceAddExistingPresentationScenario.start
                        selector
                        workspace
                        state
                        target
                        true
                        4096

                snapshotReads |> should equal 1

                WorkspaceAddExistingPresentationScenario.entries started
                |> Array.map (fun entry ->
                    WorkspaceRpcScenario.field "displayName" entry
                    |> RpcValue.requireString "displayName")
                |> should equal [| "A-Directory"; "Z-Directory" |]

                let root = WorkspaceRpcScenario.field "root" started

                WorkspaceAddExistingPresentationScenario.mapKeys "v2.root" root
                |> should
                    equal
                    [ "availability"
                      "displayName"
                      "entryId"
                      "expandable"
                      "gitStates"
                      "iconHint"
                      "kind"
                      "selectable" ]

                WorkspaceRpcScenario.field "availability" root
                |> should equal (RpcValue.String "ineligible")

                WorkspaceAddExistingPresentationScenario.fieldStrings "gitStates" root
                |> should
                    equal
                    [| "staged"; "unstaged"; "renamed"; "deleted"; "untracked"; "ignored" |]

                let allEntries =
                    WorkspaceAddExistingPresentationScenario.collectRootPages
                        selector
                        state.Revision
                        started

                let kinds =
                    allEntries
                    |> Array.map (fun entry ->
                        WorkspaceRpcScenario.field "kind" entry |> RpcValue.requireString "kind")

                kinds
                |> should
                    equal
                    (kinds
                     |> Array.sortBy (function
                         | "directory" -> 0
                         | _ -> 1))

                let orderedNames kind =
                    allEntries
                    |> Array.filter (fun entry ->
                        WorkspaceRpcScenario.field "kind" entry = RpcValue.String kind)
                    |> Array.map (fun entry ->
                        WorkspaceRpcScenario.field "displayName" entry
                        |> RpcValue.requireString "displayName")

                for kind in [ "directory"; "file" ] do
                    let names = orderedNames kind

                    names
                    |> should
                        equal
                        (names
                         |> Array.sortWith (fun left right ->
                             StringComparer.Ordinal.Compare(left, right)))

                let assertAvailability name expected =
                    WorkspaceAddExistingPresentationScenario.entryNamed name allEntries
                    |> WorkspaceRpcScenario.field "availability"
                    |> should equal (RpcValue.String expected)

                assertAvailability "Available.csproj" "available"
                assertAvailability "Existing.csproj" "alreadyPresent"
                assertAvailability "README.txt" "ineligible"
                assertAvailability "Linked.csproj" "ineligible"
                assertAvailability "A-Directory" "ineligible"

                let availableEntry =
                    WorkspaceAddExistingPresentationScenario.entryNamed
                        "Available.csproj"
                        allEntries

                WorkspaceAddExistingPresentationScenario.fieldStrings "gitStates" availableEntry
                |> should equal [| "staged"; "untracked" |]

                let firstDirectoryEntry =
                    WorkspaceAddExistingPresentationScenario.entryNamed "A-Directory" allEntries

                WorkspaceAddExistingPresentationScenario.fieldStrings
                    "gitStates"
                    firstDirectoryEntry
                |> should equal [| "unstaged"; "deleted"; "ignored" |]

                let nestedPage =
                    match
                        selector.Children(
                            WorkspaceAddExistingPresentationScenario.selectorId started,
                            WorkspaceAddExistingScenario.entryId firstDirectoryEntry,
                            Some 4096,
                            None,
                            state.Revision
                        )
                    with
                    | Ok value -> value
                    | Error error ->
                        failwithf "Nested selector page failed: %s: %s" error.Code error.Message

                let nestedEntries = WorkspaceAddExistingPresentationScenario.entries nestedPage

                nestedEntries
                |> Array.map (fun entry ->
                    WorkspaceRpcScenario.field "displayName" entry
                    |> RpcValue.requireString "displayName")
                |> should equal [| "Nested.csproj" |]

                WorkspaceAddExistingPresentationScenario.fieldStrings "gitStates" nestedEntries[0]
                |> should equal [| "ignored" |]

                snapshotReads |> should equal 1)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``non Git and safely failed Git acquisition leave v2 selector browsing and selectable source resolution available``
        (failGitRead: bool)
        =
        WorkspaceAddExistingPresentationScenario.withWorkspace
            $"add-existing-git-fallback-{failGitRead}"
            (fun directory _ ->
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Available.csproj"))
                Directory.CreateDirectory(Path.Combine(directory, "Nested")) |> ignore)
            (fun _ workspace state target ->
                let readGitSnapshot (_: CancellationToken) =
                    if failGitRead then
                        Task.FromResult(
                            Error(
                                RpcErrors.create
                                    "git_status_failed"
                                    "The bounded Git read failed."
                                    None
                            )
                        )
                    else
                        Task.FromResult(Ok None)

                use selector =
                    new AddExistingSelector((fun () -> 4096), TimeProvider.System, readGitSnapshot)

                let started =
                    WorkspaceAddExistingPresentationScenario.start
                        selector
                        workspace
                        state
                        target
                        true
                        4096

                let allEntries =
                    WorkspaceAddExistingPresentationScenario.collectRootPages
                        selector
                        state.Revision
                        started

                WorkspaceAddExistingPresentationScenario.fieldStrings
                    "gitStates"
                    (WorkspaceRpcScenario.field "root" started)
                |> should equal [||]

                allEntries
                |> Array.iter (fun entry ->
                    WorkspaceAddExistingPresentationScenario.fieldStrings "gitStates" entry
                    |> should equal [||])

                let available =
                    WorkspaceAddExistingPresentationScenario.entryNamed
                        "Available.csproj"
                        allEntries

                match
                    selector.ResolveSelection(
                        WorkspaceAddExistingPresentationScenario.selectorId started,
                        state.Revision,
                        target.Node.Id.Value,
                        [| WorkspaceAddExistingScenario.entryId available |]
                    )
                with
                | Ok(_, selected) -> selected.Length |> should equal 1
                | Error error ->
                    failwithf
                        "Git fallback prevented source resolution: %s: %s"
                        error.Code
                        error.Message

                match
                    selector.Close(WorkspaceAddExistingPresentationScenario.selectorId started)
                with
                | Ok _ -> ()
                | Error error ->
                    failwithf "Git fallback prevented close: %s: %s" error.Code error.Message)
