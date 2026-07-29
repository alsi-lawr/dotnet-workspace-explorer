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
type WorkspaceIdentityTests() =
    [<Fact>]
    member _.``should normalize only contract-equivalent workspace and node identity paths``() =
        let upper = WorkspacePath.Create "/tmp/dotnet-workspace-explorer/Demo.slnx"
        let lower = WorkspacePath.Create "/tmp/dotnet-workspace-explorer/demo.slnx"

        Assert.Equal(
            WorkspaceId.Create(upper, FileSystemCaseSensitivity.Insensitive).Value,
            WorkspaceId.Create(lower, FileSystemCaseSensitivity.Insensitive).Value
        )

        Assert.NotEqual<string>(
            WorkspaceId.Create(upper, FileSystemCaseSensitivity.Sensitive).Value,
            WorkspaceId.Create(lower, FileSystemCaseSensitivity.Sensitive).Value
        )

        let workspace = WorkspaceContractScenario.workspace WorkspaceFormat.Slnx

        let node identity =
            WorkspaceNode.Create(
                workspace,
                WorkspaceNodeKind.Project,
                WorkspaceNodeIdentity.Create identity,
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        Assert.Equal((node "src\\Demo\\Demo.csproj").Id, (node "src/Demo/Demo.csproj").Id)

        Assert.NotEqual((node "src/Demo/Demo.csproj").Id, (node "src/Demo/Renamed.csproj").Id)
