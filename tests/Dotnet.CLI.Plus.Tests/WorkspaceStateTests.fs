namespace Dotnet.CLI.Plus.Tests

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private WorkspaceStateTest =
    let array values = ImmutableArray.CreateRange values

    let diagnostic code message =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            false,
            CorrelationId.New()
        )

    let cancelled<'value> () : WorkspaceOutcome<'value> =
        WorkspaceOutcome.Failure(
            WorkspaceFailure.Cancelled(OperationId.New(), diagnostic "cancelled" "Cancelled by test.")
        )

    let failed<'value> () : WorkspaceOutcome<'value> =
        WorkspaceOutcome.Failure(WorkspaceFailure.Internal(diagnostic "test.failed" "Failed by test."))

    let dimension framework propertyValue itemMetadata resolvedPath =
        EvaluationDimensionSnapshot(
            framework
            |> Option.map TargetFramework
            |> Option.map Nullable
            |> Option.defaultValue (Nullable()),
            array [ EvaluatedProperty("Configuration", propertyValue) ],
            array
                [ EvaluatedItem("Compile", "Shared.fs", resolvedPath, array [ EvaluatedMetadata("Link", itemMetadata) ]) ],
            array [ EvaluatedReference("Other.fsproj", resolvedPath) ],
            array [ EvaluatedReference("System.Text.Json", resolvedPath) ],
            array [ EvaluatedPackage("Example.Package", "1.2.3") ],
            array [ resolvedPath ]
        )

    let snapshot projectPath propertyValue =
        let project = WorkspaceArtifactPath.Create projectPath

        let directory =
            Path.GetDirectoryName projectPath
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let linked = Path.Combine(directory, "Linked.fs")
        File.WriteAllText(linked, "module Linked")
        let resolved = WorkspaceArtifactPath.Create linked

        EvaluationSnapshot(
            project,
            array [ dimension None propertyValue "Shared.fs" resolved ],
            array [ project ],
            array [ project ],
            ImmutableArray<WorkspaceArtifactPath>.Empty,
            WorkspaceCapabilityProfile.Full,
            array [ WorkspaceCapabilityId.Read; WorkspaceCapabilityId.Write ],
            ImmutableArray<WorkspaceDiagnostic>.Empty
        )

    let success value = WorkspaceOutcome.Success value

    let value (outcome: Result<'value, RpcError>) =
        match outcome with
        | Ok result -> result
        | Error error -> failwithf "%s: %s" error.Code error.Message

    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-state-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

type private WorkspaceFixture(projectNames: string list) =
    let directory = WorkspaceStateTest.temporaryDirectory ()
    let solutionPath = Path.Combine(directory, "State.slnx")
    let model = SolutionModel()

    do
        for name in projectNames do
            File.WriteAllText(
                Path.Combine(directory, $"{name}.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            )

            model.AddProject($"{name}.fsproj", name, null) |> ignore

    member _.Directory = directory
    member _.SolutionPath = solutionPath
    member _.Model = model

    member _.SaveAndOpen() =
        let serializer =
            SolutionSerializers.GetSerializerByMoniker solutionPath
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "Missing solution serializer.")

        serializer.SaveAsync(solutionPath, model, CancellationToken.None).GetAwaiter().GetResult()

        match SolutionStore.OpenAsync(solutionPath).Result with
        | WorkspaceOutcome.Success workspace -> workspace
        | WorkspaceOutcome.Failure failure -> failwith failure.Diagnostic.Message

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists directory then
                Directory.Delete(directory, true)

type private FakeWorkspaceServices(initialWorkspace: SolutionWorkspace) =
    let evaluateCalls = Dictionary<string, int>(StringComparer.Ordinal)
    let mutable workspace = initialWorkspace

    let mutable evaluate =
        fun path -> WorkspaceStateTest.success (WorkspaceStateTest.snapshot path "Debug")

    let mutable invalidate =
        fun (_: ImmutableArray<WorkspaceArtifactPath>) ->
            WorkspaceStateTest.success MsBuildInvalidationKind.ProjectOrImport

    let mutable openCalls = 0
    let mutable invalidateCalls = 0
    let mutable refreshCalls = 0
    let mutable disposeCalls = 0

    member _.Workspace
        with set value = workspace <- value

    member _.Evaluate
        with set value = evaluate <- value

    member _.Invalidate
        with set value = invalidate <- value

    member _.EvaluateCalls path =
        match evaluateCalls.TryGetValue path with
        | true, count -> count
        | _ -> 0

    member _.TotalEvaluateCalls = evaluateCalls.Values |> Seq.sum
    member _.OpenCalls = openCalls
    member _.InvalidateCalls = invalidateCalls
    member _.RefreshCalls = refreshCalls
    member _.DisposeCalls = disposeCalls

    member _.Services =
        { OpenAsync =
            fun _ cancellationToken ->
                openCalls <- openCalls + 1

                Task.FromResult(
                    if cancellationToken.IsCancellationRequested then
                        WorkspaceStateTest.cancelled ()
                    else
                        WorkspaceStateTest.success workspace
                )
          EvaluateAsync =
            fun project _ cancellationToken ->
                let path = project.Value

                evaluateCalls[path] <-
                    (match evaluateCalls.TryGetValue path with
                     | true, count -> count + 1
                     | _ -> 1)

                Task.FromResult(
                    if cancellationToken.IsCancellationRequested then
                        WorkspaceStateTest.cancelled ()
                    else
                        evaluate path
                )
          InvalidateAsync =
            fun paths cancellationToken ->
                invalidateCalls <- invalidateCalls + 1

                Task.FromResult(
                    if cancellationToken.IsCancellationRequested then
                        WorkspaceStateTest.cancelled ()
                    else
                        invalidate paths
                )
          RefreshAsync =
            fun () ->
                refreshCalls <- refreshCalls + 1
                Task.CompletedTask
          DisposeAsync =
            fun () ->
                disposeCalls <- disposeCalls + 1
                Task.CompletedTask }

module private StateAssertions =
    let project (workspace: SolutionWorkspace) name =
        workspace.RootProjection.Projects
        |> Seq.find (fun project -> project.Node.Name = name)

    let children (state: WorkspaceState) (project: SolutionProjectProjection) =
        state.ChildrenAsync(project.Node.NodeId.Value, None, 4096, None, CancellationToken.None).Result
        |> WorkspaceStateTest.value

    let changeKinds (delta: WorkspaceDelta) =
        delta.Changes
        |> Seq.map (function
            | WorkspaceChange.Removed _ -> "remove"
            | WorkspaceChange.Replaced _ -> "replace"
            | WorkspaceChange.Moved _ -> "move"
            | WorkspaceChange.Updated _ -> "update"
            | WorkspaceChange.Added _ -> "add")
        |> Seq.toArray

type WorkspaceStateTests() =
    let options limit : WorkspaceStateOptions =
        { HydrationLimit = limit
          TokenSecret = Array.init 32 byte }

    [<Fact>]
    member _.``root and hierarchy do not evaluate and same-name items remain distinct``() =
        use fixture = new WorkspaceFixture([])
        let first = fixture.Model.AddFolder "/first/"
        let second = fixture.Model.AddFolder "/second/"
        first.AddFile "Shared.props"
        second.AddFile "Shared.props"
        let workspace = fixture.SaveAndOpen()
        let fake = FakeWorkspaceServices(workspace)

        let state =
            WorkspaceState.Create(fixture.SolutionPath, workspace, fake.Services, options 2)

        let revision, roots =
            state.RootAsync(CancellationToken.None).Result |> WorkspaceStateTest.value

        Assert.Equal(0L, revision)
        Assert.Equal(2, roots.Length)

        let pages =
            workspace.RootProjection.Folders
            |> Seq.map (fun folder ->
                state.ChildrenAsync(folder.Node.NodeId.Value, None, 4096, None, CancellationToken.None).Result
                |> WorkspaceStateTest.value)
            |> Seq.toArray

        Assert.All(pages, fun page -> Assert.Single(page.Nodes) |> ignore)
        Assert.NotEqual<string>(pages[0].Nodes[0].NodeId.Value, pages[1].Nodes[0].NodeId.Value)
        Assert.Equal(0, fake.TotalEvaluateCalls)
        state.DisposeAsync().GetAwaiter().GetResult()
        state.DisposeAsync().GetAwaiter().GetResult()
        Assert.Equal(1, fake.DisposeCalls)

    [<Fact>]
    member _.``paging binds HMAC revision before removed parent lookup``() =
        use fixture = new WorkspaceFixture([])
        let folder = fixture.Model.AddFolder "/items/"

        for index in 1..300 do
            folder.AddFile("Item" + index.ToString("000") + ".props")

        let workspace = fixture.SaveAndOpen()
        let fake = FakeWorkspaceServices(workspace)

        let state =
            WorkspaceState.Create(fixture.SolutionPath, workspace, fake.Services, options 2)

        let parent = Assert.Single workspace.RootProjection.Folders

        let page: WorkspacePageResult =
            state.ChildrenAsync(parent.Node.NodeId.Value, None, 4096, None, CancellationToken.None).Result
            |> WorkspaceStateTest.value

        Assert.Equal(256, page.Nodes.Length)
        Assert.True(page.NextToken.IsSome)

        let negotiated: WorkspacePageResult =
            state.ChildrenAsync(parent.Node.NodeId.Value, Some 4096, 100, None, CancellationToken.None).Result
            |> WorkspaceStateTest.value

        Assert.Equal(100, negotiated.Nodes.Length)

        let token = page.NextToken.Value.Value

        let tampered =
            token[.. token.Length - 2] + (if token.EndsWith("A") then "B" else "A")

        match
            state.ChildrenAsync(parent.Node.NodeId.Value, None, 4096, Some tampered, CancellationToken.None).Result
        with
        | Error error -> Assert.Equal("invalid_params", error.Code)
        | Ok _ -> failwith "Expected token tamper rejection."

        let reset =
            state
                .ResetAsync(
                    WorkspaceStateTest.diagnostic "watch.overflow" "Overflowed by test.",
                    CancellationToken.None
                )
                .Result

        Assert.Equal(1L, reset.Revision.Value)

        match state.ChildrenAsync(parent.Node.NodeId.Value, None, 4096, Some token, CancellationToken.None).Result with
        | Error error -> Assert.Equal("workspace_conflict", error.Code)
        | Ok _ -> failwith "Expected reset to invalidate the token."

        state.RootAsync(CancellationToken.None).Result
        |> WorkspaceStateTest.value
        |> ignore

        let rebased =
            state.ChildrenAsync(parent.Node.NodeId.Value, None, 4096, None, CancellationToken.None).Result
            |> WorkspaceStateTest.value

        let rebasedToken = rebased.NextToken.Value.Value

        fixture.Model.RemoveFolder folder |> ignore
        fake.Workspace <- fixture.SaveAndOpen()

        let changed =
            state
                .InvalidateFromTransactionAsync(
                    [ WorkspaceArtifactPath.Create fixture.SolutionPath ],
                    CancellationToken.None
                )
                .Result

        match changed with
        | WorkspaceInvalidationResult.Delta _ -> ()
        | value -> failwithf "Expected transaction delta, got %A" value

        match
            state.ChildrenAsync(parent.Node.NodeId.Value, None, 4096, Some rebasedToken, CancellationToken.None).Result
        with
        | Error error -> Assert.Equal("workspace_conflict", error.Code)
        | Ok _ -> failwith "Expected stale token conflict."

        Assert.Equal(1, fake.InvalidateCalls)

    [<Fact>]
    member _.``hydration cancellation is atomic and LRU rehydration evaluates fresh``() =
        use fixture = new WorkspaceFixture([ "A"; "B"; "C" ])
        let workspace = fixture.SaveAndOpen()
        let fake = FakeWorkspaceServices(workspace)

        let state =
            WorkspaceState.Create(fixture.SolutionPath, workspace, fake.Services, options 1)

        let a = StateAssertions.project workspace "A"
        let b = StateAssertions.project workspace "B"
        let c = StateAssertions.project workspace "C"

        StateAssertions.children state a |> ignore
        StateAssertions.children state b |> ignore
        Assert.Equal(1, fake.InvalidateCalls)
        StateAssertions.children state a |> ignore
        Assert.Equal(2, fake.EvaluateCalls a.Path.AbsolutePath.Value)

        let beforeCancellation = state.Revision

        fake.Evaluate <-
            fun path ->
                if path = c.Path.AbsolutePath.Value then
                    WorkspaceStateTest.cancelled ()
                else
                    WorkspaceStateTest.success (WorkspaceStateTest.snapshot path "Debug")

        match state.ChildrenAsync(c.Node.NodeId.Value, None, 4096, None, CancellationToken.None).Result with
        | Error error -> Assert.Equal("cancelled", error.Code)
        | Ok _ -> failwith "Expected typed cancellation."

        Assert.Equal(beforeCancellation, state.Revision)

    [<Fact>]
    member _.``project body deduplicates dimensions and preserves every evaluated category``() =
        use fixture = new WorkspaceFixture([ "A" ])
        let workspace = fixture.SaveAndOpen()
        let project = StateAssertions.project workspace "A"
        let linked = Path.Combine(fixture.Directory, "External.fs")
        File.WriteAllText(linked, "module External")
        let resolved = WorkspaceArtifactPath.Create linked

        let snapshot =
            EvaluationSnapshot(
                project.Path.AbsolutePath,
                WorkspaceStateTest.array
                    [ WorkspaceStateTest.dimension None "Debug" "Shared.fs" resolved
                      WorkspaceStateTest.dimension (Some "net8.0") "Debug" "Shared.fs" resolved
                      WorkspaceStateTest.dimension (Some "net9.0") "Release" "Changed.fs" resolved ],
                WorkspaceStateTest.array [ resolved ],
                WorkspaceStateTest.array [ resolved ],
                WorkspaceStateTest.array [ WorkspaceArtifactPath.Create fixture.Directory ],
                WorkspaceCapabilityProfile.Full,
                WorkspaceStateTest.array [ WorkspaceCapabilityId.Read; WorkspaceCapabilityId.Write ],
                ImmutableArray<WorkspaceDiagnostic>.Empty
            )

        let nodes =
            WorkspaceStatePure.projectBody workspace.WorkspaceDescriptor project snapshot

        let names = nodes |> Seq.map _.Name |> Seq.toArray
        Assert.Equal(8, nodes.Length)
        Assert.Contains(names, fun name -> name.Contains("[net9.0]", StringComparison.Ordinal))
        Assert.Contains(names, fun name -> name.Contains("Link=Changed.fs", StringComparison.Ordinal))
        Assert.Contains(names, fun name -> name.StartsWith("Project reference:", StringComparison.Ordinal))
        Assert.Contains(names, fun name -> name.StartsWith("Reference:", StringComparison.Ordinal))
        Assert.Contains(names, fun name -> name.StartsWith("Package:", StringComparison.Ordinal))
        Assert.Contains(names, fun name -> name.StartsWith("Analyzer:", StringComparison.Ordinal))

        let changed = WorkspaceStateTest.snapshot project.Path.AbsolutePath.Value "Changed"
        Assert.False(WorkspaceStatePure.sameSnapshot snapshot changed)

    [<Fact>]
    member _.``manual refresh preserves materialization and emits only verified change``() =
        use fixture = new WorkspaceFixture([ "A" ])
        let workspace = fixture.SaveAndOpen()
        let fake = FakeWorkspaceServices(workspace)

        let state =
            WorkspaceState.Create(fixture.SolutionPath, workspace, fake.Services, options 2)

        let project = StateAssertions.project workspace "A"
        StateAssertions.children state project |> ignore
        let hydratedRevision = state.Revision

        let noOp =
            state.RefreshAsync(Some hydratedRevision, CancellationToken.None).Result
            |> WorkspaceStateTest.value

        Assert.False(noOp.Reset)
        Assert.True(noOp.Delta.IsNone)
        Assert.Equal(hydratedRevision, noOp.Revision)
        let callsAfterRefresh = fake.EvaluateCalls project.Path.AbsolutePath.Value
        StateAssertions.children state project |> ignore
        Assert.Equal(callsAfterRefresh, fake.EvaluateCalls project.Path.AbsolutePath.Value)

        fake.Evaluate <- fun path -> WorkspaceStateTest.success (WorkspaceStateTest.snapshot path "Release")

        let changed =
            state.RefreshAsync(Some hydratedRevision, CancellationToken.None).Result
            |> WorkspaceStateTest.value

        Assert.False(changed.Reset)
        Assert.True(changed.Delta.IsSome)
        Assert.Equal(hydratedRevision + 1L, changed.Revision)
        Assert.Equal<string[]>([| "replace" |], StateAssertions.changeKinds changed.Delta.Value)
        Assert.Equal(2, fake.RefreshCalls)

    [<Fact>]
    member _.``delta order and payload are directly applicable``() =
        use fixture = new WorkspaceFixture([])
        let workspace = fixture.SaveAndOpen()
        let descriptor = workspace.WorkspaceDescriptor

        let node identity name state =
            WorkspaceNode.CreateWithLoadState(
                descriptor,
                WorkspaceNodeKind.ProjectItem,
                NodeSemanticIdentity.Create identity,
                name,
                WorkspaceCapabilityProfile.Full,
                state
            )

        let parent = node "parent" "Parent" WorkspaceNodeLoadState.Hydrated
        let removed = node "removed" "Removed" WorkspaceNodeLoadState.Hydrated
        let replaced = node "old" "Old" WorkspaceNodeLoadState.Hydrated
        let replacement = node "new" "New" WorkspaceNodeLoadState.Hydrated
        let moved = node "moved" "Moved" WorkspaceNodeLoadState.Hydrated
        let beforeUpdate = node "updated" "Updated" WorkspaceNodeLoadState.Unhydrated
        let afterUpdate = node "updated" "Updated" WorkspaceNodeLoadState.Hydrated
        let added = node "added" "Added" WorkspaceNodeLoadState.Hydrated

        let placement key value parentId index =
            { Key = PlacementKey [ key ]
              Node = value
              ParentId = parentId
              Index = index }

        let oldValues =
            [| placement "parent" parent None 0
               placement "remove" removed None 1
               placement "replace" replaced None 2
               placement "move" moved None 3
               placement "update" beforeUpdate None 4 |]

        let newValues =
            [| placement "parent" parent None 0
               placement "replace" replacement None 1
               placement "move" moved (Some parent.NodeId) 0
               placement "update" afterUpdate None 2
               placement "add" added None 3 |]

        let delta =
            WorkspaceStatePure.diff descriptor.WorkspaceId 4L oldValues newValues
            |> Option.defaultWith (fun () -> failwith "Expected a delta.")

        Assert.Equal<string[]>([| "remove"; "replace"; "move"; "update"; "add" |], StateAssertions.changeKinds delta)

        match delta.Changes[1] with
        | WorkspaceChange.Replaced(oldId, newNode, parentId, index) ->
            Assert.Equal(replaced.NodeId.Value, oldId.Value)
            Assert.Equal(replacement.NodeId.Value, newNode.NodeId.Value)
            Assert.True(parentId.IsNone)
            Assert.Equal(1, index)
        | value -> failwithf "Expected replacement payload, got %A" value

        match delta.Changes[2] with
        | WorkspaceChange.Moved(_, oldParent, oldIndex, newParent, newIndex) ->
            Assert.True(oldParent.IsNone)
            Assert.Equal(3, oldIndex)
            Assert.Equal(parent.NodeId.Value, newParent.Value.Value)
            Assert.Equal(0, newIndex)
        | value -> failwithf "Expected move payload, got %A" value

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``multi-project invalidation never publishes a partial candidate``(cancelSecond: bool) =
        use fixture = new WorkspaceFixture([ "A"; "B" ])
        let workspace = fixture.SaveAndOpen()
        let fake = FakeWorkspaceServices(workspace)

        let state =
            WorkspaceState.Create(fixture.SolutionPath, workspace, fake.Services, options 2)

        let a = StateAssertions.project workspace "A"
        let b = StateAssertions.project workspace "B"
        StateAssertions.children state a |> ignore
        StateAssertions.children state b |> ignore
        let before = state.Revision
        let mutable staged = 0

        fake.Evaluate <-
            fun path ->
                staged <- staged + 1

                if path = b.Path.AbsolutePath.Value then
                    if cancelSecond then
                        WorkspaceStateTest.cancelled ()
                    else
                        WorkspaceStateTest.failed ()
                else
                    WorkspaceStateTest.success (WorkspaceStateTest.snapshot path "Changed")

        let result =
            state
                .InvalidateAsync(
                    WorkspaceStateTest.array [ WorkspaceArtifactPath.Create a.Path.AbsolutePath.Value ],
                    CancellationToken.None
                )
                .Result

        Assert.Equal(2, staged)

        if cancelSecond then
            Assert.Equal(before, state.Revision)
            Assert.Equal(WorkspaceInvalidationResult.None, result)

            let page = StateAssertions.children state a
            Assert.DoesNotContain(page.Nodes, fun node -> node.Name.Contains("Changed", StringComparison.Ordinal))
        else
            match result with
            | WorkspaceInvalidationResult.Reset reset ->
                Assert.Equal(before + 1L, reset.Revision.Value)

                let revision, roots =
                    state.RootAsync(CancellationToken.None).Result |> WorkspaceStateTest.value

                Assert.Equal(reset.Revision.Value, revision)

                Assert.All(
                    roots |> Seq.filter (fun node -> node.NodeKind = WorkspaceNodeKind.Project),
                    fun node -> Assert.Equal(WorkspaceNodeLoadState.Unhydrated, node.NodeLoadState)
                )
            | value -> failwithf "Expected reset, got %A" value

    [<Fact>]
    member _.``export is complete and does not mutate bounded session hydration``() =
        use fixture = new WorkspaceFixture([ "A"; "B" ])
        let workspace = fixture.SaveAndOpen()
        let fake = FakeWorkspaceServices(workspace)

        let state =
            WorkspaceState.Create(fixture.SolutionPath, workspace, fake.Services, options 1)

        let before = state.Revision

        let exported: WorkspaceExportSnapshot =
            state.ExportAsync(before, CancellationToken.None).Result
            |> WorkspaceStateTest.value

        Assert.Equal(before, exported.Revision)

        Assert.True(
            exported.Nodes
            |> Seq.exists (fun node -> node.NodeKind = WorkspaceNodeKind.ProjectItem)
        )

        Assert.Equal(before, state.Revision)

        let _, roots =
            state.RootAsync(CancellationToken.None).Result |> WorkspaceStateTest.value

        Assert.All(
            roots |> Seq.filter (fun node -> node.NodeKind = WorkspaceNodeKind.Project),
            fun node -> Assert.Equal(WorkspaceNodeLoadState.Unhydrated, node.NodeLoadState)
        )

        let missing = StateAssertions.project workspace "B"

        fake.Evaluate <-
            fun path ->
                if path = missing.Path.AbsolutePath.Value then
                    WorkspaceStateTest.cancelled ()
                else
                    WorkspaceStateTest.success (WorkspaceStateTest.snapshot path "Debug")

        match state.ExportAsync(before, CancellationToken.None).Result with
        | Error error -> Assert.Equal("cancelled", error.Code)
        | Ok _ -> failwith "Expected a cancelled export."

        Assert.Equal(before, state.Revision)
        File.Delete missing.Path.AbsolutePath.Value

        match state.ExportAsync(before, CancellationToken.None).Result with
        | Error error -> Assert.Equal("not_found", error.Code)
        | Ok _ -> failwith "Expected a missing-project export failure."

        Assert.Equal(before, state.Revision)

    [<Fact>]
    member _.``watch plan and bounded hints cover exact recursive external and loss paths``() =
        use fixture = new WorkspaceFixture([ "A" ])
        let workspace = fixture.SaveAndOpen()
        let project = StateAssertions.project workspace "A"
        let externalDirectory = WorkspaceStateTest.temporaryDirectory ()

        try
            let external = Path.Combine(externalDirectory, "Linked.fs")
            File.WriteAllText(external, "module External")
            let import = Path.Combine(fixture.Directory, "Imported.props")
            let globalJson = Path.Combine(fixture.Directory, "global.json")
            File.WriteAllText(import, "<Project />")
            File.WriteAllText(globalJson, "{}")

            let resolved = WorkspaceArtifactPath.Create external

            let snapshot =
                EvaluationSnapshot(
                    project.Path.AbsolutePath,
                    WorkspaceStateTest.array [ WorkspaceStateTest.dimension None "Debug" "Linked.fs" resolved ],
                    WorkspaceStateTest.array [ WorkspaceArtifactPath.Create import ],
                    WorkspaceStateTest.array [ WorkspaceArtifactPath.Create globalJson ],
                    WorkspaceStateTest.array [ WorkspaceArtifactPath.Create fixture.Directory ],
                    WorkspaceCapabilityProfile.Full,
                    WorkspaceStateTest.array [ WorkspaceCapabilityId.Read ],
                    ImmutableArray<WorkspaceDiagnostic>.Empty
                )

            let fake = FakeWorkspaceServices(workspace)
            fake.Evaluate <- fun _ -> WorkspaceStateTest.success snapshot

            let state =
                WorkspaceState.Create(fixture.SolutionPath, workspace, fake.Services, options 2)

            StateAssertions.children state project |> ignore
            let plan = state.WatchPlanAsync(CancellationToken.None).Result

            let exactNames =
                plan
                |> Seq.filter (fun spec -> spec.Kind = WatchKind.ExactFile)
                |> Seq.collect _.Filters
                |> Set.ofSeq

            Assert.Contains("State.slnx", exactNames)
            Assert.Contains("A.fsproj", exactNames)
            Assert.Contains("Imported.props", exactNames)
            Assert.Contains("global.json", exactNames)
            Assert.Contains("Directory.Build.props", exactNames)
            Assert.Contains("Directory.Build.targets", exactNames)
            Assert.Contains("Directory.Packages.props", exactNames)
            Assert.Contains("Linked.fs", exactNames)
            Assert.Contains(plan, fun spec -> spec.Kind = WatchKind.RecursiveGlob && spec.IncludeSubdirectories)

            let hints = BoundedHintBuffer(2, StringComparer.Ordinal)
            hints.Resume()
            Assert.True(hints.Add import)
            Assert.True(hints.Add import)
            Assert.True(hints.Add globalJson)
            Assert.False(hints.Add external)
            Assert.Equal(HintDrain.Lost, hints.Drain())
            hints.Resume()
            Assert.True(hints.Add external)

            match hints.Drain() with
            | HintDrain.Hints values -> Assert.Equal(external, Assert.Single(values).Value)
            | value -> failwithf "Expected resumed hints, got %A" value

            Assert.Equal<string[]>([| import; external |], WatcherHints.renamePaths import external |> Seq.toArray)
        finally
            Directory.Delete(externalDirectory, true)
