namespace Dotnet.CLI.Plus.Solution.Tests

#nowarn "3261"

open System
open System.IO
open System.Threading
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private Helpers =
    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-solution-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let deleteDirectory path =
        if Directory.Exists path then
            Directory.Delete(path, true)

    let save path model =
        let serializer = SolutionSerializers.GetSerializerByMoniker path

        if isNull serializer then
            invalidOp $"No serializer supports {path}."

        serializer.SaveAsync(path, model, CancellationToken.None)

    let success outcome =
        match outcome with
        | Success value -> value
        | Failure failure -> failwithf "Expected success but received %s." failure.Code.Value

type SolutionStoreTests() =
    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``store projects persisted solution hierarchy and external paths``(extension: string) =
        let directory = Helpers.temporaryDirectory ()

        try
            let solutionPath = Path.Combine(directory, $"Golden{extension}")
            let model = SolutionModel()
            let folder = model.AddFolder "/src/"
            folder.AddFile "Directory.Build.props"
            let included = model.AddProject("src/Included.csproj", "Included", folder)
            let external = model.AddProject("../external/External.csproj", "External", null)
            included.AddDependency external
            model.AddBuildType "Debug"
            model.AddPlatform "Any CPU"

            included.AddProjectConfigurationRule(
                ConfigurationRule(BuildDimension.BuildType, "Debug", "Any CPU", "Release")
            )

            Helpers.save solutionPath model |> _.GetAwaiter().GetResult()

            let workspace = SolutionStore.OpenAsync(solutionPath).Result |> Helpers.success
            let root = workspace.RootProjection

            let externalProject =
                root.Projects |> Seq.find (fun project -> project.Path.IsExternal)

            Assert.Equal(
                (if extension = ".sln" then
                     WorkspaceFormat.Sln
                 else
                     WorkspaceFormat.Slnx),
                workspace.WorkspaceDescriptor.WorkspaceFormat
            )

            Assert.Equal(
                Path.GetFullPath(Path.Combine(directory, "../external/External.csproj")),
                externalProject.Path.AbsolutePath.Value
            )

            Assert.Equal(Path.Combine("..", "external", "External.csproj"), externalProject.Path.SolutionRelativePath)
            Assert.Single root.Folders |> ignore
            Assert.Single root.Items |> ignore
            Assert.Equal(2, root.Projects.Length)
            Assert.Single root.BuildTypes |> ignore
            Assert.Single root.Platforms |> ignore
            Assert.Single root.Dependencies |> ignore

            Assert.Contains(
                (root.Projects |> Seq.find (fun project -> project.Node.Name = "Included")).ConfigurationRules,
                fun rule -> rule.Dimension = "BuildType" && rule.ProjectValue = "Release"
            )

            Assert.Single(
                (root.Projects |> Seq.find (fun project -> project.Node.Name = "Included")).ConfigurationMappings
            )
            |> ignore

            Assert.All(
                root.Projects,
                fun project ->
                    Assert.Equal(WorkspaceCapabilityProfile.UnknownProjectSystem, project.Node.Profile)
                    Assert.Equal(WorkspaceNodeLoadState.Unhydrated, project.Node.NodeLoadState)
                    Assert.False(project.Node.Supports WorkspaceCapabilityId.Write)
            )
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``filter resolves backing and projects included and excluded paths relative to filter``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solutionDirectory =
                Directory.CreateDirectory(Path.Combine(directory, "solution"))

            let filterDirectory = Directory.CreateDirectory(Path.Combine(directory, "filters"))
            let solutionPath = Path.Combine(solutionDirectory.FullName, "Golden.slnx")
            let filterPath = Path.Combine(filterDirectory.FullName, "Golden.slnf")
            let model = SolutionModel()
            model.AddProject("src/Included.csproj", "Included", null) |> ignore
            model.AddProject("src/Excluded.csproj", "Excluded", null) |> ignore
            Helpers.save solutionPath model |> _.GetAwaiter().GetResult()

            File.WriteAllText(
                filterPath,
                """
                {
                  "solution": {
                    "path": "../solution/Golden.slnx",
                    "projects": [ "../solution/src/Included.csproj" ]
                  }
                }
                """
            )

            let workspace = SolutionStore.OpenAsync(filterPath).Result |> Helpers.success

            let included =
                workspace.RootProjection.Projects
                |> Seq.find (fun project -> not project.IsFilteredOut)

            let excluded = workspace.RootProjection.Projects |> Seq.find _.IsFilteredOut

            Assert.Equal(WorkspaceFormat.Slnf, workspace.WorkspaceDescriptor.WorkspaceFormat)
            Assert.True(workspace.WorkspaceDescriptor.IsReadOnly)
            Assert.Equal(WorkspaceNodeLoadState.Unhydrated, included.Node.NodeLoadState)
            Assert.Equal(WorkspaceNodeKind.Placeholder, excluded.Node.NodeKind)
            Assert.Equal(WorkspaceNodeLoadState.FilteredOut, excluded.Node.NodeLoadState)
            Assert.False(excluded.Node.Supports WorkspaceCapabilityId.Write)

            Assert.All(
                workspace.RootProjection.Nodes,
                fun node -> Assert.False(node.Supports WorkspaceCapabilityId.Write)
            )
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``directory resolution rejects mixed solution candidates deterministically``() =
        let directory = Helpers.temporaryDirectory ()

        try
            Helpers.save (Path.Combine(directory, "First.sln")) (SolutionModel())
            |> _.GetAwaiter().GetResult()

            File.WriteAllText(Path.Combine(directory, "Second.slnf"), "{}")

            match SolutionStore.OpenAsync(directory).Result with
            | Failure(AmbiguousTarget(target, _)) -> Assert.Equal("solution", target)
            | _ -> failwith "Expected an ambiguous target failure."
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``malformed and missing filter backing input returns typed failures``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let malformed = Path.Combine(directory, "Malformed.slnf")
            let missing = Path.Combine(directory, "Missing.slnf")
            File.WriteAllText(malformed, "{")
            File.WriteAllText(missing, "{ \"solution\": { \"path\": \"Absent.sln\" } }")

            match SolutionStore.OpenAsync(malformed).Result with
            | Failure(InvalidInput(input, _)) -> Assert.Equal("filter", input)
            | _ -> failwith "Expected invalid filter input."

            match SolutionStore.OpenAsync(missing).Result with
            | Failure(NotFound(target, _)) -> Assert.Equal(Path.Combine(directory, "Absent.sln"), target)
            | _ -> failwith "Expected missing backing solution."
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``capability enrichment returns a separate immutable projection``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solutionPath = Path.Combine(directory, "Golden.slnx")
            let model = SolutionModel()
            model.AddProject("Demo.csproj", "Demo", null) |> ignore
            Helpers.save solutionPath model |> _.GetAwaiter().GetResult()
            let workspace = SolutionStore.OpenAsync(solutionPath).Result |> Helpers.success
            let project = Assert.Single workspace.RootProjection.Projects

            let enriched =
                SolutionProjection.EnrichProjectCapabilities(
                    workspace,
                    [ { ProjectId = project.Node.NodeId
                        CapabilityProfile = WorkspaceCapabilityProfile.Full } ]
                )

            Assert.False(project.Node.Supports WorkspaceCapabilityId.Write)
            Assert.True((Assert.Single enriched.RootProjection.Projects).Node.Supports WorkspaceCapabilityId.Write)
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``legacy directory editor remains separate from read-only workspace state``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solutionPath = Path.Combine(directory, "Legacy.slnx")
            let target = Directory.CreateDirectory(Path.Combine(directory, "src", "tools"))
            Helpers.save solutionPath (SolutionModel()) |> _.GetAwaiter().GetResult()

            let result =
                (LegacySolutionCompatibilityEditor.AddDirectoryAsync(
                    solutionPath,
                    target.FullName,
                    CancellationToken.None
                ))
                    .Result

            let reopened =
                (SolutionSerializers.GetSerializerByMoniker solutionPath)
                    .OpenAsync(solutionPath, CancellationToken.None)
                    .Result

            Assert.Equal(0, result.ExitCode)
            Assert.True(result.Message.IsNone)
            Assert.NotNull(reopened.FindFolder "/src/tools/")
        finally
            Helpers.deleteDirectory directory
