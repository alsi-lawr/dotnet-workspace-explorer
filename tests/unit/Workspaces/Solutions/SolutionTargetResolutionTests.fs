namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open FsUnit.Xunit
open Xunit

[<Collection("Solution contracts")>]
type SolutionTargetResolutionTests() =
    [<Fact>]
    member _.``should prefer a unique root solution over nested workspace copies``() =
        let directory = SolutionScenario.temporaryDirectory ()

        try
            let rootSolution = Path.Combine(directory, "Root.slnx")
            let nestedDirectory = Path.Combine(directory, ".agent-workspace", "fixture")
            let nestedSolution = Path.Combine(nestedDirectory, "Nested.slnx")
            Directory.CreateDirectory nestedDirectory |> ignore
            SolutionScenario.save rootSolution (SolutionModel())
            SolutionScenario.save nestedSolution (SolutionModel())

            let workspace = SolutionScenario.openWorkspace directory

            (workspace.SolutionPath.Value) |> should equal (Path.GetFullPath rootSolution)
        finally
            SolutionScenario.delete directory

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
                    (target) |> should endWith ("Absent.sln")
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
                (((SolutionScenario.openWorkspace solution).Contents.Projects) |> Seq.exactlyOne)
                    .Node.Identity.Value

            let expectedIdentity =
                if semantics = FileSystemCaseSensitivity.Sensitive then
                    "project:src/Case.csproj"
                else
                    "project:SRC/CASE.CSPROJ"

            identity |> should equal expectedIdentity

            File.WriteAllText(
                filter,
                "{ \"solution\": { \"path\": \"Case.slnx\", \"projects\": [ \"SRC/CASE.CSPROJ\" ] } }"
            )

            match semantics, SolutionWorkspaceReader.OpenAsync(filter).Result with
            | FileSystemCaseSensitivity.Sensitive, Failure(InvalidInput("filter", _)) -> ()
            | FileSystemCaseSensitivity.Insensitive, Success workspace ->
                (workspace.Contents.Projects
                 |> Seq.filter (fun project -> not project.IsFilteredOut))
                |> Seq.exactlyOne
                |> ignore
            | _, outcome ->
                failwithf "Filter membership did not follow host case semantics: %A" outcome
        finally
            SolutionScenario.delete directory
