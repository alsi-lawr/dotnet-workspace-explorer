namespace Dotnet.WorkspaceExplorer.PackageExplorer

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Workspaces


type internal InstalledPackageEvaluation =
    { Snapshot: ProjectEvaluationSnapshot
      Graphs: InstalledPackageGraph list }

[<RequireQualifiedAccess>]
module internal NuGetInstalledPackages =
    let private failure kind message retry =
        PackageFailure.create kind message retry |> Result.defaultWith (failwithf "%A")

    let private projectExtensions = set [ ".csproj"; ".fsproj"; ".vbproj" ]

    let private isProject (path: string) =
        projectExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())

    let private directoryProjects (root: string) =
        let rec collect (directory: string) =
            seq {
                for file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly) do
                    if isProject file then
                        yield Path.GetFullPath file

                for child in Directory.EnumerateDirectories directory do
                    let name = Path.GetFileName child

                    if
                        name <> "bin"
                        && name <> "obj"
                        && name <> ".git"
                        && name <> ".agent-workspace"
                    then
                        yield! collect child
            }

        collect root
        |> Seq.distinct
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        |> Seq.toList

    let projectPaths target =
        async {
            let path = PackageWorkspaceTarget.path target

            match PackageWorkspaceTarget.kind target with
            | PackageWorkspaceTargetKind.Project _ -> return Ok [ path ]
            | PackageWorkspaceTargetKind.Directory ->
                try
                    return Ok(directoryProjects path)
                with
                | :? IOException
                | :? UnauthorizedAccessException ->
                    return
                        Error(
                            failure
                                PackageFailureKind.Internal
                                "The package explorer could not enumerate projects in the target directory."
                                PackageFailureRetry.Transient
                        )
            | PackageWorkspaceTargetKind.Solution
            | PackageWorkspaceTargetKind.SolutionXml
            | PackageWorkspaceTargetKind.SolutionFilter ->
                let! opened = SolutionWorkspaceReader.OpenAsync path |> Async.AwaitTask

                match opened with
                | Success workspace ->
                    return
                        workspace.Contents.Projects
                        |> Seq.filter (fun project -> not project.IsFilteredOut)
                        |> Seq.map _.Path.AbsolutePath.Value
                        |> Seq.filter isProject
                        |> Seq.distinct
                        |> Seq.sortWith (fun left right ->
                            StringComparer.Ordinal.Compare(left, right))
                        |> Seq.toList
                        |> Ok
                | Failure error ->
                    return
                        Error(
                            failure
                                PackageFailureKind.InvalidRequest
                                error.Diagnostic.Message
                                PackageFailureRetry.AfterUserAction
                        )
        }

    let private nativeMsBuildPath (value: string) =
        value
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)

    let private assetsPath (snapshot: ProjectEvaluationSnapshot) =
        let projectDirectory =
            Path.GetDirectoryName snapshot.ProjectPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let intermediate =
            snapshot.Dimensions
            |> Seq.tryPick (fun dimension ->
                dimension.Properties
                |> Seq.tryFind (fun property ->
                    property.Name = "BaseIntermediateOutputPath"
                    && not (String.IsNullOrWhiteSpace property.Value))
                |> Option.map _.Value)
            |> Option.defaultValue "obj"
            |> nativeMsBuildPath

        let directory =
            if Path.IsPathRooted intermediate then
                intermediate
            else
                Path.Combine(projectDirectory, intermediate)

        Path.Combine(directory, "project.assets.json")

    let private evaluateProject
        (evaluator: ProjectEvaluator)
        (workspacePath: string)
        (projectPath: string)
        restoreVerified
        =
        async {
            let! evaluated =
                evaluator.EvaluateAsync(
                    WorkspaceArtifactPath.Create projectPath,
                    WorkspaceArtifactPath.Create workspacePath
                )
                |> Async.AwaitTask

            match evaluated with
            | Failure error ->
                return
                    Error(
                        failure
                            PackageFailureKind.Internal
                            error.Diagnostic.Message
                            PackageFailureRetry.Transient
                    )
            | Success snapshot ->
                match PackageWorkspaceTarget.file projectPath with
                | Error _ ->
                    return
                        Error(
                            failure
                                PackageFailureKind.Unsupported
                                "The evaluated package project has an unsupported file type."
                                PackageFailureRetry.Never
                        )
                | Ok projectTarget ->
                    match NuGetSources.loadCatalog projectTarget with
                    | Error catalogFailure -> return Error catalogFailure
                    | Ok catalog ->
                        let configuration =
                            InstalledPackageGraphs.configurationFor snapshot projectTarget catalog

                        return
                            { Snapshot = snapshot
                              Graphs =
                                InstalledPackageGraphs.readSnapshot
                                    { configuration with
                                        RestoreVerified = restoreVerified }
                                    snapshot
                                    (assetsPath snapshot) }
                            |> Ok
        }

    let private readEvaluationResolved
        (evaluator: ProjectEvaluator)
        restoreVerified
        (request: PackageRequest<unit>)
        projectPaths
        =
        async {
            let workspacePath = PackageWorkspaceTarget.path request.Target

            let! results =
                projectPaths
                |> List.map (fun project ->
                    evaluateProject evaluator workspacePath project restoreVerified)
                |> Async.Sequential

            match
                results
                |> Array.tryPick (function
                    | Error error -> Some error
                    | Ok _ -> None)
            with
            | Some error -> return Error error
            | None -> return results |> Array.choose Result.toOption |> Array.toList |> Ok
        }

    let readEvaluationWithEvaluator (evaluator: ProjectEvaluator) (request: PackageRequest<unit>) =
        async {
            let! projects = projectPaths request.Target

            match projects with
            | Error error -> return Error error
            | Ok [] ->
                return
                    Error(
                        failure
                            PackageFailureKind.NotFound
                            "The package explorer target contains no supported projects."
                            PackageFailureRetry.AfterUserAction
                    )
            | Ok resolved -> return! readEvaluationResolved evaluator false request resolved
        }

    let readWithEvaluator (evaluator: ProjectEvaluator) (request: PackageRequest<unit>) =
        async {
            let! evaluated = readEvaluationWithEvaluator evaluator request
            return evaluated |> Result.map (List.collect _.Graphs)
        }

    let readWithFactory
        (evaluatorFactory: unit -> ProjectEvaluator)
        (request: PackageRequest<unit>)
        =
        async {
            let evaluator = evaluatorFactory ()

            try
                return! readWithEvaluator evaluator request
            finally
                evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    let read (request: PackageRequest<unit>) =
        readWithFactory (fun () -> new ProjectEvaluator()) request

    let private duplicateRequestFailure () =
        failure
            PackageFailureKind.InvalidRequest
            "The package request identifier is already active."
            PackageFailureRetry.Never

    let private cancelledFailure () =
        failure
            PackageFailureKind.Cancelled
            "The package work was cancelled."
            PackageFailureRetry.Never

    let private emitGraph
        (cancellation: CancellationToken)
        (sink: PackageBatchSink<InstalledPackageEntry>)
        (graph: InstalledPackageGraph)
        =
        let entries =
            match graph.Packages with
            | [] ->
                [ { Target = graph.Target
                    GraphState = graph.State
                    Package = None } ]
            | packages ->
                packages
                |> List.map (fun package ->
                    { Target = graph.Target
                      GraphState = graph.State
                      Package = Some package })

        let rec emit =
            function
            | [] -> async.Return()
            | entry :: remaining ->
                async {
                    cancellation.ThrowIfCancellationRequested()
                    do! sink cancellation (NonEmptyList.singleton entry)
                    cancellation.ThrowIfCancellationRequested()
                    return! emit remaining
                }

        emit entries

    let internal streamWithFunctions
        resolveProjects
        (restore: RunInstalledRestore option)
        evaluate
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (request: PackageRequest<unit>)
        sink
        =
        PackageProducer.cancellable
            requests
            request.Id
            (duplicateRequestFailure ())
            (cancelledFailure ())
            (fun cancellation ->
                async {
                    cancellation.ThrowIfCancellationRequested()
                    let! projects = resolveProjects request.Target

                    match projects with
                    | Error error -> return Error error
                    | Ok [] ->
                        return
                            Error(
                                failure
                                    PackageFailureKind.NotFound
                                    "The package explorer target contains no supported projects."
                                    PackageFailureRetry.AfterUserAction
                            )
                    | Ok resolved ->
                        let workspacePath = PackageWorkspaceTarget.path request.Target

                        let workingDirectory =
                            if Directory.Exists workspacePath then
                                workspacePath
                            else
                                Path.GetDirectoryName workspacePath
                                |> Option.ofObj
                                |> Option.defaultValue (Directory.GetCurrentDirectory())

                        let rec produce =
                            function
                            | [] -> async.Return(Ok())
                            | project :: remaining ->
                                async {
                                    cancellation.ThrowIfCancellationRequested()

                                    let! restored =
                                        match restore with
                                        | None -> async.Return(Ok())
                                        | Some run -> run workingDirectory project cancellation

                                    match restored with
                                    | Error error -> return Error error
                                    | Ok() ->
                                        cancellation.ThrowIfCancellationRequested()

                                        let! evaluated =
                                            evaluate workspacePath project restore.IsSome

                                        match evaluated with
                                        | Error error -> return Error error
                                        | Ok graphs ->
                                            for graph in graphs do
                                                do! emitGraph cancellation sink graph

                                            return! produce remaining
                                }

                        return! produce resolved
                })

    let private streamWith
        (evaluatorFactory: unit -> ProjectEvaluator)
        (restore: RunInstalledRestore option)
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (request: PackageRequest<unit>)
        sink
        =
        async {
            let evaluator = lazy (evaluatorFactory ())

            let evaluate workspacePath project restoreVerified =
                async {
                    let! result =
                        evaluateProject evaluator.Value workspacePath project restoreVerified

                    return result |> Result.map _.Graphs
                }

            try
                return! streamWithFunctions projectPaths restore evaluate requests request sink
            finally
                if evaluator.IsValueCreated then
                    evaluator.Value.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    let readStreamWithFactory
        (evaluatorFactory: unit -> ProjectEvaluator)
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        =
        streamWith evaluatorFactory None requests

    let refreshWith
        (evaluatorFactory: unit -> ProjectEvaluator)
        (runRestore: RunInstalledRestore)
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (request: PackageRequest<unit>)
        sink
        =
        streamWith evaluatorFactory (Some runRestore) requests request sink
