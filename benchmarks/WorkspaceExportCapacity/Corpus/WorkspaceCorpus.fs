namespace Dotnet.WorkspaceExplorer.WorkspaceExportCapacity

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc

module internal WorkspaceCorpus =
    let write root projects itemsPerProject =
        Directory.CreateDirectory root |> ignore

        File.Copy(
            Path.Combine(Arguments.repositoryRoot, "global.json"),
            Path.Combine(root, "global.json")
        )

        let solution = Path.Combine(root, "Capacity.slnx")
        use solutionWriter = new StreamWriter(solution, false, UTF8Encoding false)
        solutionWriter.WriteLine "<Solution>"
        solutionWriter.WriteLine "    <Folder Name=\"/src/\">"

        for projectNumber in 1..projects do
            let name = $"P{projectNumber:D4}"
            let relativeProject = $"src/{name}/{name}.csproj"
            solutionWriter.WriteLine $"        <Project Path=\"{relativeProject}\" Type=\"C#\" />"
            let projectDirectory = Path.Combine(root, "src", name)
            let itemsDirectory = Path.Combine(projectDirectory, "Items")
            Directory.CreateDirectory itemsDirectory |> ignore
            let projectPath = Path.Combine(projectDirectory, $"{name}.csproj")
            use projectWriter = new StreamWriter(projectPath, false, UTF8Encoding false)
            projectWriter.WriteLine "<Project Sdk=\"Microsoft.NET.Sdk\">"
            projectWriter.WriteLine "  <PropertyGroup>"
            projectWriter.WriteLine "    <TargetFramework>net10.0</TargetFramework>"

            projectWriter.WriteLine
                "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>"

            projectWriter.WriteLine "  </PropertyGroup>"
            projectWriter.WriteLine "  <ItemGroup>"

            for itemNumber in 1..itemsPerProject do
                let item = $"N{itemNumber:D4}.cs"
                projectWriter.WriteLine $"    <Compile Include=\"Items/{item}\" />"

                File.WriteAllText(
                    Path.Combine(itemsDirectory, item),
                    $"namespace {name}; class N{itemNumber:D4} {{}}"
                )

            projectWriter.WriteLine "  </ItemGroup>"
            projectWriter.WriteLine "</Project>"

        solutionWriter.WriteLine "    </Folder>"
        solutionWriter.WriteLine "</Solution>"
        solution
