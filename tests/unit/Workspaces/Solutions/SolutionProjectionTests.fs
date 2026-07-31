namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open FsUnit.Xunit
open Xunit

[<Collection("Solution contracts")>]
type SolutionProjectionTests() =
    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``canonical .sln and .slnx projections include hierarchy, dependencies, and external paths``
        (extension: string)
        =
        let path = SolutionScenario.fixturePath $"Canonical{extension}"
        let workspace = SolutionScenario.openWorkspace path
        let root = workspace.Contents
        let externalProject = root.Projects |> Seq.find _.Path.IsExternal
        let folder = (root.Folders) |> Seq.exactlyOne

        let included =
            root.Projects |> Seq.find (fun project -> project.Node.Name = "Included")

        let expectedFormat =
            if extension = ".sln" then
                WorkspaceFormat.Sln
            else
                WorkspaceFormat.Slnx

        workspace.Descriptor.Format |> should equal expectedFormat

        let expectedExternalPath =
            Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName path, "../external/External.csproj")
            )

        externalProject.Path.AbsolutePath.Value |> should equal expectedExternalPath

        externalProject.Path.SolutionRelativePath
        |> should equal (Path.Combine("..", "external", "External.csproj"))

        folder.Path |> should equal "/src/"
        included.ParentFolderPath |> should equal (Some folder.Path)
        root.Items |> should haveLength 1
        root.Projects.Length |> should equal 2
        root.Dependencies |> should haveLength 1

        root.BuildTypes
        |> Seq.exists (fun node -> node.Name = "Debug")
        |> should equal true

        root.Platforms
        |> Seq.exists (fun node -> node.Name = "Any CPU")
        |> should equal true
