using System.Runtime.ExceptionServices;
using Dotnet.CLI.Plus.Common;
using Spectre.Console;

namespace Dotnet.CLI.Plus.CommandContextAccessor;

public sealed class SolutionManipulationError(
    SolutionManipulationError.ManipulationErrorType errorType,
    ExceptionDispatchInfo? exception = null
) : ICliError
{
    public ManipulationErrorType ErrorType { get; } = errorType;
    private ExceptionDispatchInfo? ExceptionInfo { get; } = exception;

    public enum ManipulationErrorType
    {
        DirectoryNotFound,
        DirectoryOutsideSolution,
        SolutionRootDirectory,
        InvalidDirectoryPath,
        WriteFailed,
    }

    public int DisplayCliInfo()
    {
        Action writeError = this switch
        {
            { ErrorType: ManipulationErrorType.DirectoryNotFound } => () =>
                AnsiConsole.MarkupLine("[yellow]Directory to add was not found.[/]"),
            { ErrorType: ManipulationErrorType.DirectoryOutsideSolution } => () =>
                AnsiConsole.MarkupLine(
                    "[red]Directory to add must be inside the solution directory.[/]"
                ),
            { ErrorType: ManipulationErrorType.SolutionRootDirectory } => () =>
                AnsiConsole.MarkupLine("[yellow]The solution root cannot be added as a folder.[/]"),
            { ErrorType: ManipulationErrorType.InvalidDirectoryPath } => () =>
                AnsiConsole.MarkupLine("[red]Directory path is invalid.[/]"),
            { ErrorType: ManipulationErrorType.WriteFailed } => DisplayWriteFailure,
            _ => throw new ArgumentOutOfRangeException(),
        };

        writeError();
        return 1;
    }

    private void DisplayWriteFailure()
    {
        if (ExceptionInfo is null)
        {
            AnsiConsole.MarkupLine("[red]Failed to save the solution file.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[red]Failed to save the solution file:[/]");
        AnsiConsole.WriteException(ExceptionInfo.SourceException);
    }
}
