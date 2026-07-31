namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type ProjectCapabilityTests() =
    [<Fact>]
    member _.``evaluating explicit F# compile order preserves source ordinals despite lexical paths``
        ()
        =
        let directory = Test.temporaryDirectory "fsharp-item-order"

        try
            let project = Path.Combine(directory, "Ordered.fsproj")

            for relative in [ "A/First.fs"; "B/Second.fs"; "A/Third.fs" ] do
                let path = Path.Combine(directory, relative)
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, $"module {Path.GetFileNameWithoutExtension path}")

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                + "</PropertyGroup><ItemGroup>"
                + "<Compile Include=\"A/First.fs\" />"
                + "<Compile Include=\"B/Second.fs\" />"
                + "<Compile Include=\"A/Third.fs\" />"
                + "</ItemGroup></Project>"
            )

            Test.withWorker directory (fun worker ->
                let error, snapshot = Test.evaluate worker 2u project
                (error.IsNone) |> should equal true

                let dimension =
                    Test.values "dimensions" snapshot
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String "net10.0")

                let ordered =
                    Test.values "items" dimension
                    |> Seq.filter (fun value -> Test.stringField "itemType" value = "Compile")
                    |> Seq.map (fun value ->
                        Test.stringField "include" value,
                        Test.field "ordinal" value |> RpcValue.requireInteger "ordinal")
                    |> Seq.toArray

                (ordered |> Array.map fst)
                |> should equal ([| "A/First.fs"; "B/Second.fs"; "A/Third.fs" |])

                (ordered
                 |> Array.map snd
                 |> Array.pairwise
                 |> Array.forall (fun (left, right) -> left < right))
                |> should equal true

                3u)
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``evaluating a multi-target project reports dimensions and invalidates imports and globs after file changes``
        ()
        =
        let directory = Test.temporaryDirectory "projection"

        try
            let projection = Test.copyFixture directory "MultiTargetProject"
            let generatedDirectory = Path.Combine(projection, "Generated")
            let props = Path.Combine(projection, "Directory.Build.props")
            let project = Path.Combine(projection, "Project.csproj")

            Test.withWorker directory (fun worker ->
                let error, snapshot = Test.evaluate worker 2u project
                (error.IsNone) |> should equal true
                let dimensions = Test.values "dimensions" snapshot
                (dimensions.Length) |> should equal (3)

                let dimension framework =
                    dimensions
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String framework)

                let includes value =
                    Test.values "items" value |> Seq.map (Test.stringField "include")

                (dimension "net8.0" |> includes) |> should contain ("Eight.cs")
                (dimension "net9.0" |> includes) |> should not' (contain ("Eight.cs"))
                (Test.strings "imports" snapshot) |> should contain (props)
                (Test.strings "globRoots" snapshot) |> should contain (generatedDirectory)

                let importedProperties =
                    dimensions
                    |> Seq.collect (Test.values "properties")
                    |> Seq.filter (fun value -> Test.stringField "name" value = "ImportedProperty")
                    |> Seq.map (Test.stringField "value")

                (importedProperties) |> should contain ("before")

                let generated = Path.Combine(generatedDirectory, "New.cs")
                Test.write generated "class New {}"

                let globInvalidation = Test.invalidate worker 3u [ generated ]

                (Test.strings "invalidatedProjects" globInvalidation)
                |> should contain (project)

                let _, withGenerated = Test.evaluate worker 4u project

                (withGenerated
                 |> Test.values "dimensions"
                 |> Seq.collect (Test.values "items")
                 |> Seq.map (Test.stringField "include"))
                |> Seq.exists (fun itemInclude ->
                    itemInclude
                        .Replace('\\', '/')
                        .EndsWith("Generated/New.cs", StringComparison.Ordinal))
                |> should equal true

                Test.write
                    props
                    ("<Project><PropertyGroup>"
                     + "<ImportedProperty>after</ImportedProperty>"
                     + "</PropertyGroup></Project>")

                let importInvalidation = Test.invalidate worker 5u [ props ]

                (Test.strings "invalidatedProjects" importInvalidation)
                |> should contain (project)

                let _, changed = Test.evaluate worker 6u project

                (changed |> Test.values "dimensions" |> Seq.collect (Test.values "properties"))
                |> Seq.exists (fun value ->
                    Test.stringField "name" value = "ImportedProperty"
                    && Test.stringField "value" value = "after")
                |> should equal true

                7u)
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``evaluating managed and unknown projects assigns writable and read-only capability profiles``
        ()
        =
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
                    (error.IsNone) |> should equal true

                    (Test.stringField "capabilityProfile" snapshot)
                    |> should equal (expectedProfile)

                    let capabilities = Test.strings "capabilities" snapshot |> Seq.toArray
                    (capabilities) |> should contain ("workspace.read")

                    (capabilities |> Array.contains "workspace.write")
                    |> should equal (expectedWrite)

                4u)
        finally
            Directory.Delete(directory, true)
