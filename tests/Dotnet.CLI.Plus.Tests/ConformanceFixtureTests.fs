namespace Dotnet.CLI.Plus.Tests

open System
open System.IO
open System.Text
open System.Xml.Linq
open Xunit

module private ConformanceFixture =
    let private utf8 = UTF8Encoding(false)

    let private write path contents =
        use writer = new StreamWriter(path, false, utf8)
        contents writer

    let generateScale directory =
        let solution = Path.Combine(directory, "Scale.slnx")
        let nodes = Path.Combine(directory, "ExplorerNodes.tsv")

        write solution (fun writer ->
            writer.WriteLine("<Solution>")

            for project in 1..500 do
                writer.WriteLine($"  <Project Path=\"src/P{project:D4}/P{project:D4}.csproj\" />")

            writer.WriteLine("</Solution>"))

        write nodes (fun writer ->
            for project in 1..500 do
                for node in 1..500 do
                    writer.WriteLine($"P{project:D4}\tN{node:D4}\tprojectItem"))

        solution, nodes

    let rec repositoryRoot directory =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            directory
        else
            match Directory.GetParent directory with
            | null -> failwith "Could not locate the repository root."
            | parent -> repositoryRoot parent.FullName

type ConformanceFixtureTests() =
    [<Fact>]
    member _.``scale fixture regeneration is exact and conformance assets are explicitly non-packable``() =
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

        let first =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-scale-{Guid.NewGuid():N}")

        let second =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-scale-{Guid.NewGuid():N}")

        try
            Directory.CreateDirectory first |> ignore
            Directory.CreateDirectory second |> ignore
            let firstSolution, firstNodes = ConformanceFixture.generateScale first
            let secondSolution, secondNodes = ConformanceFixture.generateScale second

            Assert.Equal(
                500,
                File.ReadLines(firstSolution)
                |> Seq.filter (_.Contains("<Project Path="))
                |> Seq.length
            )

            Assert.Equal(250000, File.ReadLines(firstNodes) |> Seq.length)
            Assert.Equal<byte>(File.ReadAllBytes(firstSolution), File.ReadAllBytes(secondSolution))
            Assert.Equal<byte>(File.ReadAllBytes(firstNodes), File.ReadAllBytes(secondNodes))
        finally
            if Directory.Exists first then
                Directory.Delete(first, true)

            if Directory.Exists second then
                Directory.Delete(second, true)
