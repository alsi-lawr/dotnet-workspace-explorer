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

    let openModel path =
        let serializer = SolutionSerializers.GetSerializerByMoniker path

        if isNull serializer then
            invalidOp $"No serializer supports {path}."

        serializer.OpenAsync(path, CancellationToken.None)

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

            let includedProject =
                root.Projects |> Seq.find (fun project -> project.Node.Name = "Included")

            let mapping = Assert.Single(includedProject.ConfigurationMappings)

            Assert.Equal("Debug", mapping.SolutionBuildType)
            Assert.Equal("Any CPU", mapping.SolutionPlatform)
            Assert.Equal("Release", mapping.ProjectBuildType)
            Assert.Equal((if extension = ".sln" then "Any CPU" else "AnyCPU"), mapping.ProjectPlatform)
            Assert.True(mapping.Builds)
            Assert.False(mapping.Deploys)

            Assert.Contains(
                includedProject.ConfigurationRules,
                fun rule -> rule.Dimension = "BuildType" && rule.ProjectValue = "Release"
            )

            let dependency = Assert.Single(root.Dependencies)

            let externalProject =
                root.Projects |> Seq.find (fun project -> project.Path.IsExternal)

            Assert.Equal(includedProject.Node.NodeId.Value, dependency.ProjectId.Value)
            Assert.Equal(externalProject.Node.NodeId.Value, dependency.DependsOnProjectId.Value)

            Assert.All(
                root.Projects,
                fun project ->
                    Assert.Equal(WorkspaceCapabilityProfile.UnknownProjectSystem, project.Node.Profile)
                    Assert.Equal(WorkspaceNodeLoadState.Unhydrated, project.Node.NodeLoadState)
                    Assert.False(project.Node.Supports WorkspaceCapabilityId.Write)
            )
        finally
            Helpers.deleteDirectory directory

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``filter resolves backing relative to filter and projects relative to backing solution``
        (extension: string)
        =
        let directory = Helpers.temporaryDirectory ()

        try
            let solutionDirectory =
                Directory.CreateDirectory(Path.Combine(directory, "solution"))

            let filterDirectory = Directory.CreateDirectory(Path.Combine(directory, "filters"))
            let solutionPath = Path.Combine(solutionDirectory.FullName, $"Golden{extension}")
            let filterPath = Path.Combine(filterDirectory.FullName, "Golden.slnf")
            let model = SolutionModel()
            model.AddProject("src/Included.csproj", "Included", null) |> ignore
            model.AddProject("src/Excluded.csproj", "Excluded", null) |> ignore
            model.AddProject("../external/External.csproj", "External", null) |> ignore
            Helpers.save solutionPath model |> _.GetAwaiter().GetResult()

            File.WriteAllText(
                filterPath,
                """
                {
                  "solution": {
                    "path": "../solution/Golden%s",
                    "projects": [ "src/Included.csproj" ]
                  }
                }
                """
                    .Replace("%s", extension)
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

            let external =
                workspace.RootProjection.Projects
                |> Seq.find (fun project -> project.Path.IsExternal)

            Assert.Equal(Path.Combine("..", "external", "External.csproj"), external.Path.SolutionRelativePath)

            Assert.Equal(
                Path.GetFullPath(Path.Combine(solutionDirectory.FullName, "../external/External.csproj")),
                external.Path.AbsolutePath.Value
            )

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
    member _.``filter scalar array and invalid paths return typed invalid input``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solutionPath = Path.Combine(directory, "Golden.slnx")
            Helpers.save solutionPath (SolutionModel()) |> _.GetAwaiter().GetResult()

            let assertInvalid (name: string) (content: string) =
                let filter = Path.Combine(directory, name)
                File.WriteAllText(filter, content)

                match SolutionStore.OpenAsync(filter).Result with
                | Failure(InvalidInput(input, _)) -> Assert.Equal("filter", input)
                | _ -> failwith "Expected invalid filter input."

            assertInvalid "Scalar.slnf" "1"
            assertInvalid "Array.slnf" "[]"
            assertInvalid "InvalidSolutionPath.slnf" "{ \"solution\": { \"path\": \"\\u0000\" } }"

            assertInvalid
                "InvalidProjectPath.slnf"
                "{ \"solution\": { \"path\": \"Golden.slnx\", \"projects\": [ \"\\u0000\" ] } }"

            assertInvalid
                "UnknownProject.slnf"
                "{ \"solution\": { \"path\": \"Golden.slnx\", \"projects\": [ \"Missing.csproj\" ] } }"
        finally
            Helpers.deleteDirectory directory

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``serializer round trips solution projection semantics``(extension: string) =
        let directory = Helpers.temporaryDirectory ()

        try
            let source = Path.Combine(directory, $"RoundTrip{extension}")
            let serialized = Path.Combine(directory, $"RoundTrip.Serialized{extension}")
            let model = SolutionModel()
            let folder = model.AddFolder "/src/"
            folder.AddFile "Directory.Build.props"
            let project = model.AddProject("src/Project.csproj", "Project", folder)
            let external = model.AddProject("../external/External.csproj", "External", null)
            project.AddDependency external
            model.AddBuildType "Debug"
            model.AddPlatform "Any CPU"

            project.AddProjectConfigurationRule(
                ConfigurationRule(BuildDimension.BuildType, "Debug", "Any CPU", "Release")
            )

            if extension = ".slnx" then
                model.Description <- "Serializer-supported description"

            Helpers.save source model |> _.GetAwaiter().GetResult()
            let loaded = Helpers.openModel source |> _.GetAwaiter().GetResult()
            Helpers.save serialized loaded |> _.GetAwaiter().GetResult()
            let reopened = Helpers.openModel serialized |> _.GetAwaiter().GetResult()
            let workspace = SolutionStore.OpenAsync(serialized).Result |> Helpers.success

            Assert.NotNull(reopened.FindFolder "/src/")
            Assert.Equal(2, reopened.SolutionProjects.Count)

            Assert.Single(
                (reopened.SolutionProjects
                 |> Seq.find (fun project -> project.ActualDisplayName = "Project"))
                    .Dependencies
            )
            |> ignore

            Assert.Contains("Debug", reopened.BuildTypes)
            Assert.Contains("Any CPU", reopened.Platforms)

            let root = workspace.RootProjection
            let folderProjection = Assert.Single root.Folders
            let itemProjection = Assert.Single root.Items

            let projectProjection =
                root.Projects |> Seq.find (fun projection -> projection.Node.Name = "Project")

            let externalProjection =
                root.Projects |> Seq.find (fun projection -> projection.Path.IsExternal)

            let rule =
                projectProjection.ConfigurationRules
                |> Seq.find (fun rule -> rule.Dimension = "BuildType" && rule.ProjectValue = "Release")

            let mapping = Assert.Single projectProjection.ConfigurationMappings
            let dependency = Assert.Single root.Dependencies

            Assert.Equal("/src/", folderProjection.Path)
            Assert.Equal(Some "/src/", itemProjection.FolderPath)
            Assert.Equal("Directory.Build.props", itemProjection.RelativePath)
            Assert.Equal((if extension = ".sln" then "Debug" else String.Empty), rule.SolutionBuildType)
            Assert.Equal((if extension = ".sln" then "Any CPU" else String.Empty), rule.SolutionPlatform)
            Assert.Equal("Release", rule.ProjectValue)
            Assert.Equal("Debug", mapping.SolutionBuildType)
            Assert.Equal("Any CPU", mapping.SolutionPlatform)
            Assert.Equal("Release", mapping.ProjectBuildType)
            Assert.Equal((if extension = ".sln" then "Any CPU" else "AnyCPU"), mapping.ProjectPlatform)
            Assert.True(mapping.Builds)
            Assert.False(mapping.Deploys)
            Assert.Equal(projectProjection.Node.NodeId.Value, dependency.ProjectId.Value)
            Assert.Equal(externalProjection.Node.NodeId.Value, dependency.DependsOnProjectId.Value)

            Assert.Equal(
                Path.Combine("..", "external", "External.csproj"),
                externalProjection.Path.SolutionRelativePath
            )

            Assert.Equal(
                Path.GetFullPath(Path.Combine(directory, "../external/External.csproj")),
                externalProjection.Path.AbsolutePath.Value
            )

            if extension = ".slnx" then
                Assert.Equal("Serializer-supported description", reopened.Description)
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``case detector distinguishes case aliases from distinct siblings``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let upper = Path.Combine(directory, "One.slnx")
            let lower = Path.Combine(directory, "one.slnx")
            File.WriteAllText(upper, String.Empty)
            File.WriteAllText(lower, String.Empty)

            let matchingEntries =
                Directory.EnumerateFileSystemEntries(directory)
                |> Seq.filter (fun entry ->
                    String.Equals(Path.GetFileName(entry), "One.slnx", StringComparison.OrdinalIgnoreCase))
                |> Seq.length

            let expected =
                if matchingEntries > 1 then
                    HostFileSystemCaseSemantics.Sensitive
                else
                    HostFileSystemCaseSemantics.Insensitive

            Assert.Equal(expected, HostFileSystemCaseDetector.DetectFromExistingPath upper)
            Assert.Equal(expected, HostFileSystemCaseDetector.DetectFromExistingPath lower)

            let upperDirectory = Directory.CreateDirectory(Path.Combine(directory, "Folder"))
            Directory.CreateDirectory(Path.Combine(directory, "folder")) |> ignore

            let matchingDirectories =
                Directory.EnumerateFileSystemEntries(directory)
                |> Seq.filter (fun entry ->
                    String.Equals(Path.GetFileName(entry), "Folder", StringComparison.OrdinalIgnoreCase))
                |> Seq.length

            let directoryExpected =
                if matchingDirectories > 1 then
                    HostFileSystemCaseSemantics.Sensitive
                else
                    HostFileSystemCaseSemantics.Insensitive

            Assert.Equal(directoryExpected, HostFileSystemCaseDetector.DetectFromExistingPath upperDirectory.FullName)
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``store applies detected case semantics to project identity and filter membership``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, "Case.slnx")
            let filter = Path.Combine(directory, "Case.slnf")
            let model = SolutionModel()
            model.AddProject("src/Case.csproj", "Case", null) |> ignore
            Helpers.save solution model |> _.GetAwaiter().GetResult()

            let semantics = HostFileSystemCaseDetector.DetectFromExistingPath solution
            let workspace = SolutionStore.OpenAsync(solution).Result |> Helpers.success
            let identity = (Assert.Single workspace.RootProjection.Projects).Node.Identity.Value

            if semantics = HostFileSystemCaseSemantics.Sensitive then
                Assert.Equal("project:src/Case.csproj", identity)
            else
                Assert.Equal("project:SRC/CASE.CSPROJ", identity)

            File.WriteAllText(
                filter,
                "{ \"solution\": { \"path\": \"Case.slnx\", \"projects\": [ \"SRC/CASE.CSPROJ\" ] } }"
            )

            if semantics = HostFileSystemCaseSemantics.Sensitive then
                match SolutionStore.OpenAsync(filter).Result with
                | Failure(InvalidInput(input, _)) -> Assert.Equal("filter", input)
                | _ -> failwith "Expected a case-sensitive filter mismatch."
            else
                let filtered = SolutionStore.OpenAsync(filter).Result |> Helpers.success

                let included =
                    filtered.RootProjection.Projects
                    |> Seq.filter (fun project -> not project.IsFilteredOut)
                    |> Seq.length

                Assert.Equal(1, included)
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``backing solution semantics control filter membership and path identity``() =
        let backing = Path.Combine(Path.GetTempPath(), "Backing.slnx")

        Assert.False(
            SolutionStoreTestHooks.FilterContains(
                HostFileSystemCaseSemantics.Sensitive,
                backing,
                "src/Case.csproj",
                "SRC/CASE.CSPROJ"
            )
        )

        Assert.True(
            SolutionStoreTestHooks.FilterContains(
                HostFileSystemCaseSemantics.Insensitive,
                backing,
                "src/Case.csproj",
                "SRC/CASE.CSPROJ"
            )
        )

        Assert.Equal(
            "src/Case.csproj",
            SolutionStoreTestHooks.PathIdentity(HostFileSystemCaseSemantics.Sensitive, "src/Case.csproj")
        )

        Assert.Equal(
            "SRC/CASE.CSPROJ",
            SolutionStoreTestHooks.PathIdentity(HostFileSystemCaseSemantics.Insensitive, "src/Case.csproj")
        )

    [<Fact>]
    member _.``pre-cancelled operations return typed cancellation before resolution``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, "Golden.slnx")
            let filter = Path.Combine(directory, "Golden.slnf")
            Helpers.save solution (SolutionModel()) |> _.GetAwaiter().GetResult()
            File.WriteAllText(filter, "{ \"solution\": { \"path\": \"Golden.slnx\" } }")
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            let assertCancelled target =
                match SolutionStore.OpenAsync(target, cancellation.Token).Result with
                | Failure(Cancelled(_, _)) -> ()
                | _ -> failwith "Expected cancellation."

            assertCancelled solution
            assertCancelled directory
            assertCancelled filter
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``cancellation after ordering materialization returns typed cancellation``() =
        use cancellation = new CancellationTokenSource()

        let values =
            seq {
                for value in 1..2000 do
                    yield value.ToString("D5")

                cancellation.Cancel()
            }

        Assert.Throws<OperationCanceledException>(fun () ->
            SolutionStoreTestHooks.Order(cancellation.Token, values) |> ignore)
        |> ignore

    [<Fact>]
    member _.``excessive direct and filter paths return invalid input when supported by the runtime``() =
        let excessive = String('a', 32768)

        let raisesPathTooLong =
            try
                Path.GetFullPath excessive |> ignore
                false
            with :? PathTooLongException ->
                true

        if raisesPathTooLong then
            match SolutionStore.OpenAsync(excessive).Result with
            | Failure(InvalidInput("targetPath", _)) -> ()
            | _ -> failwith "Expected invalid excessive direct path."

            let directory = Helpers.temporaryDirectory ()

            try
                let filter = Path.Combine(directory, "Excessive.slnf")
                File.WriteAllText(filter, $"{{ \"solution\": {{ \"path\": \"{excessive}\" }} }}")

                match SolutionStore.OpenAsync(filter).Result with
                | Failure(InvalidInput("filter", _)) -> ()
                | _ -> failwith "Expected invalid excessive filter path."
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
