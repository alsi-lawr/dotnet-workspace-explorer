namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal static class ProjectEvaluationDiagnosticCodes
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
