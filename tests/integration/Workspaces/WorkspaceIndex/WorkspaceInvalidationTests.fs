namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type WorkspaceInvalidationTests() =
    [<Fact>]
    member _.``should reuse static solution projection only for project or import invalidation``() =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "workspace-state-invalidation"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let filter = Path.Combine(directory, "Demo.slnf")
            let project = Path.Combine(directory, "Demo.csproj")
            let oldImport = Path.Combine(directory, "Old.props")
            let freshImport = Path.Combine(directory, "Fresh.props")
            let freshWatch = Path.Combine(directory, "Fresh.targets")
            let freshGlob = Path.Combine(directory, "FreshItems")
            let model = SolutionModel()
            model.AddProject("Demo.csproj", "Demo", null) |> ignore
            WorkspaceRpcScenario.save solution model
            WorkspaceRpcScenario.writeProject project

            File.WriteAllText(
                filter,
                """{ "solution": { "path": "Demo.slnx", "projects": [ "Demo.csproj" ] } }"""
            )

            File.WriteAllText(oldImport, "<Project />")
            File.WriteAllText(freshImport, "<Project />")
            File.WriteAllText(freshWatch, "<Project />")
            Directory.CreateDirectory freshGlob |> ignore

            let workspace =
                match SolutionWorkspaceReader.OpenAsync(filter).Result with
                | Success value -> value
                | Failure failure -> failwithf "Could not open invalidation fixture: %A" failure

            let projectNode =
                workspace.Contents.Projects |> Seq.find (fun value -> not value.IsFilteredOut)

            let snapshot visible imports watchInputs globRoots =
                let relativePath = $"Items/N0001-{visible}.cs"

                let item =
                    EvaluatedItem(
                        "Compile",
                        relativePath,
                        WorkspaceArtifactPath.Create(Path.Combine(directory, relativePath)),
                        ImmutableArray<EvaluatedMetadata>.Empty,
                        0
                    )

                let dimension =
                    ProjectEvaluationDimension(
                        Nullable(),
                        ImmutableArray<EvaluatedProperty>.Empty,
                        ImmutableArray.Create item,
                        ImmutableArray<EvaluatedReference>.Empty,
                        ImmutableArray<EvaluatedReference>.Empty,
                        ImmutableArray<EvaluatedPackage>.Empty,
                        ImmutableArray<EvaluatedReference>.Empty
                    )

                ProjectEvaluationSnapshot(
                    WorkspaceArtifactPath.Create project,
                    ImmutableArray.Create dimension,
                    imports |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange,
                    watchInputs
                    |> Seq.map WorkspaceArtifactPath.Create
                    |> ImmutableArray.CreateRange,
                    globRoots |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange,
                    WorkspaceCapabilityProfile.Full,
                    ImmutableArray<WorkspaceCapabilityId>.Empty,
                    ImmutableArray<WorkspaceDiagnostic>.Empty
                )

            let mutable evaluated = snapshot "true" [ oldImport ] Array.empty Array.empty

            let mutable invalidationKind = ProjectEvaluationInvalidationKind.ProjectOrImport
            let mutable openCount = 0
            let mutable refreshCount = 0

            let services =
                { OpenAsync =
                    fun observedTarget _ ->
                        Assert.Equal(filter, observedTarget)
                        openCount <- openCount + 1
                        Task.FromResult<WorkspaceOutcome<SolutionWorkspace>>(Success workspace)
                  EvaluateAsync =
                    fun observedProject observedBacking _ ->
                        Assert.Equal(project, observedProject.Value)
                        Assert.Equal(solution, observedBacking.Value)

                        Task.FromResult<WorkspaceOutcome<ProjectEvaluationSnapshot>>(
                            Success evaluated
                        )
                  InvalidateAsync =
                    fun _ _ ->
                        Task.FromResult<WorkspaceOutcome<ProjectEvaluationInvalidationKind>>(
                            Success invalidationKind
                        )
                  OpenExportSessionAsync = fun _ _ _ -> failwith "Export was not expected."
                  RefreshAsync =
                    fun () ->
                        refreshCount <- refreshCount + 1
                        Task.CompletedTask
                  DisposeAsync = fun () -> Task.CompletedTask }

            let state =
                WorkspaceIndex.Create(
                    filter,
                    workspace,
                    services,
                    { HydrationLimit = 32
                      ExportCapacity = 3
                      TokenSecret = Array.create 32 1uy }
                )

            let hydrated =
                state
                    .ChildrenAsync(
                        projectNode.Node.Id.Value,
                        Some 100,
                        100,
                        None,
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()

            let hydratedPage =
                match hydrated with
                | Ok page -> page
                | Error error -> failwithf "Could not hydrate invalidation fixture: %A" error

            Assert.Equal(1L, hydratedPage.Revision)

            let itemsFolder =
                hydratedPage.Nodes
                |> Seq.find (fun node -> node.Kind = WorkspaceNodeKind.ProjectFolder)

            let oldItem =
                state
                    .ChildrenAsync(
                        itemsFolder.Id.Value,
                        Some 100,
                        100,
                        None,
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()
                |> function
                    | Ok page ->
                        page.Nodes
                        |> Seq.find (fun node ->
                            node.Kind = WorkspaceNodeKind.ProjectFile
                            && node.Name = "N0001-true.cs")
                    | Error error -> failwithf "Could not page project folder: %A" error

            evaluated <- snapshot "false" [ freshImport ] [ freshWatch ] [ freshGlob ]

            let changed =
                state
                    .InvalidateAsync(
                        ImmutableArray.Create(WorkspaceArtifactPath.Create oldImport),
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()

            Assert.Equal(0, openCount)

            match changed with
            | WorkspaceProjectInvalidationResult.Delta delta ->
                Assert.Equal(1L, delta.BaseRevision.Value)
                Assert.Equal(2L, delta.NewRevision.Value)

                let removedParent =
                    delta.Changes
                    |> Seq.choose (function
                        | Removed(nodeId, parentNodeId, _) when nodeId = oldItem.Id ->
                            Some parentNodeId
                        | _ -> None)
                    |> Seq.exactlyOne

                let newNode, addedParent =
                    delta.Changes
                    |> Seq.choose (function
                        | Added(node, parentNodeId, _) when node.Name = "N0001-false.cs" ->
                            Some(node, parentNodeId)
                        | _ -> None)
                    |> Seq.exactlyOne

                Assert.NotEqual(oldItem.Id, newNode.Id)
                Assert.Equal(Some itemsFolder.Id, removedParent)
                Assert.Equal(removedParent, addedParent)
                Assert.Equal("N0001-false.cs", newNode.Name)
            | result -> failwithf "Expected a project-file replacement delta, got %A" result

            let watchPlan =
                state.WatchPlanAsync(CancellationToken.None).GetAwaiter().GetResult()

            let watchesExact (path: string) =
                watchPlan
                |> Seq.exists (fun spec ->
                    spec.Kind = WorkspaceWatchKind.ExactFile
                    && spec.Directory = Path.GetDirectoryName path
                    && spec.Filters.Contains(Path.GetFileName path))

            Assert.True(watchesExact freshImport)
            Assert.True(watchesExact freshWatch)
            Assert.False(watchesExact oldImport)

            Assert.Contains(
                watchPlan,
                fun spec ->
                    spec.Kind = WorkspaceWatchKind.RecursiveGlob
                    && spec.Directory = freshGlob
                    && spec.IncludeSubdirectories
            )

            for solutionPath in [ filter; solution ] do
                let reopened =
                    state
                        .InvalidateAsync(
                            ImmutableArray.Create(WorkspaceArtifactPath.Create solutionPath),
                            CancellationToken.None
                        )
                        .GetAwaiter()
                        .GetResult()

                Assert.Equal(WorkspaceProjectInvalidationResult.None, reopened)

            Assert.Equal(2, openCount)
            invalidationKind <- ProjectEvaluationInvalidationKind.DotnetSdkSelection

            let toolsetChanged =
                state
                    .InvalidateAsync(
                        ImmutableArray.Create(
                            WorkspaceArtifactPath.Create(Path.Combine(directory, "global.json"))
                        ),
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()

            match toolsetChanged with
            | WorkspaceProjectInvalidationResult.Reset reset ->
                Assert.Equal(3L, reset.Revision.Value)
                Assert.Equal("workspace.toolset_changed", reset.Diagnostics[0].Code.Value)
            | result -> failwithf "Expected a toolset reset, got %A" result

            Assert.Equal(2, openCount)
            Assert.Equal(1, refreshCount)
            state.DisposeAsync().GetAwaiter().GetResult()
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should restage all materialized projects after a transient shared import failure``
        ()
        =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "workspace-state-shared-import-retry"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let sharedImport = Path.Combine(directory, "Shared.props")
            let model = SolutionModel()

            let projects =
                [| for name in [ "Alpha"; "Beta" ] do
                       let path = Path.Combine(directory, $"{name}.csproj")
                       WorkspaceRpcScenario.writeProject path
                       model.AddProject(Path.GetFileName path, name, null) |> ignore
                       yield path |]

            WorkspaceRpcScenario.save solution model
            File.WriteAllText(sharedImport, "<Project />")

            let workspace =
                match SolutionWorkspaceReader.OpenAsync(solution).Result with
                | Success value -> value
                | Failure failure -> failwithf "Could not open retry fixture: %A" failure

            let snapshot (project: string) (visible: string) =
                let name = Path.GetFileNameWithoutExtension project
                let relativePath = $"Items/{name}-{visible}.cs"

                let item =
                    EvaluatedItem(
                        "Compile",
                        relativePath,
                        WorkspaceArtifactPath.Create(Path.Combine(directory, relativePath)),
                        ImmutableArray<EvaluatedMetadata>.Empty,
                        0
                    )

                let dimension =
                    ProjectEvaluationDimension(
                        Nullable(),
                        ImmutableArray<EvaluatedProperty>.Empty,
                        ImmutableArray.Create item,
                        ImmutableArray<EvaluatedReference>.Empty,
                        ImmutableArray<EvaluatedReference>.Empty,
                        ImmutableArray<EvaluatedPackage>.Empty,
                        ImmutableArray<EvaluatedReference>.Empty
                    )

                ProjectEvaluationSnapshot(
                    WorkspaceArtifactPath.Create project,
                    ImmutableArray.Create dimension,
                    ImmutableArray.Create(WorkspaceArtifactPath.Create sharedImport),
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    WorkspaceCapabilityProfile.Full,
                    ImmutableArray<WorkspaceCapabilityId>.Empty,
                    ImmutableArray<WorkspaceDiagnostic>.Empty
                )

            let internalFailure () =
                Failure(
                    Internal(
                        WorkspaceDiagnostic.CreateSimple(
                            WorkspaceDiagnosticSeverity.Error,
                            WorkspaceDiagnosticCode.Create "workspace.test_internal",
                            "The test evaluation failed internally.",
                            true,
                            CorrelationId.New()
                        )
                    )
                )

            let cache = Dictionary<string, ProjectEvaluationSnapshot> StringComparer.Ordinal
            let evaluations = ResizeArray<string * string>()
            let invalidations = ResizeArray<string array>()
            let mutable phase = "hydrate"
            let mutable openCount = 0
            let mutable refreshCount = 0

            let services =
                { OpenAsync =
                    fun _ _ ->
                        openCount <- openCount + 1
                        Task.FromResult<WorkspaceOutcome<SolutionWorkspace>>(Success workspace)
                  EvaluateAsync =
                    fun projectPath _ _ ->
                        let project = projectPath.Value

                        match cache.TryGetValue project with
                        | true, cached ->
                            evaluations.Add(project, $"cached-{phase}")

                            Task.FromResult<WorkspaceOutcome<ProjectEvaluationSnapshot>>(
                                Success cached
                            )
                        | false, _ ->
                            evaluations.Add(project, phase)

                            match phase, Array.findIndex ((=) project) projects with
                            | "first-stage", 0 ->
                                let intermediate = snapshot project "intermediate"
                                cache[project] <- intermediate

                                Task.FromResult<WorkspaceOutcome<ProjectEvaluationSnapshot>>(
                                    Success intermediate
                                )
                            | "first-stage", 1 ->
                                phase <- "final"

                                Task.FromResult<WorkspaceOutcome<ProjectEvaluationSnapshot>>(
                                    internalFailure ()
                                )
                            | "final", _ ->
                                let final =
                                    snapshot
                                        project
                                        $"final-{Path.GetFileNameWithoutExtension project}"

                                cache[project] <- final

                                Task.FromResult<WorkspaceOutcome<ProjectEvaluationSnapshot>>(
                                    Success final
                                )
                            | _ ->
                                let initial = snapshot project "initial"
                                cache[project] <- initial

                                Task.FromResult<WorkspaceOutcome<ProjectEvaluationSnapshot>>(
                                    Success initial
                                )
                  InvalidateAsync =
                    fun paths _ ->
                        let values = paths |> Seq.map _.Value |> Seq.toArray
                        invalidations.Add values

                        if invalidations.Count = 1 then
                            Assert.Equal<string array>([| sharedImport |], values)
                            cache.Clear()
                            phase <- "first-stage"
                        else
                            values |> Array.iter (cache.Remove >> ignore)

                        Task.FromResult<WorkspaceOutcome<ProjectEvaluationInvalidationKind>>(
                            Success ProjectEvaluationInvalidationKind.ProjectOrImport
                        )
                  OpenExportSessionAsync = fun _ _ _ -> failwith "Export was not expected."
                  RefreshAsync =
                    fun () ->
                        refreshCount <- refreshCount + 1
                        Task.CompletedTask
                  DisposeAsync = fun () -> Task.CompletedTask }

            let state =
                WorkspaceIndex.Create(
                    solution,
                    workspace,
                    services,
                    { HydrationLimit = 32
                      ExportCapacity = 3
                      TokenSecret = Array.create 32 1uy }
                )

            let mutable hydratedRevision = 0L

            for project in workspace.Contents.Projects do
                let hydrated =
                    state
                        .ChildrenAsync(
                            project.Node.Id.Value,
                            Some 100,
                            100,
                            None,
                            CancellationToken.None
                        )
                        .GetAwaiter()
                        .GetResult()

                match hydrated with
                | Ok page -> hydratedRevision <- page.Revision
                | Error error -> failwithf "Could not hydrate retry fixture: %A" error

            Assert.Equal(2L, hydratedRevision)

            let changed =
                state
                    .InvalidateAsync(
                        ImmutableArray.Create(WorkspaceArtifactPath.Create sharedImport),
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()

            match changed with
            | WorkspaceProjectInvalidationResult.Delta delta ->
                Assert.Equal(hydratedRevision, delta.BaseRevision.Value)
                Assert.Equal(hydratedRevision + 1L, delta.NewRevision.Value)

                let added =
                    delta.Changes
                    |> Seq.choose (function
                        | Added(node, _, _) when node.Kind = WorkspaceNodeKind.ProjectFile ->
                            Some node.Name
                        | _ -> None)
                    |> Seq.sort
                    |> Seq.toArray

                Assert.Equal<string array>(
                    [| "Alpha-final-Alpha.cs"; "Beta-final-Beta.cs" |],
                    added
                )

                Assert.DoesNotContain(
                    delta.Changes,
                    fun change ->
                        match change with
                        | Added(node, _, _) -> node.Name.Contains "intermediate"
                        | _ -> false
                )
            | result -> failwithf "Expected one complete final-state delta, got %A" result

            Assert.Equal(2, invalidations.Count)

            invalidations[1]
            |> Seq.sort
            |> Seq.toArray
            |> should equal (projects |> Array.sort)

            Assert.Equal(0, openCount)
            Assert.Equal(0, refreshCount)
            Assert.Equal(6, evaluations.Count)
            state.DisposeAsync().GetAwaiter().GetResult()
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should bound retryable project staging failures and cancellation``() =
        let runScenario
            (name: string)
            (fromTransaction: bool)
            (stageFactory:
                CancellationTokenSource
                    -> int
                    -> ProjectEvaluationSnapshot
                    -> WorkspaceOutcome<ProjectEvaluationSnapshot>)
            (cancellationFactory: unit -> CancellationTokenSource)
            =
            let directory = WorkspaceRpcScenario.temporaryDirectory $"workspace-state-{name}"

            try
                let solution = Path.Combine(directory, "Demo.slnx")
                let project = Path.Combine(directory, "Demo.csproj")
                let import = Path.Combine(directory, "Shared.props")
                let model = SolutionModel()
                model.AddProject("Demo.csproj", "Demo", null) |> ignore
                WorkspaceRpcScenario.save solution model
                WorkspaceRpcScenario.writeProject project
                File.WriteAllText(import, "<Project />")

                let workspace =
                    match SolutionWorkspaceReader.OpenAsync(solution).Result with
                    | Success value -> value
                    | Failure failure -> failwithf "Could not open retry-bound fixture: %A" failure

                let snapshot visible =
                    let relativePath = $"Items/Demo-{visible}.cs"

                    let item =
                        EvaluatedItem(
                            "Compile",
                            relativePath,
                            WorkspaceArtifactPath.Create(Path.Combine(directory, relativePath)),
                            ImmutableArray<EvaluatedMetadata>.Empty,
                            0
                        )

                    let dimension =
                        ProjectEvaluationDimension(
                            Nullable(),
                            ImmutableArray<EvaluatedProperty>.Empty,
                            ImmutableArray.Create item,
                            ImmutableArray<EvaluatedReference>.Empty,
                            ImmutableArray<EvaluatedReference>.Empty,
                            ImmutableArray<EvaluatedPackage>.Empty,
                            ImmutableArray<EvaluatedReference>.Empty
                        )

                    ProjectEvaluationSnapshot(
                        WorkspaceArtifactPath.Create project,
                        ImmutableArray.Create dimension,
                        ImmutableArray.Create(WorkspaceArtifactPath.Create import),
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        ImmutableArray<WorkspaceArtifactPath>.Empty,
                        WorkspaceCapabilityProfile.Full,
                        ImmutableArray<WorkspaceCapabilityId>.Empty,
                        ImmutableArray<WorkspaceDiagnostic>.Empty
                    )

                use cancellation = cancellationFactory ()
                let mutable hydrated = false
                let mutable stageEvaluations = 0
                let mutable retryInvalidations = 0
                let mutable refreshCount = 0

                let services =
                    { OpenAsync =
                        fun _ _ ->
                            Task.FromResult<WorkspaceOutcome<SolutionWorkspace>>(Success workspace)
                      EvaluateAsync =
                        fun _ _ _ ->
                            if not hydrated then
                                hydrated <- true

                                Task.FromResult<WorkspaceOutcome<ProjectEvaluationSnapshot>>(
                                    Success(snapshot "initial")
                                )
                            else
                                stageEvaluations <- stageEvaluations + 1

                                Task.FromResult<WorkspaceOutcome<ProjectEvaluationSnapshot>>(
                                    stageFactory cancellation stageEvaluations (snapshot "final")
                                )
                      InvalidateAsync =
                        fun paths _ ->
                            if paths |> Seq.exists (fun path -> path.Value = project) then
                                retryInvalidations <- retryInvalidations + 1

                            Task.FromResult<WorkspaceOutcome<ProjectEvaluationInvalidationKind>>(
                                Success ProjectEvaluationInvalidationKind.ProjectOrImport
                            )
                      OpenExportSessionAsync = fun _ _ _ -> failwith "Export was not expected."
                      RefreshAsync =
                        fun () ->
                            refreshCount <- refreshCount + 1
                            Task.CompletedTask
                      DisposeAsync = fun () -> Task.CompletedTask }

                let state =
                    WorkspaceIndex.Create(
                        solution,
                        workspace,
                        services,
                        { HydrationLimit = 32
                          ExportCapacity = 3
                          TokenSecret = Array.create 32 1uy }
                    )

                let projectNode = workspace.Contents.Projects |> Seq.exactlyOne

                match
                    state
                        .ChildrenAsync(
                            projectNode.Node.Id.Value,
                            Some 100,
                            100,
                            None,
                            CancellationToken.None
                        )
                        .GetAwaiter()
                        .GetResult()
                with
                | Ok page -> Assert.Equal(1L, page.Revision)
                | Error error -> failwithf "Could not hydrate retry-bound fixture: %A" error

                let changed =
                    if fromTransaction then
                        state
                            .InvalidateFromTransactionAsync(
                                [ WorkspaceArtifactPath.Create import ],
                                cancellation.Token
                            )
                            .GetAwaiter()
                            .GetResult()
                    else
                        state
                            .InvalidateAsync(
                                ImmutableArray.Create(WorkspaceArtifactPath.Create import),
                                cancellation.Token
                            )
                            .GetAwaiter()
                            .GetResult()

                let revision = state.Revision
                state.DisposeAsync().GetAwaiter().GetResult()
                changed, stageEvaluations, retryInvalidations, refreshCount, revision
            finally
                if Directory.Exists directory then
                    Directory.Delete(directory, true)

        let diagnostic (code: string) (message: string) =
            WorkspaceDiagnostic.CreateSimple(
                WorkspaceDiagnosticSeverity.Error,
                WorkspaceDiagnosticCode.Create code,
                message,
                false,
                CorrelationId.New()
            )

        let (invalidRecovered,
             invalidRecoveryEvaluations,
             invalidRecoveryRetries,
             invalidRecoveryRefreshes,
             invalidRecoveryRevision) =
            runScenario
                "invalid-recovered"
                false
                (fun _ evaluation final ->
                    if evaluation = 1 then
                        Failure(
                            InvalidInput(
                                "project",
                                diagnostic
                                    "msbuild.project_malformed"
                                    "The project is transiently malformed."
                            )
                        )
                    else
                        Success final)
                (fun () -> new CancellationTokenSource())

        match invalidRecovered with
        | WorkspaceProjectInvalidationResult.Delta delta ->
            Assert.Equal(1L, delta.BaseRevision.Value)
            Assert.Equal(2L, delta.NewRevision.Value)

            Assert.Contains(
                delta.Changes,
                fun change ->
                    match change with
                    | Added(node, _, _) ->
                        node.Kind = WorkspaceNodeKind.ProjectFile && node.Name = "Demo-final.cs"
                    | _ -> false
            )
        | result -> failwithf "Expected an invalid-input recovery delta, got %A" result

        Assert.Equal(2, invalidRecoveryEvaluations)
        Assert.Equal(1, invalidRecoveryRetries)
        Assert.Equal(0, invalidRecoveryRefreshes)
        Assert.Equal(2L, invalidRecoveryRevision)

        let (internalExhausted,
             internalExhaustedEvaluations,
             internalExhaustedRetries,
             internalExhaustedRefreshes,
             internalExhaustedRevision) =
            runScenario
                "internal-exhausted"
                false
                (fun _ _ _ ->
                    Failure(
                        Internal(
                            diagnostic
                                "workspace.test_internal"
                                "The project evaluation failed internally."
                        )
                    ))
                (fun () -> new CancellationTokenSource())

        match internalExhausted with
        | WorkspaceProjectInvalidationResult.Reset reset ->
            Assert.Equal(2L, reset.Revision.Value)
            Assert.Equal("workspace.watch_unverified", reset.Diagnostics[0].Code.Value)
        | result -> failwithf "Expected an internal-error exhaustion reset, got %A" result

        Assert.Equal(21, internalExhaustedEvaluations)
        Assert.Equal(20, internalExhaustedRetries)
        Assert.Equal(1, internalExhaustedRefreshes)
        Assert.Equal(2L, internalExhaustedRevision)

        let (invalidExhausted,
             invalidExhaustedEvaluations,
             invalidExhaustedRetries,
             invalidExhaustedRefreshes,
             invalidExhaustedRevision) =
            runScenario
                "invalid-exhausted"
                false
                (fun _ _ _ ->
                    Failure(
                        InvalidInput(
                            "project",
                            diagnostic "msbuild.project_malformed" "The project is malformed."
                        )
                    ))
                (fun () -> new CancellationTokenSource())

        match invalidExhausted with
        | WorkspaceProjectInvalidationResult.Reset reset ->
            Assert.Equal(2L, reset.Revision.Value)
            Assert.Equal("workspace.watch_unverified", reset.Diagnostics[0].Code.Value)
        | result -> failwithf "Expected an invalid-input exhaustion reset, got %A" result

        Assert.Equal(21, invalidExhaustedEvaluations)
        Assert.Equal(20, invalidExhaustedRetries)
        Assert.Equal(1, invalidExhaustedRefreshes)
        Assert.Equal(2L, invalidExhaustedRevision)

        let (cancelled,
             cancelledEvaluations,
             cancelledRetries,
             cancelledRefreshes,
             cancelledRevision) =
            runScenario
                "cancelled"
                false
                (fun cancellation _ _ ->
                    cancellation.Cancel()

                    Failure(
                        Internal(
                            diagnostic
                                "workspace.test_internal"
                                "The project evaluation failed internally."
                        )
                    ))
                (fun () -> new CancellationTokenSource())

        Assert.Equal(WorkspaceProjectInvalidationResult.None, cancelled)
        Assert.Equal(1, cancelledEvaluations)
        Assert.Equal(0, cancelledRetries)
        Assert.Equal(0, cancelledRefreshes)
        Assert.Equal(1L, cancelledRevision)

        let (transactionReset,
             transactionEvaluations,
             transactionRetries,
             transactionRefreshes,
             transactionRevision) =
            runScenario
                "transaction"
                true
                (fun _ _ _ ->
                    Failure(
                        Internal(
                            diagnostic
                                "workspace.test_internal"
                                "The transaction evaluation failed internally."
                        )
                    ))
                (fun () -> new CancellationTokenSource())

        match transactionReset with
        | WorkspaceProjectInvalidationResult.Reset reset ->
            Assert.Equal(2L, reset.Revision.Value)
            Assert.Equal("workspace.watch_unverified", reset.Diagnostics[0].Code.Value)
        | result -> failwithf "Expected a transaction staging reset, got %A" result

        Assert.Equal(1, transactionEvaluations)
        Assert.Equal(0, transactionRetries)
        Assert.Equal(1, transactionRefreshes)
        Assert.Equal(2L, transactionRevision)
