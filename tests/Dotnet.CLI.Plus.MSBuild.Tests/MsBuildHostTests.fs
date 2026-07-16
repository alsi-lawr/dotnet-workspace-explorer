namespace Dotnet.CLI.Plus.MSBuild.Tests

open System
open System.IO
open System.Threading
open Dotnet.CLI.Plus.MSBuild
open Xunit

module private Fixture =
    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-msbuild-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let apphost () =
        let rec root (directory: string) =
            if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
                directory
            else
                match Directory.GetParent(directory) with
                | null -> failwith "Could not locate the repository root."
                | parent -> root parent.FullName

        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.CLI.Plus.exe"
            else
                "Dotnet.CLI.Plus"

        let repository = root AppContext.BaseDirectory
        let baseDirectory = DirectoryInfo(AppContext.BaseDirectory)

        let configuration =
            match baseDirectory.Parent with
            | null -> failwith "Could not determine the build configuration."
            | parent -> parent.Name

        Path.Combine(repository, "src", "Dotnet.CLI.Plus", "bin", configuration, "net10.0", name)

    let write (path: string) (contents: string) = File.WriteAllText(path, contents)

type MsBuildHostTests() =
    [<Fact>]
    member _.``isolated host evaluates imported conditional project data without running targets``() =
        let directory = Fixture.temporaryDirectory ()

        try
            Fixture.write
                (Path.Combine(directory, "Directory.Build.props"))
                """
<Project><PropertyGroup><ImportedProperty>imported</ImportedProperty></PropertyGroup></Project>
"""

            Fixture.write
                (Path.Combine(directory, "Directory.Packages.props"))
                """
<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include="Example.Package" Version="2.0.0" /></ItemGroup></Project>
"""

            let project = Path.Combine(directory, "Demo.csproj")

            Fixture.write
                project
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks><MarkerRan>false</MarkerRan></PropertyGroup>
  <ItemGroup><Compile Include="Linked.cs"><Link>Virtual/Linked.cs</Link></Compile><PackageReference Include="Example.Package" /></ItemGroup>
  <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'"><ConditionalProperty>eight</ConditionalProperty></PropertyGroup>
  <Target Name="Marker"><Error Text="targets must not run" /></Target>
</Project>
"""

            Fixture.write (Path.Combine(directory, "Linked.cs")) "class Linked {}"
            let client = new MsBuildEvaluationClient(Fixture.apphost ())
            let outcome = client.EvaluateAsync(project, directory).GetAwaiter().GetResult()
            client.DisposeAsync().AsTask().GetAwaiter().GetResult()

            match outcome with
            | :? EvaluationOutcome.Success as success ->
                Assert.Equal<string array>([| "net8.0"; "net9.0" |], success.Snapshot.TargetFrameworks |> Seq.toArray)

                Assert.Contains(
                    success.Snapshot.Properties,
                    fun property -> property.Name = "ImportedProperty" && property.Value = "imported"
                )

                Assert.Contains(
                    success.Snapshot.Properties,
                    fun property -> property.Name = "ConditionalProperty" && property.Value = "eight"
                )

                Assert.Contains(
                    success.Snapshot.Items,
                    fun item ->
                        item.Metadata
                        |> Seq.exists (fun metadata -> metadata.Name = "Link" && metadata.Value = "Virtual/Linked.cs")
                )

                Assert.Contains(
                    success.Snapshot.Packages,
                    fun package -> package.Id = "Example.Package" && package.Version = "2.0.0"
                )

                Assert.Contains(
                    success.Snapshot.Diagnostics,
                    fun diagnostic -> diagnostic.Code = "msbuild.assets_missing"
                )
            | :? EvaluationOutcome.Failure as failure -> failwithf "Evaluation failed: %s" failure.Message
            | _ -> failwith "Unexpected evaluation outcome."
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``discovery cancellation is a typed outcome and main process avoids MSBuild assemblies``() =
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let outcome =
            DotnetSdkDiscovery
                .DiscoverAsync(Directory.GetCurrentDirectory(), cancellation.Token)
                .GetAwaiter()
                .GetResult()

        Assert.IsType<ToolsetDiscoveryOutcome.Failure>(outcome) |> ignore

        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            fun assembly ->
                match assembly.GetName().Name with
                | null -> false
                | name -> name.StartsWith("Microsoft.Build", StringComparison.Ordinal)
        )
