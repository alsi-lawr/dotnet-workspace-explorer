namespace Dotnet.WorkspaceExplorer.Workspaces

open System
open System.IO

type WorkspaceArtifactPath private (value: string) =
    member _.Value = value

    static member Create(path: string) =
        path
        |> WorkspaceValue.nonEmpty (nameof path)
        |> Path.GetFullPath
        |> WorkspaceArtifactPath

    override _.ToString() = value

type WorkspaceDiagnosticSeverity =
    | Information = 0
    | Warning = 1
    | Error = 2

type WorkspaceDiagnosticCode private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> WorkspaceValue.nonEmpty (nameof value) |> WorkspaceDiagnosticCode

    override _.ToString() = value

type CorrelationId private (value: Guid) =
    member _.Value = value
    static member New() = CorrelationId(Guid.NewGuid())
    override _.ToString() = value.ToString "N"

type WorkspaceDiagnostic =
    private
        { SeverityValue: WorkspaceDiagnosticSeverity
          CodeValue: WorkspaceDiagnosticCode
          MessageValue: string
          ArtifactPathValue: WorkspaceArtifactPath option
          RetryableValue: bool
          CorrelationIdValue: CorrelationId }

    member this.Severity = this.SeverityValue
    member this.Code = this.CodeValue
    member this.Message = this.MessageValue
    member this.ArtifactPath = this.ArtifactPathValue
    member this.Retryable = this.RetryableValue
    member this.CorrelationId = this.CorrelationIdValue

    static member Create
        (
            severity: WorkspaceDiagnosticSeverity,
            code: WorkspaceDiagnosticCode,
            safeMessage: string,
            artifactPath: WorkspaceArtifactPath option,
            isRetryable: bool,
            correlationId: CorrelationId
        ) =
        if isNull (box code) then
            nullArg (nameof code)

        if isNull (box correlationId) then
            nullArg (nameof correlationId)

        safeMessage |> WorkspaceValue.nonEmpty (nameof safeMessage) |> ignore

        { SeverityValue = severity
          CodeValue = code
          MessageValue = safeMessage
          ArtifactPathValue = artifactPath
          RetryableValue = isRetryable
          CorrelationIdValue = correlationId }

    static member CreateSimple
        (
            severity: WorkspaceDiagnosticSeverity,
            code: WorkspaceDiagnosticCode,
            safeMessage: string,
            isRetryable: bool,
            correlationId: CorrelationId
        ) =
        WorkspaceDiagnostic.Create(severity, code, safeMessage, None, isRetryable, correlationId)

type WorkspaceErrorCode private (value: string) =
    member _.Value = value
    static member InvalidInput = WorkspaceErrorCode "invalid_input"
    static member UnsupportedCapability = WorkspaceErrorCode "unsupported_capability"
    static member NotFound = WorkspaceErrorCode "not_found"
    static member AmbiguousTarget = WorkspaceErrorCode "ambiguous_target"
    static member WorkspaceConflict = WorkspaceErrorCode "workspace_conflict"
    static member Cancelled = WorkspaceErrorCode "cancelled"
    static member ExternalToolFailed = WorkspaceErrorCode "external_tool_failed"
    static member PartialRecoveryRequired = WorkspaceErrorCode "partial_recovery_required"
    static member InternalError = WorkspaceErrorCode "internal_error"
    override _.ToString() = value
