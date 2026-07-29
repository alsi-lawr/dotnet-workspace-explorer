using Dotnet.CLI.Plus.Core;
using Dotnet.CLI.Plus.Transport;
using Microsoft.FSharp.Core;

namespace Dotnet.CLI.Plus.MSBuild;

internal static class CoreOutcomes
{
    internal static WorkspaceOutcome<T> Success<T>(T value) =>
        WorkspaceOutcome<T>.NewSuccess(value);

    internal static WorkspaceOutcome<T> InvalidInput<T>(
        string inputName,
        string code,
        string message,
        WorkspaceArtifactPath? path = null
    ) => Failure<T>(WorkspaceFailure.NewInvalidInput(inputName, Diagnostic(code, message, path)));

    internal static WorkspaceOutcome<T> NotFound<T>(
        WorkspaceArtifactPath path,
        string code,
        string message
    ) => Failure<T>(WorkspaceFailure.NewNotFound(path.Value, Diagnostic(code, message, path)));

    internal static WorkspaceOutcome<T> Cancelled<T>(string message) =>
        Failure<T>(
            WorkspaceFailure.NewCancelled(
                OperationId.New(),
                Diagnostic(MsBuildDiagnosticCodes.Cancelled, message, null, true)
            )
        );

    internal static WorkspaceOutcome<T> ExternalToolFailed<T>(
        string toolName,
        int exitCode,
        string code,
        string message,
        bool retryable = false
    ) =>
        Failure<T>(
            WorkspaceFailure.NewExternalToolFailed(
                toolName,
                exitCode,
                Diagnostic(code, message, null, retryable)
            )
        );

    internal static WorkspaceOutcome<T> Internal<T>(
        string code,
        string message,
        bool retryable = false
    ) => Failure<T>(WorkspaceFailure.NewInternal(Diagnostic(code, message, null, retryable)));

    internal static WorkspaceOutcome<T> WorkerClosed<T>() =>
        ExternalToolFailed<T>(
            "msbuild-host",
            -1,
            MsBuildDiagnosticCodes.WorkerClosed,
            "The MSBuild evaluator is closed."
        );

    internal static WorkspaceOutcome<T> Failure<T>(WorkspaceFailure failure) =>
        WorkspaceOutcome<T>.NewFailure(failure);

    internal static bool TrySuccess<T>(
        WorkspaceOutcome<T> outcome,
        out T? value,
        out WorkspaceFailure? failure
    )
    {
        if (outcome is WorkspaceOutcome<T>.Success success)
        {
            value = success.value;
            failure = null;
            return true;
        }

        value = default;
        failure = ((WorkspaceOutcome<T>.Failure)outcome).error;
        return false;
    }

    internal static RpcError ToRpcError(WorkspaceFailure failure) =>
        RpcErrors.create(failure.Diagnostic.DiagnosticCode.Value, failure.Diagnostic.Message, null);

    internal static WorkspaceOutcome<T> FromRpcError<T>(
        RpcError error,
        WorkspaceArtifactPath? projectPath
    ) =>
        error.Code switch
        {
            MsBuildDiagnosticCodes.ProjectNotFound when projectPath is not null => NotFound<T>(
                projectPath,
                error.Code,
                error.Message
            ),
            MsBuildDiagnosticCodes.ProjectMalformed => InvalidInput<T>(
                "projectPath",
                error.Code,
                error.Message,
                projectPath
            ),
            MsBuildDiagnosticCodes.EvaluationFailed => Internal<T>(error.Code, error.Message),
            _ => Internal<T>(
                MsBuildDiagnosticCodes.ProtocolFailure,
                "The MSBuild worker returned an unsupported failure."
            ),
        };

    internal static WorkspaceDiagnostic Diagnostic(
        string code,
        string message,
        WorkspaceArtifactPath? path,
        bool retryable = false,
        WorkspaceDiagnosticSeverity severity = WorkspaceDiagnosticSeverity.Error
    ) =>
        WorkspaceDiagnostic.Create(
            severity,
            WorkspaceDiagnosticCode.Create(code),
            message,
            path is null ? null : FSharpOption<WorkspaceArtifactPath>.Some(path),
            null,
            retryable,
            CorrelationId.New()
        );
}

internal static class MsBuildDiagnosticCodes
{
    internal const string AssetsMissing = "msbuild.assets_missing";
    internal const string Cancelled = "msbuild.cancelled";
    internal const string EvaluationFailed = "msbuild.evaluation_failed";
    internal const string ProjectMalformed = "msbuild.project_malformed";
    internal const string ProjectNotFound = "msbuild.project_not_found";
    internal const string ProtocolFailure = "msbuild.protocol_failure";
    internal const string SdkNotFound = "msbuild.sdk_not_found";
    internal const string SdkSelectionFailed = "msbuild.sdk_selection_failed";
    internal const string SdkStartFailed = "msbuild.sdk_start_failed";
    internal const string ToolsetIncompatible = "msbuild.toolset_incompatible";
    internal const string WorkerClosed = "msbuild.worker_closed";
    internal const string WorkerCrashed = "msbuild.worker_crashed";
    internal const string WorkerDisabled = "msbuild.worker_disabled";
}
