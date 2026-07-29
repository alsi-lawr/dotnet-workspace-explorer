namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Xunit

[<Collection("Solution contracts")>]
type SolutionProjectionTests() =
    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``should project hierarchy dependencies and external paths for sln formats``
        (extension: string)
        =
        let path = SolutionScenario.fixturePath $"Canonical{extension}"
        let workspace = SolutionScenario.openWorkspace path
        let root = workspace.Contents
        let externalProject = root.Projects |> Seq.find _.Path.IsExternal
        let folder = Assert.Single root.Folders

        let included =
            root.Projects |> Seq.find (fun project -> project.Node.Name = "Included")

        Assert.Equal(
            (if extension = ".sln" then
                 WorkspaceFormat.Sln
             else
                 WorkspaceFormat.Slnx),
            workspace.Descriptor.Format
        )

        Assert.Equal(
            Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName path, "../external/External.csproj")
            ),
            externalProject.Path.AbsolutePath.Value
        )

        Assert.Equal(
            Path.Combine("..", "external", "External.csproj"),
            externalProject.Path.SolutionRelativePath
        )

        Assert.Equal("/src/", folder.Path)
        Assert.Equal(Some folder.Path, included.ParentFolderPath)
        Assert.Single root.Items |> ignore
        Assert.Equal(2, root.Projects.Length)
        Assert.Single root.Dependencies |> ignore
        Assert.Contains(root.BuildTypes, fun node -> node.Name = "Debug")
        Assert.Contains(root.Platforms, fun node -> node.Name = "Any CPU")
