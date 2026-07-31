namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Xunit

[<Collection("Core contracts")>]
type WorkspaceIdentityTests() =
    [<Fact>]
    member _.``should normalize only contract-equivalent workspace and node identity paths``() =
        let upper = WorkspacePath.Create "/tmp/dotnet-workspace-explorer/Demo.slnx"
        let lower = WorkspacePath.Create "/tmp/dotnet-workspace-explorer/demo.slnx"

        (WorkspaceId.Create(lower, FileSystemCaseSensitivity.Insensitive).Value)
        |> should equal (WorkspaceId.Create(upper, FileSystemCaseSensitivity.Insensitive).Value)

        (WorkspaceId.Create(lower, FileSystemCaseSensitivity.Sensitive).Value)
        |> should
            not'
            (equal (WorkspaceId.Create(upper, FileSystemCaseSensitivity.Sensitive).Value))

        let workspace = WorkspaceContractScenario.workspace WorkspaceFormat.Slnx

        let node identity =
            WorkspaceNode.Create(
                workspace,
                WorkspaceNodeKind.Project,
                WorkspaceNodeIdentity.Create identity,
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        ((node "src/Demo/Demo.csproj").Id)
        |> should equal ((node "src\\Demo\\Demo.csproj").Id)

        ((node "src/Demo/Renamed.csproj").Id)
        |> should not' (equal ((node "src/Demo/Demo.csproj").Id))
