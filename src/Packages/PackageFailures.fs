namespace Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
type PackageFailureKind =
    | InvalidRequest
    | NotFound
    | AmbiguousTarget
    | Unsupported
    | AuthenticationRequired
    | SourceUnavailable
    | StaleState
    | Cancelled
    | ExternalToolFailed
    | PartialRecoveryRequired
    | Internal

[<RequireQualifiedAccess>]
type PackageFailureRetry =
    | Never
    | AfterUserAction
    | Transient

type PackageFailure =
    private
        { Kind: PackageFailureKind
          Code: string
          Message: string
          Retry: PackageFailureRetry }

[<RequireQualifiedAccess>]
module PackageFailure =
    let private stableCode kind =
        match kind with
        | PackageFailureKind.InvalidRequest -> "DWE-PACKAGE-INVALID-REQUEST"
        | PackageFailureKind.NotFound -> "DWE-PACKAGE-NOT-FOUND"
        | PackageFailureKind.AmbiguousTarget -> "DWE-PACKAGE-AMBIGUOUS-TARGET"
        | PackageFailureKind.Unsupported -> "DWE-PACKAGE-UNSUPPORTED"
        | PackageFailureKind.AuthenticationRequired -> "DWE-PACKAGE-AUTHENTICATION-REQUIRED"
        | PackageFailureKind.SourceUnavailable -> "DWE-PACKAGE-SOURCE-UNAVAILABLE"
        | PackageFailureKind.StaleState -> "DWE-PACKAGE-STALE-STATE"
        | PackageFailureKind.Cancelled -> "DWE-PACKAGE-CANCELLED"
        | PackageFailureKind.ExternalToolFailed -> "DWE-PACKAGE-EXTERNAL-TOOL-FAILED"
        | PackageFailureKind.PartialRecoveryRequired -> "DWE-PACKAGE-PARTIAL-RECOVERY"
        | PackageFailureKind.Internal -> "DWE-PACKAGE-INTERNAL"

    let create kind message retry =
        if System.String.IsNullOrWhiteSpace message then
            Error(PackageContractViolation.MissingValue "failureMessage")
        else
            Ok
                { Kind = kind
                  Code = stableCode kind
                  Message = message
                  Retry = retry }

    let kind failure = failure.Kind
    let code failure = failure.Code
    let message failure = failure.Message
    let retry failure = failure.Retry
