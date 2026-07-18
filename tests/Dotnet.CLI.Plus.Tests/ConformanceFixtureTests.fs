namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.IO
open System.Text
open System.Threading
open System.Xml.Linq
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private ConformanceFixture =
    let private utf8 = UTF8Encoding(false)

    let private write path contents =
        use writer = new StreamWriter(path, false, utf8)
        contents writer

    let private writeLines path lines =
        File.WriteAllText(path, String.concat "\n" lines + "\n", utf8)

    let private files directory =
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        |> Seq.map (fun path -> Path.GetRelativePath(directory, path))
        |> Seq.sort
        |> Seq.toArray

    let compareDirectories expected actual =
        let expectedFiles = files expected
        Assert.Equal<string>(expectedFiles, files actual)

        for relative in expectedFiles do
            Assert.Equal<byte>(
                File.ReadAllBytes(Path.Combine(expected, relative)),
                File.ReadAllBytes(Path.Combine(actual, relative))
            )

    let private normalizeSlnx path =
        File.ReadAllLines path
        |> Array.map (fun line ->
            let indentation = line |> Seq.takeWhile Char.IsWhiteSpace |> Seq.length
            String(' ', indentation * 2) + line.TrimStart())
        |> fun lines -> writeLines path lines

    let generateSmall directory =
        let solutions = Path.Combine(directory, "Solutions")
        let msbuild = Path.Combine(directory, "MSBuild")
        let solutionSource = Path.Combine(solutions, "src")
        let projection = Path.Combine(msbuild, "Projection")
        Directory.CreateDirectory(Path.Combine(solutions, "Filters")) |> ignore
        Directory.CreateDirectory(solutionSource) |> ignore
        Directory.CreateDirectory(Path.Combine(projection, "Generated")) |> ignore

        writeLines
            (Path.Combine(solutionSource, "Directory.Build.props"))
            [ "<Project>"
              "    <PropertyGroup>"
              "        <SharedFixture>true</SharedFixture>"
              "    </PropertyGroup>"
              "</Project>" ]

        writeLines
            (Path.Combine(solutionSource, "Included.csproj"))
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "    <PropertyGroup>"
              "        <TargetFramework>net10.0</TargetFramework>"
              "        <ConformanceMarker>initial</ConformanceMarker>"
              "    </PropertyGroup>"
              "</Project>" ]

        writeLines
            (Path.Combine(solutions, "Filters", "Canonical.slnf"))
            [ "{"
              "  \"solution\": {"
              "    \"path\": \"../Canonical.slnx\","
              "    \"projects\": [ \"src/Included.csproj\" ]"
              "  }"
              "}" ]

        writeLines
            (Path.Combine(projection, "Directory.Build.props"))
            [ "<Project>"
              "    <PropertyGroup>"
              "        <ImportedProperty>before</ImportedProperty>"
              "    </PropertyGroup>"
              "</Project>" ]

        writeLines
            (Path.Combine(projection, "Project.csproj"))
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "    <PropertyGroup>"
              "        <TargetFrameworks>net8.0;net9.0</TargetFrameworks>"
              "        <EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
              "    </PropertyGroup>"
              "    <ItemGroup>"
              "        <Compile Include=\"Eight.cs\" Condition=\"'$(TargetFramework)' == 'net8.0'\" />"
              "        <Compile Include=\"Generated/**/*.cs\" />"
              "    </ItemGroup>"
              "</Project>" ]

        writeLines (Path.Combine(projection, "Eight.cs")) [ "class Eight { }" ]
        writeLines (Path.Combine(projection, "Generated", "Existing.cs")) [ "class Existing { }" ]

        writeLines
            (Path.Combine(msbuild, "Unknown.proj"))
            [ "<Project>"
              "  <PropertyGroup>"
              "    <Value>readable</Value>"
              "  </PropertyGroup>"
              "</Project>" ]

        let model = SolutionModel()
        let folder = model.AddFolder "/src/"
        folder.AddFile "Directory.Build.props"
        let included = model.AddProject("src/Included.csproj", "Included", folder)
        let external = model.AddProject("../external/External.csproj", "External", null)
        included.AddDependency external
        model.AddBuildType "Debug"
        model.AddPlatform "Any CPU"

        for name in [ "Canonical.sln"; "Canonical.slnx" ] do
            let path = Path.Combine(solutions, name)

            SolutionSerializers
                .GetSerializerByMoniker(path)
                .SaveAsync(path, model, CancellationToken.None)
                .GetAwaiter()
                .GetResult()

        normalizeSlnx (Path.Combine(solutions, "Canonical.slnx"))

    let generateScale directory =
        let solution = Path.Combine(directory, "Scale.slnx")

        write solution (fun writer ->
            writer.WriteLine("<Solution>")

            for project in 1..500 do
                writer.WriteLine($"  <Project Path=\"src/P{project:D4}/P{project:D4}.csproj\" />")

            writer.WriteLine("</Solution>"))

        for project in 1..500 do
            let directory = Path.Combine(directory, "src", $"P{project:D4}")
            Directory.CreateDirectory directory |> ignore

            write (Path.Combine(directory, $"P{project:D4}.csproj")) (fun writer ->
                writer.WriteLine("<Project Sdk=\"Microsoft.NET.Sdk\">")

                writer.WriteLine(
                    "  <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>"
                )

                writer.WriteLine("  <ItemGroup>")

                for node in 1..500 do
                    writer.WriteLine($"    <Compile Include=\"Items/N{node:D4}.cs\" />")

                writer.WriteLine("  </ItemGroup>")
                writer.WriteLine("</Project>"))

        solution

    let rec repositoryRoot directory =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            directory
        else
            match Directory.GetParent directory with
            | null -> failwith "Could not locate the repository root."
            | parent -> repositoryRoot parent.FullName

