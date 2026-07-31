namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces
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

        (filtered.IsReadOnly) |> should equal true
        (filteredProject.Supports WorkspaceCapabilityId.Write) |> should equal false
        (unknownProject.Supports WorkspaceCapabilityId.Read) |> should equal true
        (unknownProject.Supports WorkspaceCapabilityId.Write) |> should equal false
