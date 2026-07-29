using System.ComponentModel;
using System.Diagnostics;
using Dotnet.WorkspaceExplorer.Workspaces;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal static class DotnetSdkResolver
{
    internal static async Task<WorkspaceOutcome<DotnetSdkSelection>> DiscoverAsync(
        WorkspaceArtifactPath workspacePath,
        string dotnetExecutable,
        CancellationToken cancellationToken
    )
    {
        var workingDirectory = Directory.Exists(workspacePath.Value)
            ? workspacePath.Value
            : Path.GetDirectoryName(workspacePath.Value) ?? Directory.GetCurrentDirectory();
        var version = await RunDotnetAsync(
            dotnetExecutable,
            "--version",
            workingDirectory,
            cancellationToken
        );
        if (
            !ProjectEvaluationOutcomes.TrySuccess(
                version,
                out var selectedVersion,
                out var versionFailure
            )
        )
        {
            return ProjectEvaluationOutcomes.Failure<DotnetSdkSelection>(versionFailure!);
        }

        var installed = await RunDotnetAsync(
            dotnetExecutable,
            "--list-sdks",
            workingDirectory,
            cancellationToken
        );
        if (
            !ProjectEvaluationOutcomes.TrySuccess(
                installed,
                out var installedSdks,
                out var installedFailure
            )
        )
        {
            return ProjectEvaluationOutcomes.Failure<DotnetSdkSelection>(installedFailure!);
        }

        var sdkVersion = selectedVersion!.Trim();
        var sdkPath = FindSdkPath(installedSdks!, sdkVersion);
        return sdkPath is null
            ? ProjectEvaluationOutcomes.ExternalToolFailed<DotnetSdkSelection>(
                "dotnet",
                -1,
                ProjectEvaluationDiagnosticCodes.SdkNotFound,
                "The selected workspace SDK could not be located."
            )
            : ProjectEvaluationOutcomes.Success(
                new DotnetSdkSelection(
                    WorkspaceArtifactPath.Create(sdkPath),
                    FindGlobalJson(workingDirectory)
                )
            );
    }

    internal static string? FindSdkPath(string listing, string version) =>
        listing
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(" [", StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && StringComparer.Ordinal.Equals(parts[0], version))
            .Select(parts => Path.Combine(parts[1].TrimEnd(']'), version))
            .FirstOrDefault(Directory.Exists);

    private static async Task<WorkspaceOutcome<string>> RunDotnetAsync(
        string dotnetExecutable,
        string argument,
        string workingDirectory,
        CancellationToken cancellationToken
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(dotnetExecutable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
            {
                return ProjectEvaluationOutcomes.ExternalToolFailed<string>(
                    "dotnet",
                    -1,
                    ProjectEvaluationDiagnosticCodes.SdkStartFailed,
                    "The dotnet SDK command could not be started."
                );
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorDrain = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, errorDrain);
            return process.ExitCode == 0
                ? ProjectEvaluationOutcomes.Success(output.Result)
                : ProjectEvaluationOutcomes.ExternalToolFailed<string>(
                    "dotnet",
                    process.ExitCode,
                    ProjectEvaluationDiagnosticCodes.SdkSelectionFailed,
                    "The workspace SDK could not be selected."
                );
        }
        catch (OperationCanceledException)
        {
            await KillAndReapAsync(process);
            return ProjectEvaluationOutcomes.Cancelled<string>(
                "The workspace SDK selection was cancelled."
            );
        }
        catch (Win32Exception)
        {
            return ProjectEvaluationOutcomes.ExternalToolFailed<string>(
                "dotnet",
                -1,
                ProjectEvaluationDiagnosticCodes.SdkStartFailed,
                "The dotnet SDK command could not be started."
            );
        }
    }

    private static WorkspaceArtifactPath? FindGlobalJson(string directory)
    {
        for (
            var candidate = new DirectoryInfo(directory);
            candidate is not null;
            candidate = candidate.Parent
        )
        {
            var path = Path.Combine(candidate.FullName, "global.json");
            if (File.Exists(path))
            {
                return WorkspaceArtifactPath.Create(path);
            }
        }

        return null;
    }

    private static async Task KillAndReapAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(true);
        }

        await process.WaitForExitAsync();
    }
}
