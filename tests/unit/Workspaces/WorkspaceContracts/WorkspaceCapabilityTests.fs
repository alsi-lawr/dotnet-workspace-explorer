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

[<Collection("Core contracts")>]
type WorkspaceCapabilityTests() =
    [<Fact>]
    member _.``should not advertise mutation capabilities for read-only or unknown systems``() =
        let filtered = WorkspaceContractScenario.workspace WorkspaceFormat.Slnf

        let filteredProject =
            WorkspaceNode.Create(
                filtered,
                WorkspaceNodeKind.Project,
                WorkspaceNodeIdentity.Create "Demo.csproj",
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        let unknownProject =
            WorkspaceNode.Create(
                WorkspaceContractScenario.workspace WorkspaceFormat.Slnx,
                WorkspaceNodeKind.Project,
                WorkspaceNodeIdentity.Create "Unknown.proj",
                "Unknown",
                WorkspaceCapabilityProfile.UnknownProjectSystem
            )

        Assert.True filtered.IsReadOnly
        Assert.False(filteredProject.Supports WorkspaceCapabilityId.Write)
        Assert.True(unknownProject.Supports WorkspaceCapabilityId.Read)
        Assert.False(unknownProject.Supports WorkspaceCapabilityId.Write)
