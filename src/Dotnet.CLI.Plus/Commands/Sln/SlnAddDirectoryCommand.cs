using System.ComponentModel;
using Dotnet.CLI.Plus.Solution;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Dotnet.CLI.Plus.Commands.Sln;

public sealed class SlnAddDirectoryCommand : AsyncCommand<SlnAddDirectoryCommand.Settings>
{
    public sealed class Settings : SlnRootCommand.Settings
    {
        [CommandArgument(0, "[path]")]
        [Description(
            """
                Directory to add to the solution. Nested paths create nested solution folders.
                """
        )]
        public string PathToAdd { get; init; } = string.Empty;

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(PathToAdd))
            {
                return ValidationResult.Error("Please specify a directory to add to the solution.");
            }

            return base.Validate();
        }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var result = await LegacySolutionCompatibilityEditor.AddDirectoryAsync(
            settings.SolutionPath,
            settings.PathToAdd,
            cancellationToken
        );

        if (result.Message is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Message.Value)}[/]");
        }

        return result.ExitCode;
    }
}
