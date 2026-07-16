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
        var parseResult = await SolutionParser.ParseAsync(settings.SolutionPath, cancellationToken);

        return await parseResult.Match(
            solution => AddDirectoryAsync(solution, settings.PathToAdd, cancellationToken),
            error => Task.FromResult(error.DisplayCliInfo())
        );
    }

    private static async Task<int> AddDirectoryAsync(
        SolutionDocument solution,
        string targetPath,
        CancellationToken cancellationToken
    )
    {
        var folderPathResult = SolutionFolderPath.FromDirectory(solution.FilePath, targetPath);

        return await folderPathResult.Match(
            async folderPath =>
            {
                solution.Model.AddFolder(folderPath.Value);
                var saveResult = await solution.SaveAsync(cancellationToken);
                return saveResult.Match(_ => 0, error => error.DisplayCliInfo());
            },
            error => Task.FromResult(error.DisplayCliInfo())
        );
    }
}
