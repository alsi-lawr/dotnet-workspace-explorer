namespace Dotnet.CLI.Plus.Solution

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Microsoft.VisualStudio.SolutionPersistence
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer

type internal LegacyCompatibilityResult =
    { ExitCode: int
      Message: string option }

module private LegacyCompatibilityEditorImplementation =
    let success = { ExitCode = 0; Message = None }

    let failure message =
        { ExitCode = 1; Message = Some message }

    let resolveSolution (targetPath: string) =
        match SolutionStoreImplementation.resolveTarget targetPath CancellationToken.None with
        | WorkspaceOutcome.Failure _ -> Error "Solution file could not be resolved."
        | WorkspaceOutcome.Success path when SolutionStoreImplementation.isSolution path -> Ok path
        | WorkspaceOutcome.Success _ -> Error "Expected a .sln or .slnx solution file."

    let folderPath (solutionPath: string) (targetPath: string) =
        try
            let solutionDirectory =
                Path.GetDirectoryName solutionPath
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let targetDirectory = Path.GetFullPath(targetPath, solutionDirectory)

            if not (Directory.Exists targetDirectory) then
                Error "Directory to add was not found."
            else
                let relativePath = Path.GetRelativePath(solutionDirectory, targetDirectory)

                if
                    Path.IsPathRooted relativePath
                    || relativePath = ".."
                    || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                then
                    Error "Directory to add must be inside the solution directory."
                elif relativePath = "." then
                    Error "The solution root cannot be added as a folder."
                else
                    let segments =
                        relativePath.Split(
                            [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                            StringSplitOptions.RemoveEmptyEntries
                        )

                    if segments.Length = 0 then
                        Error "Directory path is invalid."
                    else
                        Ok $"/{String.Join('/', segments)}/"
        with
        | :? ArgumentException -> Error "Directory path is invalid."
        | :? NotSupportedException -> Error "Directory path is invalid."
        | :? PathTooLongException -> Error "Directory path is invalid."

    let addDirectory targetPath directory cancellationToken =
        task {
            match resolveSolution targetPath with
            | Error message -> return failure message
            | Ok solutionPath ->
                match folderPath solutionPath directory with
                | Error message -> return failure message
                | Ok path ->
                    try
                        match SolutionSerializers.GetSerializerByMoniker solutionPath |> Option.ofObj with
                        | None -> return failure "Expected a .sln or .slnx solution file."
                        | Some serializer ->
                            let! model = serializer.OpenAsync(solutionPath, cancellationToken)
                            model.AddFolder path |> ignore
                            do! serializer.SaveAsync(solutionPath, model, cancellationToken)
                            return success
                    with
                    | :? OperationCanceledException -> return failure "Solution operation was cancelled."
                    | :? SolutionException -> return failure "Failed to save the solution file."
                    | :? IOException -> return failure "Failed to save the solution file."
                    | :? UnauthorizedAccessException -> return failure "Failed to save the solution file."
        }

/// Temporary compatibility seam for `sln <PATH> add directory`; T-007 replaces it with transactions.
type internal LegacySolutionCompatibilityEditor private () =
    static member AddDirectoryAsync
        (solutionPath: string, directoryPath: string, cancellationToken: CancellationToken)
        : Task<LegacyCompatibilityResult> =
        LegacyCompatibilityEditorImplementation.addDirectory solutionPath directoryPath cancellationToken
