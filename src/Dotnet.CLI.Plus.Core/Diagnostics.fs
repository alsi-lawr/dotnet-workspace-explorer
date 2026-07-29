namespace Dotnet.CLI.Plus.Core

open System
open System.IO

type WorkspaceArtifactPath private (value: string) =
    member _.Value = value

    static member Create(path: string) =
        path
        |> Validation.nonEmpty (nameof path)
        |> Path.GetFullPath
        |> WorkspaceArtifactPath

    override _.ToString() = value

type WorkspaceSourceLocation private (line: int, column: int) =
    member _.Line = line
    member _.Column = column

    static member Create(line: int, column: int) =
        if line < 0 then
            invalidArg (nameof line) "A source line cannot be negative."

        if column < 0 then
            invalidArg (nameof column) "A source column cannot be negative."

        WorkspaceSourceLocation(line, column)

type WorkspaceDiagnosticSeverity =
    | Information = 0
    | Warning = 1
    | Error = 2

type WorkspaceDiagnosticCode private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> WorkspaceDiagnosticCode

    override _.ToString() = value

type CorrelationId private (value: Guid) =
    member _.Value = value
    static member New() = CorrelationId(Guid.NewGuid())
    override _.ToString() = value.ToString "N"

type WorkspaceDiagnostic =
    private
        { Severity: WorkspaceDiagnosticSeverity
          Code: WorkspaceDiagnosticCode
          SafeMessage: string
          ArtifactPath: WorkspaceArtifactPath option
          Location: WorkspaceSourceLocation option
          IsRetryable: bool
          CorrelationId: CorrelationId }

    member this.DiagnosticSeverity = this.Severity
    member this.DiagnosticCode = this.Code
    member this.Message = this.SafeMessage
    member this.DiagnosticArtifactPath = this.ArtifactPath
    member this.DiagnosticLocation = this.Location
    member this.Retryable = this.IsRetryable
    member this.DiagnosticCorrelationId = this.CorrelationId

    static member Create
        (
            severity: WorkspaceDiagnosticSeverity,
            code: WorkspaceDiagnosticCode,
            safeMessage: string,
            artifactPath: WorkspaceArtifactPath option,
            location: WorkspaceSourceLocation option,
            isRetryable: bool,
            correlationId: CorrelationId
        ) =
        if isNull (box code) then
            nullArg (nameof code)

        if isNull (box correlationId) then
            nullArg (nameof correlationId)

        safeMessage |> Validation.nonEmpty (nameof safeMessage) |> ignore

        { Severity = severity
          Code = code
          SafeMessage = safeMessage
          ArtifactPath = artifactPath
          Location = location
          IsRetryable = isRetryable
          CorrelationId = correlationId }

    static member CreateSimple
        (
            severity: WorkspaceDiagnosticSeverity,
            code: WorkspaceDiagnosticCode,
            safeMessage: string,
            isRetryable: bool,
            correlationId: CorrelationId
        ) =
        WorkspaceDiagnostic.Create(
            severity,
            code,
            safeMessage,
            None,
            None,
            isRetryable,
            correlationId
        )

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
