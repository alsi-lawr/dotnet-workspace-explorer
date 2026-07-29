namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Xunit

[<Collection("Workspace edits")>]
type NativeArtifactTrashTests() =
    [<Fact>]
    member _.``should select the native trash backend for the current host``() =
        let selected = NativeArtifactTrash.CreateForCurrentUser()

        if OperatingSystem.IsWindows() then
            Assert.Equal("WindowsArtifactTrash", selected.GetType().Name)
        elif OperatingSystem.IsMacOS() then
            Assert.Equal("MacArtifactTrash", selected.GetType().Name)
        else
            Assert.Equal("FreedesktopArtifactTrash", selected.GetType().Name)
