using System.Runtime.ExceptionServices;
using Dotnet.CLI.Plus.CommandContextAccessor;
using Dotnet.CLI.Plus.Common;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using static Dotnet.CLI.Plus.CommandContextAccessor.SolutionParseError;

namespace Dotnet.CLI.Plus.Solution;

public static class SolutionParser
{
    private static readonly string[] SupportedExtensions = [".sln", ".slnx"];

    public static async Task<Result<SolutionDocument, SolutionParseError>> ParseAsync(
        string solutionPath,
        CancellationToken cancellationToken = default
    )
    {
        var pathResult = ResolveSolutionFilePath(solutionPath);

        return await pathResult.Match(
            solutionFilePath => OpenAsync(solutionFilePath, cancellationToken),
            error =>
                Task.FromResult<Result<SolutionDocument, SolutionParseError>>(
                    new Result<SolutionDocument, SolutionParseError>.Failure(error)
                )
        );
    }

    private static async Task<Result<SolutionDocument, SolutionParseError>> OpenAsync(
        string solutionFilePath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var serializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath);
            if (serializer is null)
            {
                return Failure<SolutionDocument>(ParseErrorType.NotASolutionFile);
            }

            var model = await serializer.OpenAsync(solutionFilePath, cancellationToken);
            return new Result<SolutionDocument, SolutionParseError>.Success(
                new SolutionDocument(solutionFilePath, serializer, model)
            );
        }
        catch (SolutionException exception)
        {
            return InternalFailure<SolutionDocument>(exception);
        }
        catch (IOException exception)
        {
            return InternalFailure<SolutionDocument>(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return InternalFailure<SolutionDocument>(exception);
        }
    }

    private static Result<string, SolutionParseError> ResolveSolutionFilePath(string solutionPath)
    {
        try
        {
            if (Directory.Exists(solutionPath))
            {
                return FindSolutionInDirectory(solutionPath);
            }

            if (!File.Exists(solutionPath))
            {
                return Failure<string>(
                    IsSupportedSolutionPath(solutionPath)
                        ? ParseErrorType.FileNotFound
                        : ParseErrorType.DirectoryNotFound
                );
            }

            return !IsSupportedSolutionPath(solutionPath)
                ? Failure<string>(ParseErrorType.NotASolutionFile)
                : new Result<string, SolutionParseError>.Success(Path.GetFullPath(solutionPath));
        }
        catch (IOException exception)
        {
            return InternalFailure<string>(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return InternalFailure<string>(exception);
        }
    }

    private static Result<string, SolutionParseError> FindSolutionInDirectory(
        string solutionDirectory
    )
    {
        var solutionFiles = Directory
            .EnumerateFiles(solutionDirectory, "*", SearchOption.AllDirectories)
            .Where(IsSupportedSolutionPath)
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .Take(2)
            .ToArray();

        return solutionFiles.Length switch
        {
            0 => Failure<string>(ParseErrorType.FileNotFound),
            > 1 => Failure<string>(ParseErrorType.MultipleSolutionsFound),
            _ => new Result<string, SolutionParseError>.Success(solutionFiles[0]),
        };
    }

    private static bool IsSupportedSolutionPath(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static Result<TValue, SolutionParseError> Failure<TValue>(ParseErrorType errorType)
        where TValue : notnull =>
        new Result<TValue, SolutionParseError>.Failure(new SolutionParseError(errorType));

    private static Result<TValue, SolutionParseError> InternalFailure<TValue>(Exception exception)
        where TValue : notnull =>
        new Result<TValue, SolutionParseError>.Failure(
            new SolutionParseError(
                ParseErrorType.InternalParsingError,
                ExceptionDispatchInfo.Capture(exception)
            )
        );
}
