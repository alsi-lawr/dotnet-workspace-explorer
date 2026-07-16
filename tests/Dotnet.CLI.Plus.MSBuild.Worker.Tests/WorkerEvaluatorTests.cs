using System.Diagnostics;
using System.Runtime.CompilerServices;
using Dotnet.CLI.Plus.Core;
using Microsoft.Build.Locator;
using Xunit;

namespace Dotnet.CLI.Plus.MSBuild.Worker.Tests;

public sealed class WorkerEvaluatorTests
{
    [Fact]
    public void CacheCapacityUsesDeterministicLruEviction()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotnet-cli-plus-msbuild-lru-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);

        try
        {
            RegisterSelectedToolset(directory);
            RunRegistered(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterSelectedToolset(string workingDirectory)
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        var version = RunDotnet(workingDirectory, "--version").Trim();
        var listing = RunDotnet(workingDirectory, "--list-sdks");
        var toolset = DotnetSdkDiscovery.FindSdkPath(listing, version);
        Assert.NotNull(toolset);
        MSBuildLocator.RegisterMSBuildPath(toolset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunRegistered(string directory)
    {
        var projects = Enumerable
            .Range(1, 3)
            .Select(index =>
            {
                var path = Path.Combine(directory, $"Project{index}.proj");
                File.WriteAllText(
                    path,
                    $"<Project><PropertyGroup><Value>{index}</Value></PropertyGroup></Project>"
                );
                return WorkspaceArtifactPath.Create(path);
            })
            .ToArray();

        using var evaluator = new WorkerEvaluator(2);
        foreach (var project in projects)
        {
            Assert.True(evaluator.Evaluate(project).IsSuccess);
        }

        Assert.Equal(2, evaluator.CachedProjectCount);
        Assert.Empty(evaluator.Invalidate([projects[0]]).InvalidatedProjects);
        Assert.Single(evaluator.Invalidate([projects[1]]).InvalidatedProjects);
        Assert.Equal(1, evaluator.CachedProjectCount);
    }

    private static string RunDotnet(string workingDirectory, string argument)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(argument);
        using var process =
            Process.Start(start) ?? throw new InvalidOperationException("dotnet could not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }
}
