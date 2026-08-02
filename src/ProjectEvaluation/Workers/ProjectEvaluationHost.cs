using System.Runtime.CompilerServices;
using Dotnet.WorkspaceExplorer.Rpc;
using Microsoft.Build.Locator;

namespace Dotnet.WorkspaceExplorer.ProjectEvaluation;

internal static class ProjectEvaluationHost
{
    private const int InvalidSdkExitCode = 66;
    private const int SdkLoadExitCode = 70;

    internal static Task<int> RunAsync(string sdkPath, CancellationToken cancellationToken) =>
        RegisterThenRunAsync(Path.GetFullPath(sdkPath), cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<int> RegisterThenRunAsync(
        string sdkPath,
        CancellationToken cancellationToken
    )
    {
        if (!Directory.Exists(sdkPath))
        {
            Console.Error.WriteLine("project-evaluation-host:sdk-not-found");
            return Task.FromResult(InvalidSdkExitCode);
        }

        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterMSBuildPath(sdkPath);
            }
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("project-evaluation-host:locator-registration-failed");
            return Task.FromResult(SdkLoadExitCode);
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine("project-evaluation-host:locator-registration-failed");
            return Task.FromResult(SdkLoadExitCode);
        }

        return RunRegisteredAsync(sdkPath, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunRegisteredAsync(
        string sdkPath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await ProjectEvaluationServer.RunAsync(sdkPath, cancellationToken);
        }
        catch (FileLoadException)
        {
            Console.Error.WriteLine("project-evaluation-host:sdk-load-failed");
            return SdkLoadExitCode;
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine("project-evaluation-host:sdk-load-failed");
            return SdkLoadExitCode;
        }
        catch (TypeLoadException)
        {
            Console.Error.WriteLine("project-evaluation-host:sdk-load-failed");
            return SdkLoadExitCode;
        }
    }
}
