using System.Runtime.ExceptionServices;
using Dotnet.CLI.Plus.Common;
using Spectre.Console;

namespace Dotnet.CLI.Plus.CommandContextAccessor;

public sealed class SolutionParseError(
    SolutionParseError.ParseErrorType errorType,
    ExceptionDispatchInfo? exception = null
) : ICliError
{
    public ParseErrorType ErrorType { get; } = errorType;
    private ExceptionDispatchInfo? ExceptionInfo { get; } = exception;

    public enum ParseErrorType
    {
        FileNotFound,
        DirectoryNotFound,
        NotASolutionFile,
        InternalParsingError,
        MultipleSolutionsFound,
    }

    public int DisplayCliInfo()
    {
        Action writeError = this switch
        {
            { ErrorType: ParseErrorType.FileNotFound } => () =>
                AnsiConsole.MarkupLine("[yellow]Solution file not found.[/]"),
            { ErrorType: ParseErrorType.DirectoryNotFound } => () =>
                AnsiConsole.MarkupLine("[yellow]Solution directory not found.[/]"),
            { ErrorType: ParseErrorType.NotASolutionFile } => () =>
                AnsiConsole.MarkupLine("[red]Expected a .sln or .slnx solution file.[/]"),
            { ErrorType: ParseErrorType.MultipleSolutionsFound } => () =>
                AnsiConsole.MarkupLine(
                    "[red]Multiple solution files were found. Specify one explicitly.[/]"
                ),
            { ErrorType: ParseErrorType.InternalParsingError } => DisplayInternalFailure,
            _ => throw new ArgumentOutOfRangeException(),
        };

        writeError();
        return 1;
    }

    private void DisplayInternalFailure()
    {
        if (ExceptionInfo is null)
        {
            AnsiConsole.MarkupLine("[red]Failed to parse solution file.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[red]Parser failed to read the solution file:[/]");
        AnsiConsole.WriteException(ExceptionInfo.SourceException);
    }
}
