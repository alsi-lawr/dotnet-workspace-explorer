using System.Diagnostics;
using System.Runtime.CompilerServices;
using Dotnet.CLI.Plus.Core;
using Dotnet.CLI.Plus.Transport;
using Microsoft.Build.Locator;
using Xunit;

namespace Dotnet.CLI.Plus.MSBuild.Worker.Tests;

public sealed class WorkerEvaluatorTests
{
    [Fact]
    public void CacheCapacityUsesDeterministicLruEviction()
    {
        var directory = TemporaryDirectory("lru");

        try
        {
            RegisterSelectedToolset(directory);
            RunLru(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FailedInnerDimensionIsRepeatableAndRecoversAfterImportRepair()
    {
        var directory = TemporaryDirectory("partial");

        try
        {
            RegisterSelectedToolset(directory);
            RunPartialLoadRecovery(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FragmentedNearLimitResponseIsAccumulatedOnceAndDecodedExactly()
    {
        const int frameLimit = 1024 * 1024;
        var payload = new string('x', frameLimit - 16);
        var encoded = RpcCodec.encodeFrame(
            RpcFrame.NewResponse(42, null, RpcValue.NewString(payload))
        );
        Assert.InRange(encoded.Length, frameLimit - 32, frameLimit);

        await using var stream = new FragmentedReadStream(encoded, 257);
        var attempt = await WorkerClient.ReadResponseAsync(
            stream,
            42,
            frameLimit,
            CancellationToken.None
        );
        var received = Assert.IsType<WorkerClient.WorkerAttempt.Received>(attempt);
        var result = Assert.IsType<RpcValue.String>(received.Result);

        Assert.Equal(payload, result.Item);
        Assert.Equal(encoded.Length, stream.BytesRead);
        Assert.True(stream.ReadCount > 1000);

        var oversized = RpcCodec.encodeFrame(
            RpcFrame.NewResponse(43, null, RpcValue.NewString(new string('x', frameLimit)))
        );
        await using var oversizedStream = new FragmentedReadStream(oversized, 257);
        var rejected = await WorkerClient.ReadResponseAsync(
            oversizedStream,
            43,
            frameLimit,
            CancellationToken.None
        );

        Assert.IsType<WorkerClient.WorkerAttempt.TransportFailed>(rejected);
        Assert.Equal(frameLimit + 1, oversizedStream.BytesRead);
    }

    private static string TemporaryDirectory(string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"dotnet-cli-plus-msbuild-{name}-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
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
    private static void RunLru(string directory)
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
        Assert.True(evaluator.Evaluate(projects[0]).IsSuccess);
        Assert.True(evaluator.Evaluate(projects[1]).IsSuccess);
        Assert.True(evaluator.Evaluate(projects[0]).IsSuccess);
        Assert.True(evaluator.Evaluate(projects[2]).IsSuccess);

        Assert.Equal(2, evaluator.CachedProjectCount);
        Assert.Empty(evaluator.Invalidate([projects[1]]).InvalidatedProjects);
        Assert.Single(evaluator.Invalidate([projects[0]]).InvalidatedProjects);
        Assert.Equal(1, evaluator.CachedProjectCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunPartialLoadRecovery(string directory)
    {
        var projectPath = Path.Combine(directory, "Partial.proj");
        var importPath = Path.Combine(directory, "broken.targets");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>
              <Import Project="broken.targets" Condition="'$(TargetFramework)' == 'net9.0'" />
            </Project>
            """
        );
        File.WriteAllText(importPath, "<Project><PropertyGroup>");
        var project = WorkspaceArtifactPath.Create(projectPath);

        using var evaluator = new WorkerEvaluator();
        var first = AssertFailure(evaluator.Evaluate(project));
        var second = AssertFailure(evaluator.Evaluate(project));

        Assert.True(first.IsInvalidInput);
        Assert.True(second.IsInvalidInput);
        Assert.Equal(
            MsBuildDiagnosticCodes.ProjectMalformed,
            first.Diagnostic.DiagnosticCode.Value
        );
        Assert.Equal(
            MsBuildDiagnosticCodes.ProjectMalformed,
            second.Diagnostic.DiagnosticCode.Value
        );

        File.WriteAllText(
            importPath,
            "<Project><PropertyGroup><Recovered>true</Recovered></PropertyGroup></Project>"
        );
        var recovered = Assert
            .IsType<WorkspaceOutcome<EvaluationSnapshot>.Success>(evaluator.Evaluate(project))
            .value;

        Assert.Equal(3, recovered.Dimensions.Length);
        Assert.Contains(
            recovered.Dimensions,
            dimension =>
                dimension.TargetFramework?.Value == "net9.0"
                && dimension.Properties.Any(property =>
                    property.Name == "Recovered" && property.Value == "true"
                )
        );
        Assert.Equal(1, evaluator.CachedProjectCount);
    }

    private static WorkspaceFailure AssertFailure<T>(WorkspaceOutcome<T> outcome) =>
        Assert.IsType<WorkspaceOutcome<T>.Failure>(outcome).error;

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

    private sealed class FragmentedReadStream(byte[] bytes, int fragmentSize)
        : MemoryStream(bytes, false)
    {
        internal int BytesRead => checked((int)Position);
        internal int ReadCount { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            return base.Read(buffer[..Math.Min(buffer.Length, fragmentSize)]);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return base.ReadAsync(
                buffer[..Math.Min(buffer.Length, fragmentSize)],
                cancellationToken
            );
        }
    }
}
