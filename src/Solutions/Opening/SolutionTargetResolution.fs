namespace Dotnet.WorkspaceExplorer.Solutions

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces

module internal SolutionTargetResolution =
    let diagnostic code message retryable =
        WorkspaceDiagnostic.Create(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            None,
            None,
            retryable,
            CorrelationId.New()
        )

    let invalidInput input message =
        Failure(InvalidInput(input, diagnostic "solution.invalid_input" message false))

    let notFound target message =
        Failure(NotFound(target, diagnostic "solution.not_found" message false))

    let ambiguous target message =
        Failure(AmbiguousTarget(target, diagnostic "solution.ambiguous" message false))

    let cancelled () =
        Failure(
            Cancelled(
                WorkspaceOperationId.New(),
                diagnostic "solution.cancelled" "Solution operation was cancelled." true
            )
        )

    let internalFailure code message =
        Failure(Internal(diagnostic code message true))

    let text (value: obj) =
        match value with
        | null -> String.Empty
        | :? string as result -> result
        | _ -> invalidArg (nameof value) "Expected a string value."

    let throwIfCancellationRequested (cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()

    let comparer semantics : StringComparer =
        match semantics with
        | FileSystemCaseSensitivity.Insensitive -> StringComparer.OrdinalIgnoreCase
        | _ -> StringComparer.Ordinal

    let pathIdentity semantics (path: string) =
        match semantics with
        | FileSystemCaseSensitivity.Insensitive -> path.ToUpperInvariant()
        | _ -> path

    let includedProjects semantics paths =
        paths
        |> Option.map (fun values ->
            ImmutableHashSet.CreateRange<string>(comparer semantics, values))

    let orderBy cancellationToken key values =
        throwIfCancellationRequested cancellationToken

        let ordered =
            values
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(key left, key right))
            |> Seq.toArray

        throwIfCancellationRequested cancellationToken
        ordered

    let isExtension extension (path: string) =
        String.Equals(
            System.IO.Path.GetExtension path,
            extension,
            StringComparison.OrdinalIgnoreCase
        )

    let isSolution path =
        isExtension ".sln" path || isExtension ".slnx" path

    let isCandidate path =
        isSolution path || isExtension ".slnf" path

    let format path =
        if isExtension ".sln" path then
            WorkspaceFormat.Sln
        elif isExtension ".slnx" path then
            WorkspaceFormat.Slnx
        elif isExtension ".slnf" path then
            WorkspaceFormat.Slnf
        else
            invalidArg (nameof path) "Expected a supported solution path."

    let resolveCandidates cancellationToken predicate candidates =
        let matches = ResizeArray<string>()

        for candidate in candidates do
            throwIfCancellationRequested cancellationToken

            if predicate candidate then
                matches.Add(System.IO.Path.GetFullPath candidate)

        matches
        |> orderBy cancellationToken id
        |> Seq.truncate 2
        |> Seq.toArray
        |> function
            | [||] -> notFound "solution" "No solution or filter file was found."
            | [| path |] -> Success path
            | _ -> ambiguous "solution" "Multiple solution or filter files were found."

    let resolveTarget targetPath cancellationToken =
        throwIfCancellationRequested cancellationToken

        if String.IsNullOrWhiteSpace targetPath then
            invalidInput "targetPath" "A solution path is required."
        else
            try
                if Directory.Exists targetPath then
                    Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories)
                    |> resolveCandidates cancellationToken isCandidate
                else
                    let path = System.IO.Path.GetFullPath targetPath

                    if not (File.Exists path) then
                        notFound path "The solution or filter file was not found."
                    elif isCandidate path then
                        Success path
                    else
                        invalidInput "targetPath" "Expected a .sln, .slnx, or .slnf file."
            with
            | :? PathTooLongException -> invalidInput "targetPath" "The solution path is invalid."
            | :? IOException ->
                internalFailure "solution.resolve_failed" "Failed to resolve the solution path."
            | :? UnauthorizedAccessException ->
                internalFailure "solution.resolve_failed" "Failed to resolve the solution path."
            | :? ArgumentException
            | :? NotSupportedException -> invalidInput "targetPath" "The solution path is invalid."

    let resolveBackingSolution backingPath cancellationToken =
        throwIfCancellationRequested cancellationToken

        if Directory.Exists backingPath then
            Directory.EnumerateFiles(backingPath, "*", SearchOption.AllDirectories)
            |> resolveCandidates cancellationToken isSolution
        elif not (File.Exists backingPath) then
            notFound backingPath "The filter backing solution was not found."
        elif isSolution backingPath then
            Success(System.IO.Path.GetFullPath backingPath)
        else
            invalidInput "solution" "The filter backing solution must be a .sln or .slnx file."
