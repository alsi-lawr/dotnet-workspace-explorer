namespace Dotnet.WorkspaceExplorer.Workspaces

type WorkspaceFailure =
    | InvalidInput of inputName: string * diagnostic: WorkspaceDiagnostic
    | UnsupportedCapability of capability: WorkspaceCapabilityId * diagnostic: WorkspaceDiagnostic
    | NotFound of target: string * diagnostic: WorkspaceDiagnostic
    | AmbiguousTarget of target: string * diagnostic: WorkspaceDiagnostic
    | Conflict of
        expectedRevision: WorkspaceRevision *
        actualRevision: WorkspaceRevision *
        diagnostic: WorkspaceDiagnostic
    | Cancelled of operationId: WorkspaceOperationId * diagnostic: WorkspaceDiagnostic
    | ExternalToolFailed of toolName: string * exitCode: int * diagnostic: WorkspaceDiagnostic
    | PartialRecoveryRequired of recoveryAction: string * diagnostic: WorkspaceDiagnostic
    | Internal of diagnostic: WorkspaceDiagnostic

    member this.Code =
        match this with
        | InvalidInput _ -> WorkspaceErrorCode.InvalidInput
        | UnsupportedCapability _ -> WorkspaceErrorCode.UnsupportedCapability
        | NotFound _ -> WorkspaceErrorCode.NotFound
        | AmbiguousTarget _ -> WorkspaceErrorCode.AmbiguousTarget
        | Conflict _ -> WorkspaceErrorCode.WorkspaceConflict
        | Cancelled _ -> WorkspaceErrorCode.Cancelled
        | ExternalToolFailed _ -> WorkspaceErrorCode.ExternalToolFailed
        | PartialRecoveryRequired _ -> WorkspaceErrorCode.PartialRecoveryRequired
        | Internal _ -> WorkspaceErrorCode.InternalError

    member this.Diagnostic =
        match this with
        | InvalidInput(_, diagnostic)
        | UnsupportedCapability(_, diagnostic)
        | NotFound(_, diagnostic)
        | AmbiguousTarget(_, diagnostic)
        | Conflict(_, _, diagnostic)
        | Cancelled(_, diagnostic)
        | ExternalToolFailed(_, _, diagnostic)
        | PartialRecoveryRequired(_, diagnostic)
        | Internal diagnostic -> diagnostic

type WorkspaceOutcome<'value> =
    | Success of value: 'value
    | Failure of error: WorkspaceFailure
