namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open FsUnit.Xunit
open Xunit

[<Collection("Solution contracts")>]
type SolutionFilterProjectionTests() =
    [<Fact>]
    member _.``an .slnf projection resolves its backing solution with excluded projects as read-only placeholders``
        ()
        =
        let workspace =
            SolutionScenario.openWorkspace (SolutionScenario.fixturePath "Filters/Canonical.slnf")

        let included =
            workspace.Contents.Projects
            |> Seq.find (fun project -> not project.IsFilteredOut)

        let excluded = workspace.Contents.Projects |> Seq.find _.IsFilteredOut
        (workspace.Descriptor.Format) |> should equal (WorkspaceFormat.Slnf)
        (workspace.Descriptor.IsReadOnly) |> should equal true
        (included.Node.LoadState) |> should equal (WorkspaceNodeLoadState.Unhydrated)
        (excluded.Node.Kind) |> should equal (WorkspaceNodeKind.Placeholder)
        (excluded.Node.LoadState) |> should equal (WorkspaceNodeLoadState.FilteredOut)

        (workspace.Contents.Nodes)
        |> Seq.iter (fun node -> (node.Supports WorkspaceCapabilityId.Write) |> should equal false)
