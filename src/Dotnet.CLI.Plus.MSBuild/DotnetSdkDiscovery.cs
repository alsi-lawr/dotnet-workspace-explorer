using System.ComponentModel;
using System.Diagnostics;
using Dotnet.CLI.Plus.Core;

namespace Dotnet.CLI.Plus.MSBuild;

internal static class DotnetSdkDiscovery
{
    internal static async Task<WorkspaceOutcome<ToolsetSelection>> DiscoverAsync(
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
        if (!CoreOutcomes.TrySuccess(version, out var selectedVersion, out var versionFailure))
        {
            return CoreOutcomes.Failure<ToolsetSelection>(versionFailure!);
        }

        var installed = await RunDotnetAsync(
            dotnetExecutable,
            "--list-sdks",
            workingDirectory,
            cancellationToken
        );
        if (!CoreOutcomes.TrySuccess(installed, out var installedSdks, out var installedFailure))
        {
            return CoreOutcomes.Failure<ToolsetSelection>(installedFailure!);
        }

        var sdkVersion = selectedVersion!.Trim();
        var toolsetPath = FindSdkPath(installedSdks!, sdkVersion);
        return toolsetPath is null
            ? CoreOutcomes.ExternalToolFailed<ToolsetSelection>(
                "dotnet",
                -1,
                MsBuildDiagnosticCodes.SdkNotFound,
                "The selected workspace SDK could not be located."
            )
            : CoreOutcomes.Success(
                new ToolsetSelection(
                    WorkspaceArtifactPath.Create(toolsetPath),
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
                return CoreOutcomes.ExternalToolFailed<string>(
                    "dotnet",
                    -1,
                    MsBuildDiagnosticCodes.SdkStartFailed,
                    "The dotnet SDK command could not be started."
                );
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorDrain = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, errorDrain);
            return process.ExitCode == 0
                ? CoreOutcomes.Success(output.Result)
                : CoreOutcomes.ExternalToolFailed<string>(
                    "dotnet",
                    process.ExitCode,
                    MsBuildDiagnosticCodes.SdkSelectionFailed,
                    "The workspace SDK could not be selected."
                );
        }
        catch (OperationCanceledException)
        {
            await KillAndReapAsync(process);
            return CoreOutcomes.Cancelled<string>("The workspace SDK selection was cancelled.");
        }
        catch (Win32Exception)
        {
            return CoreOutcomes.ExternalToolFailed<string>(
                "dotnet",
                -1,
                MsBuildDiagnosticCodes.SdkStartFailed,
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
