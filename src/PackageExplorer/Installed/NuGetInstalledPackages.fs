namespace Dotnet.WorkspaceExplorer.PackageExplorer

#nowarn "3261"
#nowarn "3262"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Workspaces

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

                for child in Directory.EnumerateDirectories(directory) do
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

    let private projectPaths target =
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
                let! opened = SolutionWorkspaceReader.OpenAsync(path) |> Async.AwaitTask

                match opened with
                | WorkspaceOutcome.Success workspace ->
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
                | WorkspaceOutcome.Failure error ->
                    return
                        Error(
                            failure
                                PackageFailureKind.InvalidRequest
                                error.Diagnostic.Message
                                PackageFailureRetry.AfterUserAction
                        )
        }

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
        =
        async {
            let! evaluated =
                evaluator.EvaluateAsync(
                    WorkspaceArtifactPath.Create projectPath,
                    WorkspaceArtifactPath.Create workspacePath
                )
                |> Async.AwaitTask

            match evaluated with
            | WorkspaceOutcome.Failure error ->
                return
                    Error(
                        failure
                            PackageFailureKind.Internal
                            error.Diagnostic.Message
                            PackageFailureRetry.Transient
                    )
            | WorkspaceOutcome.Success snapshot ->
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
                            InstalledPackageGraphs.readSnapshot
                                configuration
                                snapshot
                                (assetsPath snapshot)
                            |> Ok
        }

    let read (request: PackageRequest<unit>) =
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
            | Ok projectPaths ->
                let workspacePath = PackageWorkspaceTarget.path request.Target
                let evaluator = new ProjectEvaluator()

                try
                    let! results =
                        projectPaths
                        |> List.map (evaluateProject evaluator workspacePath)
                        |> Async.Sequential

                    match
                        results
                        |> Array.tryPick (function
                            | Error error -> Some error
                            | Ok _ -> None)
                    with
                    | Some error -> return Error error
                    | None ->
                        return
                            results
                            |> Array.choose (function
                                | Ok graphs -> Some graphs
                                | Error _ -> None)
                            |> Array.collect List.toArray
                            |> Array.toList
                            |> Ok
                finally
                    evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }
