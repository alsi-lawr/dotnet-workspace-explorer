namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces

module private WorkspaceContractScenario =
    let workspace format =
        WorkspaceDescriptor.Create(
            WorkspacePath.Create "/tmp/dotnet-workspace-explorer/Demo.slnx",
            FileSystemCaseSensitivity.Sensitive,
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
