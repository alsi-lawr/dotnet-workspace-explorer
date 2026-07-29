namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.CommandLine
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type EvaluationWorkerIsolationTests() =
    [<Fact>]
    member _.``should isolate export evaluation and invalidate before reusing a worker lane``() =
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

                    Assert.Equal("before", evaluate () |> marker)
                    writeMarker "after"
                    Assert.Equal("after", evaluate () |> marker)
                finally
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult()
            finally
                evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)
