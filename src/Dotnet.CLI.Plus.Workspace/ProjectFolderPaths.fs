namespace Dotnet.CLI.Plus

open System
open System.IO
open Dotnet.CLI.Plus.Core

module internal ProjectFolderPaths =
    let normalizedRelative projectDirectory path =
        Path.GetRelativePath(projectDirectory, path)
        |> fun value ->
            value
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')

    let isProjectLocal projectDirectory path =
        let relative = Path.GetRelativePath(projectDirectory, path)

        not (Path.IsPathRooted relative)
        && relative <> ".."
        && not (relative.StartsWith $"..{Path.DirectorySeparatorChar}")
        && not (relative.StartsWith $"..{Path.AltDirectorySeparatorChar}")

    let generated projectDirectory path =
        let relative = normalizedRelative projectDirectory path

        relative.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
        || relative.Equals(".generated", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(".generated/", StringComparison.OrdinalIgnoreCase)
        || relative.Contains("/.generated/", StringComparison.OrdinalIgnoreCase)

    let canonicalDirectory projectDirectory value =
        let path = Path.GetFullPath(value, projectDirectory)

        match MutationFiles.canonicalNoFollow false path with
        | Error message -> Error message
        | Ok canonical when not (Directory.Exists canonical) -> Error "The folder does not exist."
        | Ok canonical when not (isProjectLocal projectDirectory canonical) ->
            Error "The folder must stay within the project directory."
        | Ok canonical when generated projectDirectory canonical ->
            Error "Generated folders are read-only."
        | Ok canonical -> Ok canonical

    let canonicalNewDirectory projectDirectory value =
        let path = Path.GetFullPath(value, projectDirectory)

        match MutationFiles.canonicalNoFollow false path with
        | Error message -> Error message
        | Ok canonical when not (isProjectLocal projectDirectory canonical) ->
            Error "The destination folder must stay within the project directory."
        | Ok canonical when generated projectDirectory canonical ->
            Error "Generated folders are read-only."
        | Ok canonical when MutationFiles.exists canonical ->
            Error "The destination folder already exists."
        | Ok canonical when not (Directory.Exists(Path.GetDirectoryName canonical)) ->
            Error "The destination parent folder does not exist."
        | Ok canonical -> Ok canonical

    let canonicalExternalDirectory projectDirectory value =
        let path = Path.GetFullPath(value, projectDirectory)

        match MutationFiles.canonicalNoFollow false path with
        | Error message -> Error message
        | Ok canonical when not (Directory.Exists canonical) ->
            Error "The source folder does not exist."
        | Ok canonical when isProjectLocal projectDirectory canonical ->
            Error "The source folder must be external to the project."
        | Ok canonical -> Ok canonical

    let enumerateNoFollow path =
        let rec visit current =
            if MutationFiles.isLink current then
                Error "Symbolic links and reparse points are not supported for folder commands."
            elif File.Exists current then
                Ok [ current ]
            elif Directory.Exists current then
                Directory.EnumerateFileSystemEntries current
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> Seq.fold
                    (fun state child ->
                        state
                        |> Result.bind (fun entries ->
                            visit child |> Result.map (List.append entries)))
                    (Ok [])
            else
                Error "A folder entry disappeared during preflight."

        visit path

    let completeTree path =
        enumerateNoFollow path
        |> Result.bind (fun entries ->
            MutationFiles.fingerprint path
            |> Result.map (fun fingerprint -> entries, fingerprint))

    let validateDestinationTree projectDirectory source destination =
        completeTree source
        |> Result.bind (fun (entries, fingerprint) ->
            let root = Path.GetFullPath source

            entries
            |> List.fold
                (fun state entry ->
                    state
                    |> Result.bind (fun () ->
                        let relative = Path.GetRelativePath(root, entry)
                        let mapped = Path.Combine(destination, relative)

                        match MutationFiles.canonicalNoFollow false mapped with
                        | Error message -> Error message
                        | Ok canonical when generated projectDirectory canonical ->
                            Error "Generated folders are read-only."
                        | Ok canonical when MutationFiles.exists canonical ->
                            Error "A destination folder entry already exists."
                        | Ok _ -> Ok()))
                (Ok())
            |> Result.map (fun () -> fingerprint))