type ConformanceFixtureTests() =
    [<Fact>]
    member _.``should regenerate shared fixtures and the scale corpus exactly with non-packable assets``() =
        let root = ConformanceFixture.repositoryRoot AppContext.BaseDirectory

        for project in
            [ "tests/Dotnet.CLI.Plus.Tests/Dotnet.CLI.Plus.Tests.fsproj"
              "tests/Dotnet.CLI.Plus.Transport.Tests/Dotnet.CLI.Plus.Transport.Tests.fsproj"
              "tests/Dotnet.CLI.Plus.MSBuild.Tests/Dotnet.CLI.Plus.MSBuild.Tests.fsproj" ] do
            let items =
                XDocument.Load(Path.Combine(root, project)).Descendants(XName.Get "None")
                |> Seq.filter (fun item ->
                    match item.Attribute(XName.Get "Include") with
                    | null -> false
                    | includePath -> includePath.Value.Contains("ConformanceFixtures", StringComparison.Ordinal))
                |> Seq.toArray

            Assert.NotEmpty(items)

            Assert.All(
                items,
                fun item ->
                    match item.Attribute(XName.Get "Pack") with
                    | null -> failwith "Conformance assets must declare Pack=false."
                    | pack -> Assert.Equal("false", pack.Value)
            )

        let fixtureRoot = Path.Combine(root, "tests", "ConformanceFixtures")

        let first =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-scale-{Guid.NewGuid():N}")

        let second =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-scale-{Guid.NewGuid():N}")

        let small =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-small-fixtures-{Guid.NewGuid():N}")

        try
            Directory.CreateDirectory first |> ignore
            Directory.CreateDirectory second |> ignore
            Directory.CreateDirectory small |> ignore
            ConformanceFixture.generateSmall small

            ConformanceFixture.compareDirectories
                (Path.Combine(fixtureRoot, "Solutions"))
                (Path.Combine(small, "Solutions"))

            ConformanceFixture.compareDirectories
                (Path.Combine(fixtureRoot, "MSBuild"))
                (Path.Combine(small, "MSBuild"))

            let firstSolution = ConformanceFixture.generateScale first
            let secondSolution = ConformanceFixture.generateScale second

            let firstProjects =
                Directory.EnumerateFiles(Path.Combine(first, "src"), "*.csproj", SearchOption.AllDirectories)
                |> Seq.toArray

            Assert.Equal(
                500,
                File.ReadLines(firstSolution)
                |> Seq.filter (_.Contains("<Project Path="))
                |> Seq.length
            )

            Assert.Equal(500, firstProjects.Length)

            Assert.Equal(
                250000,
                firstProjects
                |> Seq.sumBy (fun project ->
                    File.ReadLines(project)
                    |> Seq.filter (_.Contains("<Compile Include="))
                    |> Seq.length)
            )

            ConformanceFixture.compareDirectories first second
        finally
            if Directory.Exists first then
                Directory.Delete(first, true)

            if Directory.Exists second then
                Directory.Delete(second, true)

            if Directory.Exists small then
                Directory.Delete(small, true)
