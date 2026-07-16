using Dotnet.CLI.Plus.CommandContextAccessor;
using Dotnet.CLI.Plus.Common;
using Dotnet.CLI.Plus.Solution;

namespace Dotnet.CLI.Plus.Tests.Solution;

public sealed class SolutionFolderPathTests
{
    [Fact]
    public void FromDirectoryCreatesNestedSolutionFolderPath()
    {
        using var directory = new TemporaryDirectory();
        var targetDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "src", "tools")
        );

        var result = SolutionFolderPath.FromDirectory(
            Path.Combine(directory.Path, "Example.slnx"),
            targetDirectory.FullName
        );
        var success = Assert.IsType<Result<SolutionFolderPath, SolutionManipulationError>.Success>(
            result
        );

        Assert.Equal("/src/tools/", success.Value.Value);
    }

    [Fact]
    public void FromDirectoryRejectsDirectoriesOutsideTheSolution()
    {
        using var solutionDirectory = new TemporaryDirectory();
        using var targetDirectory = new TemporaryDirectory();

        var result = SolutionFolderPath.FromDirectory(
            Path.Combine(solutionDirectory.Path, "Example.slnx"),
            targetDirectory.Path
        );
        var failure = Assert.IsType<Result<SolutionFolderPath, SolutionManipulationError>.Failure>(
            result
        );

        Assert.Equal(
            SolutionManipulationError.ManipulationErrorType.DirectoryOutsideSolution,
            failure.Error.ErrorType
        );
    }
}
