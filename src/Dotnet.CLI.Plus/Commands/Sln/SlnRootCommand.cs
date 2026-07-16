using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Dotnet.CLI.Plus.Commands.Sln;

public class SlnRootCommand : Command<SlnRootCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<PATH>")]
        [Description("Path to a .sln or .slnx file. Directory paths are also supported.")]
        public string SolutionPath { get; init; } = string.Empty;

        public override ValidationResult Validate()
        {
            if (!Path.Exists(SolutionPath))
            {
                return ValidationResult.Error("Solution path is invalid.");
            }

            return base.Validate();
        }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    ) => 0;
}
