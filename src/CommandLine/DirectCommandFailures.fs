namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3261"
#nowarn "3511"


module internal DirectCommandFailures =
    let diagnostic code message retryable =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            retryable,
            CorrelationId.New()
        )

    let invalid message =
        InvalidInput("arguments", diagnostic WorkspaceErrorCode.InvalidInput.Value message false)

    let unsupported message =
        UnsupportedCapability(
            WorkspaceCapabilityId.Write,
            diagnostic WorkspaceErrorCode.UnsupportedCapability.Value message false
        )

    let verification message =
        Internal(diagnostic WorkspaceErrorCode.InternalError.Value message false)

    let cancelled () =
        Cancelled(
            WorkspaceOperationId.New(),
            diagnostic WorkspaceErrorCode.Cancelled.Value "The dotnet command was cancelled." true
        )

    let internalFailure message =
        Internal(diagnostic WorkspaceErrorCode.InternalError.Value message false)
