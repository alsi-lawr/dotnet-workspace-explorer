using System.Runtime.ExceptionServices;
using Dotnet.CLI.Plus.CommandContextAccessor;
using Dotnet.CLI.Plus.Common;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using static Dotnet.CLI.Plus.CommandContextAccessor.SolutionManipulationError;

namespace Dotnet.CLI.Plus.Solution;

public sealed class SolutionDocument(
    string filePath,
    ISolutionSerializer serializer,
    SolutionModel model
)
{
    public string FilePath { get; } = filePath;
    public SolutionModel Model { get; } = model;

    public async Task<Result<Unit, SolutionManipulationError>> SaveAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await serializer.SaveAsync(FilePath, Model, cancellationToken);
            return new Result<Unit, SolutionManipulationError>.Success(Unit.Value);
        }
        catch (SolutionException exception)
        {
            return WriteFailure(exception);
        }
        catch (IOException exception)
        {
            return WriteFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return WriteFailure(exception);
        }
    }

    private static Result<Unit, SolutionManipulationError> WriteFailure(Exception exception) =>
        new Result<Unit, SolutionManipulationError>.Failure(
            new SolutionManipulationError(
                ManipulationErrorType.WriteFailed,
                ExceptionDispatchInfo.Capture(exception)
            )
        );
}
