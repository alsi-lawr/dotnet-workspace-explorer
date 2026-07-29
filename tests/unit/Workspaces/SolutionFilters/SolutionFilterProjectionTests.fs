namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

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
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open FsUnit.Xunit
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
