namespace Dotnet.WorkspaceExplorer.Solutions

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Immutable
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open SolutionTargetResolution

module internal SolutionFilterReader =
    type internal SolutionFilterDefinition =
        { BackingSolutionPath: string
          IncludedProjectPaths: ImmutableArray<string> option }

    let readFilter filterPath cancellationToken =
        task {
            try
                throwIfCancellationRequested cancellationToken
                use stream = File.OpenRead filterPath

                use! document =
                    JsonDocument.ParseAsync(stream, cancellationToken = cancellationToken)

                let root = document.RootElement
                let mutable solution = Unchecked.defaultof<JsonElement>
                let mutable path = Unchecked.defaultof<JsonElement>

                if
                    root.ValueKind <> JsonValueKind.Object
                    || not (root.TryGetProperty("solution", &solution))
                    || solution.ValueKind <> JsonValueKind.Object
                    || not (solution.TryGetProperty("path", &path))
                    || path.ValueKind <> JsonValueKind.String
                    || String.IsNullOrWhiteSpace(path.GetString())
                then
                    return invalidInput "filter" "The solution filter must declare solution.path."
                else
                    let filterDirectory =
                        System.IO.Path.GetDirectoryName filterPath
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    let backingPath =
                        path.GetString()
                        |> Option.ofObj
                        |> Option.map (fun value ->
                            System.IO.Path.GetFullPath(value, filterDirectory))
                        |> Option.defaultWith (fun () ->
                            invalidArg "filter" "A filter solution path is required.")

                    match resolveBackingSolution backingPath cancellationToken with
                    | Failure failure -> return Failure failure
                    | Success resolvedBacking ->
                        let mutable projects = Unchecked.defaultof<JsonElement>

                        if not (solution.TryGetProperty("projects", &projects)) then
                            return
                                Success
                                    { BackingSolutionPath = resolvedBacking
                                      IncludedProjectPaths = Some(ImmutableArray<string>.Empty) }
                        elif projects.ValueKind <> JsonValueKind.Array then
                            return
                                invalidInput
                                    "filter"
                                    "The solution filter projects value must be an array of paths."
                        else
                            let values = projects.EnumerateArray() |> Seq.toArray

                            if
                                values
                                |> Array.exists (fun project ->
                                    project.ValueKind <> JsonValueKind.String)
                            then
                                return
                                    invalidInput
                                        "filter"
                                        "The solution filter projects value must be an array of paths."
                            else
                                let includedPaths =
                                    values
                                    |> Seq.choose (fun project ->
                                        throwIfCancellationRequested cancellationToken

                                        project.GetString()
                                        |> Option.ofObj
                                        |> Option.map (fun value ->
                                            let backingDirectory =
                                                System.IO.Path.GetDirectoryName resolvedBacking
                                                |> Option.ofObj
                                                |> Option.defaultValue (
                                                    Directory.GetCurrentDirectory()
                                                )

                                            System.IO.Path.GetFullPath(value, backingDirectory)))
                                    |> Seq.toArray

                                return
                                    Success
                                        { BackingSolutionPath = resolvedBacking
                                          IncludedProjectPaths =
                                            Some(ImmutableArray.CreateRange includedPaths) }
            with
            | :? OperationCanceledException -> return cancelled ()
            | :? JsonException ->
                return invalidInput "filter" "The solution filter is malformed JSON."
            | :? PathTooLongException ->
                return invalidInput "filter" "The solution filter contains an invalid path."
            | :? IOException ->
                return
                    internalFailure
                        "solution.filter_read_failed"
                        "Failed to read the solution filter."
            | :? UnauthorizedAccessException ->
                return
                    internalFailure
                        "solution.filter_read_failed"
                        "Failed to read the solution filter."
            | :? ArgumentException
            | :? NotSupportedException ->
                return invalidInput "filter" "The solution filter contains an invalid path."
        }
