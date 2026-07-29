namespace Dotnet.CLI.Plus.Tests

open Dotnet.CLI.Plus.Core
open Xunit

module private CoreContract =
    let workspace format =
        WorkspaceDescriptor.Create(
            WorkspaceTargetPath.Create "/tmp/dotnet-cli-plus/Demo.slnx",
            HostFileSystemCaseSemantics.Sensitive,
            format,
            WorkspaceRevision.Create 1L,
            WorkspaceAccess.ReadWrite
        )

    let diagnostic () =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create "workspace.test",
            "Safe test diagnostic.",
            false,
            CorrelationId.New()
        )

type CoreContractTests() =
    [<Fact>]
    member _.``should normalize only contract-equivalent workspace and node identity paths``() =
        let upper = WorkspaceTargetPath.Create "/tmp/dotnet-cli-plus/Demo.slnx"
        let lower = WorkspaceTargetPath.Create "/tmp/dotnet-cli-plus/demo.slnx"

        Assert.Equal(
            WorkspaceId.Create(upper, HostFileSystemCaseSemantics.Insensitive).Value,
            WorkspaceId.Create(lower, HostFileSystemCaseSemantics.Insensitive).Value
        )

        Assert.NotEqual<string>(
            WorkspaceId.Create(upper, HostFileSystemCaseSemantics.Sensitive).Value,
            WorkspaceId.Create(lower, HostFileSystemCaseSemantics.Sensitive).Value
        )

        let workspace = CoreContract.workspace WorkspaceFormat.Slnx

        let node identity =
            WorkspaceNode.Create(
                workspace,
                WorkspaceNodeKind.Project,
                NodeSemanticIdentity.Create identity,
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        Assert.Equal((node "src\\Demo\\Demo.csproj").NodeId, (node "src/Demo/Demo.csproj").NodeId)

        Assert.NotEqual(
            (node "src/Demo/Demo.csproj").NodeId,
            (node "src/Demo/Renamed.csproj").NodeId
        )

    [<Fact>]
    member _.``should not advertise mutation capabilities for read-only or unknown systems``() =
        let filtered = CoreContract.workspace WorkspaceFormat.Slnf

        let filteredProject =
            WorkspaceNode.Create(
                filtered,
                WorkspaceNodeKind.Project,
                NodeSemanticIdentity.Create "Demo.csproj",
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        let unknownProject =
            WorkspaceNode.Create(
                CoreContract.workspace WorkspaceFormat.Slnx,
                WorkspaceNodeKind.Project,
                NodeSemanticIdentity.Create "Unknown.proj",
                "Unknown",
                WorkspaceCapabilityProfile.UnknownProjectSystem
            )

        Assert.True filtered.IsReadOnly
        Assert.False(filteredProject.Supports WorkspaceCapabilityId.Write)
        Assert.True(unknownProject.Supports WorkspaceCapabilityId.Read)
        Assert.False(unknownProject.Supports WorkspaceCapabilityId.Write)

    [<Fact>]
    member _.``should preserve both revisions and the stable failure code for conflicts``() =
        let expected = WorkspaceRevision.Create 5L
        let actual = WorkspaceRevision.Create 6L

        match WorkspaceRevisionPrecondition.Check(expected, actual, CoreContract.diagnostic ()) with
        | Failure(Conflict(conflictExpected, conflictActual, diagnostic)) ->
            Assert.Equal(expected, conflictExpected)
            Assert.Equal(actual, conflictActual)
            Assert.Equal("workspace_conflict", WorkspaceErrorCode.WorkspaceConflict.Value)
            Assert.Equal("workspace.test", diagnostic.DiagnosticCode.Value)
        | outcome -> failwithf "Expected a typed conflict, got %A" outcome
