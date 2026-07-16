using Dotnet.CLI.Plus.CommandContextAccessor;
using Dotnet.CLI.Plus.Common;
using Dotnet.CLI.Plus.Solution;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Dotnet.CLI.Plus.Tests.Solution;

public sealed class SolutionParserTests
{
    [Theory]
    [InlineData(".sln")]
    [InlineData(".slnx")]
    public async Task ParseAsyncLoadsAndSavesSupportedSolutionFormats(string extension)
    {
        using var directory = new TemporaryDirectory();
        var solutionPath = Path.Combine(directory.Path, $"Example{extension}");
        var serializer =
            SolutionSerializers.GetSerializerByMoniker(solutionPath)
            ?? throw new InvalidOperationException($"No serializer supports {extension}.");

        await serializer.SaveAsync(solutionPath, new SolutionModel(), CancellationToken.None);

        var parseResult = await SolutionParser.ParseAsync(solutionPath);
        var solution = Assert
            .IsType<Result<SolutionDocument, SolutionParseError>.Success>(parseResult)
            .Value;
        solution.Model.AddFolder("/src/tools/");
        var saveResult = await solution.SaveAsync();

        var reopenedParseResult = await SolutionParser.ParseAsync(solutionPath);
        var reopenedSolution = Assert
            .IsType<Result<SolutionDocument, SolutionParseError>.Success>(reopenedParseResult)
            .Value;

        Assert.IsType<Result<Unit, SolutionManipulationError>.Success>(saveResult);
        Assert.Equal(Path.GetFullPath(solutionPath), reopenedSolution.FilePath);
        Assert.NotNull(reopenedSolution.Model.FindFolder("/src/tools/"));
    }

    [Fact]
    public async Task ParseAsyncFindsTheOnlySolutionBelowADirectory()
    {
        using var directory = new TemporaryDirectory();
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "nested"));
        var solutionPath = Path.Combine(nestedDirectory.FullName, "Example.slnx");

        await SolutionSerializers.SlnXml.SaveAsync(
            solutionPath,
            new SolutionModel(),
            CancellationToken.None
        );

        var parseResult = await SolutionParser.ParseAsync(directory.Path);
        var solution = Assert
            .IsType<Result<SolutionDocument, SolutionParseError>.Success>(parseResult)
            .Value;

        Assert.Equal(Path.GetFullPath(solutionPath), solution.FilePath);
    }

    [Fact]
    public async Task ParseAsyncRejectsDirectoriesContainingMultipleSolutions()
    {
        using var directory = new TemporaryDirectory();

        await SolutionSerializers.SlnXml.SaveAsync(
            Path.Combine(directory.Path, "First.slnx"),
            new SolutionModel(),
            CancellationToken.None
        );
        await SolutionSerializers.SlnXml.SaveAsync(
            Path.Combine(directory.Path, "Second.slnx"),
            new SolutionModel(),
            CancellationToken.None
        );

        var parseResult = await SolutionParser.ParseAsync(directory.Path);
        var failure = Assert.IsType<Result<SolutionDocument, SolutionParseError>.Failure>(
            parseResult
        );

        Assert.Equal(
            SolutionParseError.ParseErrorType.MultipleSolutionsFound,
            failure.Error.ErrorType
        );
    }
}
