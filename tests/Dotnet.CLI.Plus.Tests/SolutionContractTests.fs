namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.IO
open System.Threading
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private SolutionContract =
    let fixturePath name =
        Path.Combine(AppContext.BaseDirectory, "ConformanceFixtures", "Solutions", name)

    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-solution-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let save path model =
        SolutionSerializers
            .GetSerializerByMoniker(path)
            .SaveAsync(path, model, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    let openWorkspace path =
        match SolutionStore.OpenAsync(path).Result with
        | Success workspace -> workspace
        | Failure failure -> failwithf "Expected success, got %s" failure.Code.Value

    let delete path =
        if Directory.Exists path then
            Directory.Delete(path, true)

type SolutionContractTests() =
    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``sln formats project hierarchy dependencies and external paths``(extension: string) =
        let path = SolutionContract.fixturePath $"Canonical{extension}"
        let workspace = SolutionContract.openWorkspace path
        let root = workspace.RootProjection
        let externalProject = root.Projects |> Seq.find _.Path.IsExternal
        let folder = Assert.Single(root.Folders)

        let included =
            root.Projects |> Seq.find (fun project -> project.Node.Name = "Included")

        Assert.Equal(
            (if extension = ".sln" then
                 WorkspaceFormat.Sln
             else
                 WorkspaceFormat.Slnx),
            workspace.WorkspaceDescriptor.WorkspaceFormat
        )

        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), "../external/External.csproj")),
            externalProject.Path.AbsolutePath.Value
        )

        Assert.Equal(Path.Combine("..", "external", "External.csproj"), externalProject.Path.SolutionRelativePath)
        Assert.Equal("/src/", folder.Path)
        Assert.Equal(Some folder.Path, included.ParentFolderPath)
        Assert.Single(root.Items) |> ignore
        Assert.Equal(2, root.Projects.Length)
        Assert.Single(root.Dependencies) |> ignore
        Assert.Contains(root.BuildTypes, fun node -> node.Name = "Debug")
        Assert.Contains(root.Platforms, fun node -> node.Name = "Any CPU")

    [<Fact>]
    member _.``slnf resolves against its backing solution and projects excluded entries as read-only placeholders``() =
        let workspace =
            SolutionContract.openWorkspace (SolutionContract.fixturePath "Filters/Canonical.slnf")

        let included =
            workspace.RootProjection.Projects
            |> Seq.find (fun project -> not project.IsFilteredOut)

        let excluded = workspace.RootProjection.Projects |> Seq.find _.IsFilteredOut
        Assert.Equal(WorkspaceFormat.Slnf, workspace.WorkspaceDescriptor.WorkspaceFormat)
        Assert.True(workspace.WorkspaceDescriptor.IsReadOnly)
        Assert.Equal(WorkspaceNodeLoadState.Unhydrated, included.Node.NodeLoadState)
        Assert.Equal(WorkspaceNodeKind.Placeholder, excluded.Node.NodeKind)
        Assert.Equal(WorkspaceNodeLoadState.FilteredOut, excluded.Node.NodeLoadState)

        Assert.All(workspace.RootProjection.Nodes, fun node -> Assert.False(node.Supports WorkspaceCapabilityId.Write))

    [<Fact>]
    member _.``ambiguous targets and invalid filter shapes retain distinct classifications``() =
        let directory = SolutionContract.temporaryDirectory ()

        try
            SolutionContract.save (Path.Combine(directory, "First.sln")) (SolutionModel())
            File.WriteAllText(Path.Combine(directory, "Second.slnf"), "{}")

            match SolutionStore.OpenAsync(directory).Result with
            | Failure(AmbiguousTarget("solution", _)) -> ()
            | outcome -> failwithf "Expected ambiguous_target, got %A" outcome

            for name, content in
                [ "Malformed.slnf", "{"
                  "Scalar.slnf", "1"
                  "Missing.slnf", "{ \"solution\": { \"path\": \"Absent.sln\" } }" ] do
                let path = Path.Combine(directory, name)
                File.WriteAllText(path, content)

                match name, SolutionStore.OpenAsync(path).Result with
                | "Missing.slnf", Failure(NotFound(target, _)) -> Assert.EndsWith("Absent.sln", target)
                | _, Failure(InvalidInput("filter", _)) -> ()
                | _, outcome -> failwithf "Expected a typed filter failure for %s, got %A" name outcome
        finally
            SolutionContract.delete directory

    [<Fact>]
    member _.``detected filesystem case semantics govern project and filter identity``() =
        let directory = SolutionContract.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, "Case.slnx")
            let filter = Path.Combine(directory, "Case.slnf")
            let model = SolutionModel()
            model.AddProject("src/Case.csproj", "Case", null) |> ignore
            SolutionContract.save solution model
            let semantics = HostFileSystemCaseDetector.DetectFromExistingPath solution

            let identity =
                (Assert.Single((SolutionContract.openWorkspace solution).RootProjection.Projects)).Node.Identity.Value

            Assert.Equal(
                (if semantics = HostFileSystemCaseSemantics.Sensitive then
                     "project:src/Case.csproj"
                 else
                     "project:SRC/CASE.CSPROJ"),
                identity
            )

            File.WriteAllText(
                filter,
                "{ \"solution\": { \"path\": \"Case.slnx\", \"projects\": [ \"SRC/CASE.CSPROJ\" ] } }"
            )

            match semantics, SolutionStore.OpenAsync(filter).Result with
            | HostFileSystemCaseSemantics.Sensitive, Failure(InvalidInput("filter", _)) -> ()
            | HostFileSystemCaseSemantics.Insensitive, Success workspace ->
                Assert.Single(
                    workspace.RootProjection.Projects
                    |> Seq.filter (fun project -> not project.IsFilteredOut)
                )
                |> ignore
            | _, outcome -> failwithf "Filter membership did not follow host case semantics: %A" outcome
        finally
            SolutionContract.delete directory
