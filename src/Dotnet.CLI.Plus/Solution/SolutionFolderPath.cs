using Dotnet.CLI.Plus.CommandContextAccessor;
using Dotnet.CLI.Plus.Common;
using static Dotnet.CLI.Plus.CommandContextAccessor.SolutionManipulationError;

namespace Dotnet.CLI.Plus.Solution;

public sealed class SolutionFolderPath
{
    private SolutionFolderPath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<SolutionFolderPath, SolutionManipulationError> FromDirectory(
        string solutionFilePath,
        string targetPath
    )
    {
        try
        {
            var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionFilePath));
            if (solutionDirectory is null)
            {
                return Failure(ManipulationErrorType.InvalidDirectoryPath);
            }

            var targetDirectory = Path.GetFullPath(targetPath, solutionDirectory);
            if (!Directory.Exists(targetDirectory))
            {
                return Failure(ManipulationErrorType.DirectoryNotFound);
            }

            var relativePath = Path.GetRelativePath(solutionDirectory, targetDirectory);
            if (IsOutsideSolution(relativePath))
            {
                return Failure(ManipulationErrorType.DirectoryOutsideSolution);
            }

            var segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            );
            if (segments.Length == 0 || relativePath == ".")
            {
                return Failure(ManipulationErrorType.SolutionRootDirectory);
            }

            return new Result<SolutionFolderPath, SolutionManipulationError>.Success(
                new SolutionFolderPath($"/{string.Join('/', segments)}/")
            );
        }
        catch (ArgumentException)
        {
            return Failure(ManipulationErrorType.InvalidDirectoryPath);
        }
        catch (NotSupportedException)
        {
            return Failure(ManipulationErrorType.InvalidDirectoryPath);
        }
        catch (PathTooLongException)
        {
            return Failure(ManipulationErrorType.InvalidDirectoryPath);
        }
    }

    private static bool IsOutsideSolution(string relativePath) =>
        Path.IsPathRooted(relativePath)
        || relativePath == ".."
        || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private static Result<SolutionFolderPath, SolutionManipulationError> Failure(
        ManipulationErrorType errorType
    ) =>
        new Result<SolutionFolderPath, SolutionManipulationError>.Failure(
            new SolutionManipulationError(errorType)
        );
}
