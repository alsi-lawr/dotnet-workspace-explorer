namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces
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
