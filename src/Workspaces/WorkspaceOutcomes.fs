namespace Dotnet.WorkspaceExplorer.Workspaces

open System

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

    member this.Match
        (
            onInvalidInput: Func<string, WorkspaceDiagnostic, 'result>,
            onUnsupportedCapability: Func<WorkspaceCapabilityId, WorkspaceDiagnostic, 'result>,
            onNotFound: Func<string, WorkspaceDiagnostic, 'result>,
            onAmbiguousTarget: Func<string, WorkspaceDiagnostic, 'result>,
            onConflict: Func<WorkspaceRevision, WorkspaceRevision, WorkspaceDiagnostic, 'result>,
            onCancelled: Func<WorkspaceOperationId, WorkspaceDiagnostic, 'result>,
            onExternalToolFailed: Func<string, int, WorkspaceDiagnostic, 'result>,
            onPartialRecoveryRequired: Func<string, WorkspaceDiagnostic, 'result>,
            onInternal: Func<WorkspaceDiagnostic, 'result>
        ) =
        match this with
        | InvalidInput(inputName, diagnostic) -> onInvalidInput.Invoke(inputName, diagnostic)
        | UnsupportedCapability(capability, diagnostic) ->
            onUnsupportedCapability.Invoke(capability, diagnostic)
        | NotFound(target, diagnostic) -> onNotFound.Invoke(target, diagnostic)
        | AmbiguousTarget(target, diagnostic) -> onAmbiguousTarget.Invoke(target, diagnostic)
        | Conflict(expectedRevision, actualRevision, diagnostic) ->
            onConflict.Invoke(expectedRevision, actualRevision, diagnostic)
        | Cancelled(operationId, diagnostic) -> onCancelled.Invoke(operationId, diagnostic)
        | ExternalToolFailed(toolName, exitCode, diagnostic) ->
            onExternalToolFailed.Invoke(toolName, exitCode, diagnostic)
        | PartialRecoveryRequired(recoveryAction, diagnostic) ->
            onPartialRecoveryRequired.Invoke(recoveryAction, diagnostic)
        | Internal diagnostic -> onInternal.Invoke diagnostic

type WorkspaceOutcome<'value> =
    | Success of value: 'value
    | Failure of error: WorkspaceFailure

    member this.Match
        (
            onSuccess: Func<'value, 'result>,
            onInvalidInput: Func<string, WorkspaceDiagnostic, 'result>,
            onUnsupportedCapability: Func<WorkspaceCapabilityId, WorkspaceDiagnostic, 'result>,
            onNotFound: Func<string, WorkspaceDiagnostic, 'result>,
            onAmbiguousTarget: Func<string, WorkspaceDiagnostic, 'result>,
            onConflict: Func<WorkspaceRevision, WorkspaceRevision, WorkspaceDiagnostic, 'result>,
            onCancelled: Func<WorkspaceOperationId, WorkspaceDiagnostic, 'result>,
            onExternalToolFailed: Func<string, int, WorkspaceDiagnostic, 'result>,
            onPartialRecoveryRequired: Func<string, WorkspaceDiagnostic, 'result>,
            onInternal: Func<WorkspaceDiagnostic, 'result>
        ) =
        match this with
        | Success value -> onSuccess.Invoke value
        | Failure error ->
            error.Match(
                onInvalidInput,
                onUnsupportedCapability,
                onNotFound,
                onAmbiguousTarget,
                onConflict,
                onCancelled,
                onExternalToolFailed,
                onPartialRecoveryRequired,
                onInternal
            )

[<AbstractClass; Sealed>]
type WorkspaceRevisionPrecondition private () =
    static member Check
        (
            expectedRevision: WorkspaceRevision,
            actualRevision: WorkspaceRevision,
            conflictDiagnostic: WorkspaceDiagnostic
        ) =
        if isNull (box expectedRevision) then
            nullArg (nameof expectedRevision)

        if isNull (box actualRevision) then
            nullArg (nameof actualRevision)

        if isNull (box conflictDiagnostic) then
            nullArg (nameof conflictDiagnostic)

        if expectedRevision.Value = actualRevision.Value then
            Success actualRevision
        else
            Failure(Conflict(expectedRevision, actualRevision, conflictDiagnostic))
