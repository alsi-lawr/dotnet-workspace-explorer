using Dotnet.WorkspaceExplorer.Rpc;
using Dotnet.WorkspaceExplorer.Workspaces;
using Microsoft.FSharp.Core;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal static class ProjectEvaluationOutcomes
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
                WorkspaceOperationId.New(),
                Diagnostic(ProjectEvaluationDiagnosticCodes.Cancelled, message, null, true)
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
            "project-evaluation-host",
            -1,
            ProjectEvaluationDiagnosticCodes.WorkerClosed,
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
        RpcErrors.create(failure.Diagnostic.Code.Value, failure.Diagnostic.Message, null);

    internal static WorkspaceOutcome<T> FromRpcError<T>(
        RpcError error,
        WorkspaceArtifactPath? projectPath
    ) =>
        error.Code switch
        {
            ProjectEvaluationDiagnosticCodes.ProjectNotFound when projectPath is not null =>
                NotFound<T>(projectPath, error.Code, error.Message),
            ProjectEvaluationDiagnosticCodes.ProjectMalformed => InvalidInput<T>(
                "projectPath",
                error.Code,
                error.Message,
                projectPath
            ),
            ProjectEvaluationDiagnosticCodes.EvaluationFailed => Internal<T>(
                error.Code,
                error.Message
            ),
            _ => Internal<T>(
                ProjectEvaluationDiagnosticCodes.ProtocolFailure,
                "The project evaluation worker returned an unsupported failure."
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
