namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.CommandLine
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type ProjectCapabilityTests() =
    [<Fact>]
    member _.``should project dimensions and invalidate imports and globs in the real worker``() =
        let directory = Test.temporaryDirectory "projection"

        try
            let projection = Test.copyFixture directory "MultiTargetProject"
            let generatedDirectory = Path.Combine(projection, "Generated")
            let props = Path.Combine(projection, "Directory.Build.props")
            let project = Path.Combine(projection, "Project.csproj")

            Test.withWorker directory (fun worker ->
                let error, snapshot = Test.evaluate worker 2u project
                Assert.True error.IsNone
                let dimensions = Test.values "dimensions" snapshot
                Assert.Equal(3, dimensions.Length)

                let dimension framework =
                    dimensions
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String framework)

                let includes value =
                    Test.values "items" value |> Seq.map (Test.stringField "include")

                Assert.Contains("Eight.cs", dimension "net8.0" |> includes)
                Assert.DoesNotContain("Eight.cs", dimension "net9.0" |> includes)
                Assert.Contains(props, Test.strings "imports" snapshot)
                Assert.Contains(generatedDirectory, Test.strings "globRoots" snapshot)

                let importedProperties =
                    dimensions
                    |> Seq.collect (Test.values "properties")
                    |> Seq.filter (fun value -> Test.stringField "name" value = "ImportedProperty")
                    |> Seq.map (Test.stringField "value")

                Assert.Contains("before", importedProperties)

                let generated = Path.Combine(generatedDirectory, "New.cs")
                Test.write generated "class New {}"

                let globInvalidation = Test.invalidate worker 3u [ generated ]
                Assert.Contains(project, Test.strings "invalidatedProjects" globInvalidation)

                let _, withGenerated = Test.evaluate worker 4u project

                Assert.Contains(
                    withGenerated
                    |> Test.values "dimensions"
                    |> Seq.collect (Test.values "items")
                    |> Seq.map (Test.stringField "include"),
                    fun itemInclude ->
                        itemInclude
                            .Replace('\\', '/')
                            .EndsWith("Generated/New.cs", StringComparison.Ordinal)
                )

                Test.write
                    props
                    ("<Project><PropertyGroup>"
                     + "<ImportedProperty>after</ImportedProperty>"
                     + "</PropertyGroup></Project>")

                let importInvalidation = Test.invalidate worker 5u [ props ]
                Assert.Contains(project, Test.strings "invalidatedProjects" importInvalidation)
                let _, changed = Test.evaluate worker 6u project

                Assert.Contains(
                    changed |> Test.values "dimensions" |> Seq.collect (Test.values "properties"),
                    fun value ->
                        Test.stringField "name" value = "ImportedProperty"
                        && Test.stringField "value" value = "after"
                )

                7u)
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``should make managed projects writable and unknown projects read only``() =
        let directory = Test.temporaryDirectory "capabilities"

        try
            let unknown = Path.Combine(directory, "Unknown.proj")
            File.Copy(Test.fixturePath "UnsupportedProject/Unknown.proj", unknown)

            let projects =
                [ Test.simpleProject directory "CSharp" ".csproj", "Full", true
                  unknown, "UnknownProjectSystem", false ]

            Test.withWorker directory (fun worker ->
                for index, (project, expectedProfile, expectedWrite) in Seq.indexed projects do
                    let error, snapshot = Test.evaluate worker (uint32 index + 2u) project
                    Assert.True error.IsNone
                    Assert.Equal(expectedProfile, Test.stringField "capabilityProfile" snapshot)
                    let capabilities = Test.strings "capabilities" snapshot |> Seq.toArray
                    Assert.Contains("workspace.read", capabilities)
                    Assert.Equal(expectedWrite, capabilities |> Array.contains "workspace.write")

                4u)
        finally
            Directory.Delete(directory, true)
