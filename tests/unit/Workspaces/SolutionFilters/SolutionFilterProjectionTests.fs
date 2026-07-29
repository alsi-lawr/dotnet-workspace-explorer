namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Xunit

[<Collection("Solution contracts")>]
type SolutionFilterProjectionTests() =
    [<Fact>]
    member _.``should resolve slnf against its backing solution with excluded read-only placeholders``
        ()
        =
        let workspace =
            SolutionScenario.openWorkspace (SolutionScenario.fixturePath "Filters/Canonical.slnf")

        let included =
            workspace.Contents.Projects
            |> Seq.find (fun project -> not project.IsFilteredOut)

        let excluded = workspace.Contents.Projects |> Seq.find _.IsFilteredOut
        Assert.Equal(WorkspaceFormat.Slnf, workspace.Descriptor.Format)
        Assert.True workspace.Descriptor.IsReadOnly
        Assert.Equal(WorkspaceNodeLoadState.Unhydrated, included.Node.LoadState)
        Assert.Equal(WorkspaceNodeKind.Placeholder, excluded.Node.Kind)
        Assert.Equal(WorkspaceNodeLoadState.FilteredOut, excluded.Node.LoadState)

        Assert.All(
            workspace.Contents.Nodes,
            fun node -> Assert.False(node.Supports WorkspaceCapabilityId.Write)
        )
