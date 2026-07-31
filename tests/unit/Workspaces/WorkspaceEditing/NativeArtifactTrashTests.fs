namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open FsUnit.Xunit
open Xunit

[<Collection("Workspace edits")>]
type NativeArtifactTrashTests() =
    [<Fact>]
    member _.``should select the native trash backend for the current host``() =
        let selected = NativeArtifactTrash.CreateForCurrentUser()

        if OperatingSystem.IsWindows() then
            (selected.GetType().Name) |> should equal ("WindowsArtifactTrash")
        elif OperatingSystem.IsMacOS() then
            (selected.GetType().Name) |> should equal ("MacArtifactTrash")
        else
            (selected.GetType().Name) |> should equal ("FreedesktopArtifactTrash")
