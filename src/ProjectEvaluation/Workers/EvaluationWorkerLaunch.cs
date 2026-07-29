using System.Diagnostics;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal sealed record EvaluationWorkerLaunch(
    string HostExecutable,
    string? HostAssembly,
    string DotnetExecutable
)
{
    internal static EvaluationWorkerLaunch ForCurrentProcess() =>
        new(
            Environment.ProcessPath
                ?? throw new InvalidOperationException("The host executable path is unavailable."),
            null,
            "dotnet"
        );

    internal ProcessStartInfo CreateStartInfo(DotnetSdkSelection selection)
    {
        var start = new ProcessStartInfo(HostAssembly is null ? HostExecutable : DotnetExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (HostAssembly is not null)
        {
            start.ArgumentList.Add(HostAssembly);
        }

        start.ArgumentList.Add("internal");
        start.ArgumentList.Add("project-evaluation-host");
        start.ArgumentList.Add("--sdk");
        start.ArgumentList.Add(selection.SdkPath.Value);
        return start;
    }
}
