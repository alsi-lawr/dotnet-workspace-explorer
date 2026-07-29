namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Xml.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
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

[<Collection("Project-folder scenarios")>]
type ProjectFolderDeclarationOwnershipTests() =
    [<Fact>]
    member _.``should refuse an affected direct macro folder declaration``() =
        let contents =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Content Include=\"$(MSBuildThisFileDirectory)Old/Source.cs\" /></ItemGroup></Project>"

        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "direct-macro-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    Directory.CreateDirectory old |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.cs"), "source"))
                contents

        try
            let old = Path.Combine(session.Directory, "Old")

            WorkspaceRpcScenario.previewFailure
                session
                3u
                "project.folder.rename"
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            Directory.Exists old |> should equal true
            File.ReadAllText session.Project |> should equal contents
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should refuse an affected imported macro folder declaration``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "imported-macro-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    Directory.CreateDirectory old |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.cs"), "source")

                    File.WriteAllText(
                        Path.Combine(directory, "Shared.props"),
                        "<Project><ItemGroup><Content Include=\"$(MSBuildThisFileDirectory)Old/Source.cs\" /></ItemGroup></Project>"
                    ))
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Shared.props\" /></Project>"

        try
            let old = Path.Combine(session.Directory, "Old")

            WorkspaceRpcScenario.previewFailure
                session
                3u
                "project.folder.rename"
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            Directory.Exists old |> should equal true
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should refuse an imported macro path token owned by a project folder``() =
        let imported =
            "<Project><ItemGroup><Content Include=\"Old/$(File)\" /></ItemGroup></Project>"

        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "imported-macro-path-token-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    Directory.CreateDirectory old |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.cs"), "source")
                    File.WriteAllText(Path.Combine(directory, "Shared.props"), imported))
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Shared.props\" /></Project>"

        try
            let old = Path.Combine(session.Directory, "Old")
            let importedPath = Path.Combine(session.Directory, "Shared.props")

            WorkspaceRpcScenario.previewFailure
                session
                3u
                "project.folder.rename"
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            Directory.Exists old |> should equal true
            Directory.Exists(Path.Combine(session.Directory, "New")) |> should equal false
            File.ReadAllText importedPath |> should equal imported
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should refuse a root-relative literal declaration in a nested import``() =
        let imported =
            "<Project><ItemGroup><Content Include=\"Old/Source.txt\" /></ItemGroup></Project>"

        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "nested-imported-literal-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    let build = Path.Combine(directory, "build")
                    Directory.CreateDirectory old |> ignore
                    Directory.CreateDirectory build |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.txt"), "source")
                    File.WriteAllText(Path.Combine(build, "Shared.props"), imported))
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Shared.props\" /></Project>"

        try
            let old = Path.Combine(session.Directory, "Old")
            let importedPath = Path.Combine(session.Directory, "build", "Shared.props")

            WorkspaceRpcScenario.previewFailure
                session
                3u
                "project.folder.rename"
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            File.Exists(Path.Combine(old, "Source.txt")) |> should equal true
            Directory.Exists(Path.Combine(session.Directory, "New")) |> should equal false
            File.ReadAllText importedPath |> should equal imported
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should ignore an unrelated macro folder declaration when renaming``() =
        let contents =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<Content Include=\"$(MSBuildThisFileDirectory)Other/Old/Unrelated.cs\" />"
            + "<Content Include=\"Old/Source.cs\" /></ItemGroup></Project>"

        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "unrelated-macro-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    let unrelated = Path.Combine(directory, "Other", "Old")
                    Directory.CreateDirectory old |> ignore
                    Directory.CreateDirectory unrelated |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.cs"), "source")
                    File.WriteAllText(Path.Combine(unrelated, "Unrelated.cs"), "unrelated"))
                contents

        try
            let old = Path.Combine(session.Directory, "Old")
            let renamed = Path.Combine(session.Directory, "New")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.rename"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L
                true

            File.Exists(Path.Combine(renamed, "Source.cs")) |> should equal true

            File.Exists(Path.Combine(session.Directory, "Other", "Old", "Unrelated.cs"))
            |> should equal true

            let project = File.ReadAllText session.Project

            Assert.Contains(
                "Include=\"$(MSBuildThisFileDirectory)Other/Old/Unrelated.cs\"",
                project
            )

            Assert.Contains("Include=\"New/Source.cs\"", project)

            let names = WorkspaceRpcScenario.readAllProjectChildNames session 5u 1L

            names
            |> Array.exists (fun name ->
                name.StartsWith("Content: New/Source.cs", StringComparison.Ordinal))
            |> should equal true

            names
            |> Array.exists (fun name -> name.Contains(": Old/Source.cs", StringComparison.Ordinal))
            |> should equal false
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should refuse an affected multi-value folder declaration``() =
        let contents =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Content Include=\"Old/A.cs;Old/B.cs\" /></ItemGroup></Project>"

        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "multi-value-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    Directory.CreateDirectory old |> ignore
                    File.WriteAllText(Path.Combine(old, "A.cs"), "a")
                    File.WriteAllText(Path.Combine(old, "B.cs"), "b"))
                contents

        try
            let old = Path.Combine(session.Directory, "Old")

            WorkspaceRpcScenario.previewFailure
                session
                3u
                "project.folder.rename"
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            File.Exists(Path.Combine(old, "A.cs")) |> should equal true
            File.Exists(Path.Combine(old, "B.cs")) |> should equal true
            File.ReadAllText session.Project |> should equal contents
        finally
            WorkspaceRpcScenario.closeProject session
