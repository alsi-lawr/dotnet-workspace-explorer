namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type EvaluationWorkerIsolationTests() =
    [<Fact>]
    member _.``prewarming a workspace SDK session keeps the evaluator ready for its first project evaluation``
        ()
        =
        let directory = Test.temporaryDirectory "prewarmed-session"

        try
            let project = Test.simpleProject directory "Prewarmed" ".csproj"
            let solution = Test.writeSolution directory [ project ]
            let workspacePath = WorkspaceArtifactPath.Create solution
            let settings = EvaluationWorkerLaunch(Test.executable, null, "dotnet")
            let evaluator = new ProjectEvaluator(settings)

            try
                for warmup in
                    [ evaluator.WarmAsync(workspacePath, CancellationToken.None).Result
                      evaluator.WarmAsync(workspacePath, CancellationToken.None).Result ] do
                    match warmup with
                    | Success readiness ->
                        (readiness) |> should equal (ProjectEvaluationReadiness.Ready)
                    | Failure failure -> failwithf "Evaluator prewarm failed: %A" failure

                match
                    evaluator
                        .EvaluateAsync(
                            WorkspaceArtifactPath.Create project,
                            workspacePath,
                            CancellationToken.None
                        )
                        .Result
                with
                | Success snapshot -> (snapshot.ProjectPath.Value) |> should equal (project)
                | Failure failure -> failwithf "Prewarmed evaluation failed: %A" failure
            finally
                evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``export-session evaluation observes project changes after invalidation before worker reuse``
        ()
        =
        let directory = Test.temporaryDirectory "export-session"

        try
            let project = Test.simpleProject directory "Exported" ".csproj"
            let solution = Test.writeSolution directory [ project ]

            let writeMarker value =
                Test.write
                    project
                    $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ExportMarker>{value}</ExportMarker>
  </PropertyGroup>
</Project>
"""

            let marker (snapshot: ProjectEvaluationSnapshot) =
                snapshot.Dimensions
                |> Seq.collect _.Properties
                |> Seq.find (fun property -> property.Name = "ExportMarker")
                |> _.Value

            writeMarker "before"

            let settings = EvaluationWorkerLaunch(Test.executable, null, "dotnet")
            let evaluator = new ProjectEvaluator(settings)

            try
                let opened =
                    evaluator
                        .OpenExportSessionAsync(
                            WorkspaceArtifactPath.Create solution,
                            1,
                            CancellationToken.None
                        )
                        .Result

                let session =
                    match opened with
                    | Success value -> value
                    | Failure failure -> failwithf "Could not open export session: %A" failure

                try
                    let evaluate () =
                        match
                            session
                                .EvaluateAsync(
                                    WorkspaceArtifactPath.Create project,
                                    CancellationToken.None
                                )
                                .Result
                        with
                        | Success snapshot -> snapshot
                        | Failure failure -> failwithf "Export evaluation failed: %A" failure

                    (evaluate () |> marker) |> should equal ("before")
                    writeMarker "after"
                    (evaluate () |> marker) |> should equal ("after")
                finally
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult()
            finally
                evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)
