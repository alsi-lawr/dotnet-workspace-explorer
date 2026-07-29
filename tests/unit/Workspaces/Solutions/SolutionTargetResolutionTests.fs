namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Xml.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open FsUnit.Xunit
open Xunit

[<Collection("Solution contracts")>]
type SolutionTargetResolutionTests() =
    [<Fact>]
    member _.``should retain distinct classifications for ambiguous targets and invalid filters``
        ()
        =
        let directory = SolutionScenario.temporaryDirectory ()

        try
            SolutionScenario.save (Path.Combine(directory, "First.sln")) (SolutionModel())
            File.WriteAllText(Path.Combine(directory, "Second.slnf"), "{}")

            match SolutionWorkspaceReader.OpenAsync(directory).Result with
            | Failure(AmbiguousTarget("solution", _)) -> ()
            | outcome -> failwithf "Expected ambiguous_target, got %A" outcome

            for name, content in
                [ "Malformed.slnf", "{"
                  "Scalar.slnf", "1"
                  "Missing.slnf", "{ \"solution\": { \"path\": \"Absent.sln\" } }" ] do
                let path = Path.Combine(directory, name)
                File.WriteAllText(path, content)

                match name, SolutionWorkspaceReader.OpenAsync(path).Result with
                | "Missing.slnf", Failure(NotFound(target, _)) ->
                    Assert.EndsWith("Absent.sln", target)
                | _, Failure(InvalidInput("filter", _)) -> ()
                | _, outcome ->
                    failwithf "Expected a typed filter failure for %s, got %A" name outcome
        finally
            SolutionScenario.delete directory

    [<Fact>]
    member _.``should govern project and filter identity with detected filesystem case semantics``
        ()
        =
        let directory = SolutionScenario.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, "Case.slnx")
            let filter = Path.Combine(directory, "Case.slnf")
            let model = SolutionModel()
            model.AddProject("src/Case.csproj", "Case", null) |> ignore
            SolutionScenario.save solution model
            let semantics = FileSystemCaseSensitivityDetector.DetectFromExistingPath solution

            let identity =
                (Assert.Single((SolutionScenario.openWorkspace solution).Contents.Projects))
                    .Node.Identity.Value

            Assert.Equal(
                (if semantics = FileSystemCaseSensitivity.Sensitive then
                     "project:src/Case.csproj"
                 else
                     "project:SRC/CASE.CSPROJ"),
                identity
            )

            File.WriteAllText(
                filter,
                "{ \"solution\": { \"path\": \"Case.slnx\", \"projects\": [ \"SRC/CASE.CSPROJ\" ] } }"
            )

            match semantics, SolutionWorkspaceReader.OpenAsync(filter).Result with
            | FileSystemCaseSensitivity.Sensitive, Failure(InvalidInput("filter", _)) -> ()
            | FileSystemCaseSensitivity.Insensitive, Success workspace ->
                Assert.Single(
                    workspace.Contents.Projects
                    |> Seq.filter (fun project -> not project.IsFilteredOut)
                )
                |> ignore
            | _, outcome ->
                failwithf "Filter membership did not follow host case semantics: %A" outcome
        finally
            SolutionScenario.delete directory
