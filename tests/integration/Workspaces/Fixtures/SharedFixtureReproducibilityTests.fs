namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open System.Xml.Linq
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type SharedFixtureReproducibilityTests() =
    [<Fact>]
    member _.``should regenerate shared fixtures and the scale corpus exactly with non-packable assets``
        ()
        =
        let root = FixtureScenario.repositoryRoot AppContext.BaseDirectory

        for project in
            [ "tests/unit/Workspaces/Dotnet.WorkspaceExplorer.Workspaces.UnitTests.fsproj"
              "tests/unit/Rpc/Dotnet.WorkspaceExplorer.Rpc.UnitTests.fsproj"
              "tests/integration/ProjectEvaluation/Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests.fsproj"
              "tests/integration/Workspaces/Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests.fsproj" ] do
            let items =
                XDocument.Load(Path.Combine(root, project)).Descendants(XName.Get "None")
                |> Seq.filter (fun item ->
                    match item.Attribute(XName.Get "Include") with
                    | null -> false
                    | includePath ->
                        includePath.Value.Contains("Fixtures", StringComparison.Ordinal))
                |> Seq.toArray

            (items) |> should not' (be Empty)

            (items)
            |> Seq.iter (fun item ->
                match item.Attribute(XName.Get "Pack") with
                | null -> failwith "Conformance assets must declare Pack=false."
                | pack -> (pack.Value) |> should equal ("false"))

        let fixtureRoot = Path.Combine(root, "tests", "Fixtures")

        let first =
            Path.Combine(Path.GetTempPath(), $"dotnet-workspace-explorer-scale-{Guid.NewGuid():N}")

        let second =
            Path.Combine(Path.GetTempPath(), $"dotnet-workspace-explorer-scale-{Guid.NewGuid():N}")

        let small =
            Path.Combine(
                Path.GetTempPath(),
                $"dotnet-workspace-explorer-small-fixtures-{Guid.NewGuid():N}"
            )

        try
            Directory.CreateDirectory first |> ignore
            Directory.CreateDirectory second |> ignore
            Directory.CreateDirectory small |> ignore
            FixtureScenario.generateSmall small

            FixtureScenario.compareDirectories
                (Path.Combine(fixtureRoot, "SolutionFiles", "CanonicalWorkspace"))
                (Path.Combine(small, "Solutions"))

            FixtureScenario.compareDirectories
                (Path.Combine(fixtureRoot, "ProjectEvaluation", "MultiTargetProject"))
                (Path.Combine(small, "MSBuild", "Projection"))

            (File.ReadAllBytes(Path.Combine(small, "MSBuild", "Unknown.proj")))
            |> should
                equal
                (File.ReadAllBytes(
                    Path.Combine(
                        fixtureRoot,
                        "ProjectEvaluation",
                        "UnsupportedProject",
                        "Unknown.proj"
                    )
                ))

            let firstSolution = FixtureScenario.generateScale first
            let _ = FixtureScenario.generateScale second

            let firstProjects =
                Directory.EnumerateFiles(
                    Path.Combine(first, "src"),
                    "*.csproj",
                    SearchOption.AllDirectories
                )
                |> Seq.toArray

            (File.ReadLines firstSolution
             |> Seq.filter _.Contains("<Project Path=")
             |> Seq.length)
            |> should equal (500)

            (firstProjects.Length) |> should equal (500)

            (firstProjects
             |> Seq.sumBy (fun project ->
                 File.ReadLines project
                 |> Seq.filter _.Contains("<Compile Include=")
                 |> Seq.length))
            |> should equal (250000)

            FixtureScenario.compareDirectories first second
        finally
            if Directory.Exists first then
                Directory.Delete(first, true)

            if Directory.Exists second then
                Directory.Delete(second, true)

            if Directory.Exists small then
                Directory.Delete(small, true)
