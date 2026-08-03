namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.IO
open Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
type PackageStartupFailure =
    | InvalidInvocation of detail: string
    | TargetNotFound of target: string
    | UnsupportedTarget of target: string
    | AmbiguousWorkspace of directory: string * candidates: NonEmptyList<string>

    member this.Code =
        match this with
        | PackageStartupFailure.InvalidInvocation _ -> "DWE-PACKAGES-INVALID-INVOCATION"
        | PackageStartupFailure.TargetNotFound _ -> "DWE-PACKAGES-TARGET-NOT-FOUND"
        | PackageStartupFailure.UnsupportedTarget _ -> "DWE-PACKAGES-UNSUPPORTED-TARGET"
        | PackageStartupFailure.AmbiguousWorkspace _ -> "DWE-PACKAGES-AMBIGUOUS-TARGET"

    member this.Message =
        match this with
        | PackageStartupFailure.InvalidInvocation detail -> detail
        | PackageStartupFailure.TargetNotFound target ->
            $"The package explorer target was not found: {target}"
        | PackageStartupFailure.UnsupportedTarget target ->
            $"The package explorer does not support this target: {target}"
        | PackageStartupFailure.AmbiguousWorkspace(directory, candidates) ->
            let names = candidates |> NonEmptyList.toList |> String.concat ", "
            $"More than one package explorer target was found in {directory}: {names}"

[<RequireQualifiedAccess>]
type PackageStartup =
    | NotPackageRoute
    | Invalid of PackageStartupFailure
    | Terminal of PackageWorkspaceTarget
    | Pipe of PackageWorkspaceTarget

[<RequireQualifiedAccess>]
module PackageStartup =
    let private directCommands = set [ "add"; "remove"; "update"; "list"; "search" ]

    let private invalid detail =
        PackageStartup.Invalid(PackageStartupFailure.InvalidInvocation detail)

    let private classifyExistingTarget currentDirectory target =
        try
            let baseDirectory = Path.GetFullPath currentDirectory
            let path = Path.GetFullPath(target, baseDirectory)

            if File.Exists path then
                match PackageWorkspaceTarget.file path with
                | Ok workspaceTarget -> Ok workspaceTarget
                | Error _ -> Error(PackageStartupFailure.UnsupportedTarget path)
            elif Directory.Exists path then
                PackageWorkspaceTarget.directory path
                |> Result.mapError (fun _ -> PackageStartupFailure.UnsupportedTarget path)
            else
                Error(PackageStartupFailure.TargetNotFound path)
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException -> Error(PackageStartupFailure.UnsupportedTarget target)

    let private eligibleFile path =
        match PackageWorkspaceTarget.file path with
        | Ok target -> Some target
        | Error _ -> None

    let private resolveCurrentDirectory currentDirectory =
        try
            let directory = Path.GetFullPath currentDirectory

            if not (Directory.Exists directory) then
                Error(PackageStartupFailure.TargetNotFound directory)
            else
                let candidates =
                    Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    |> Seq.choose eligibleFile
                    |> Seq.sortWith (fun left right ->
                        StringComparer.Ordinal.Compare(
                            PackageWorkspaceTarget.path left,
                            PackageWorkspaceTarget.path right
                        ))
                    |> Seq.toList

                match candidates with
                | [ target ] -> Ok target
                | [] -> Error(PackageStartupFailure.TargetNotFound directory)
                | first :: rest ->
                    let paths =
                        NonEmptyList.create first rest
                        |> NonEmptyList.map PackageWorkspaceTarget.path

                    Error(PackageStartupFailure.AmbiguousWorkspace(directory, paths))
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException
        | :? IOException
        | :? UnauthorizedAccessException ->
            Error(PackageStartupFailure.UnsupportedTarget currentDirectory)

    let private select startup result =
        match result with
        | Ok target -> startup target
        | Error failure -> PackageStartup.Invalid failure

    let resolve currentDirectory (arguments: string array) =
        match arguments |> Array.toList with
        | "packages" :: [] ->
            resolveCurrentDirectory currentDirectory |> select PackageStartup.Terminal
        | "packages" :: "--pipe" :: [] ->
            invalid "Package pipe startup requires exactly one target before --pipe."
        | "packages" :: command :: _ when directCommands.Contains command ->
            invalid
                "Direct package commands are not supported here. Use the standard dotnet package command."
        | "packages" :: target :: [] when target <> "--pipe" ->
            classifyExistingTarget currentDirectory target |> select PackageStartup.Terminal
        | "packages" :: target :: "--pipe" :: [] when target <> "--pipe" ->
            classifyExistingTarget currentDirectory target |> select PackageStartup.Pipe
        | "packages" :: _ -> invalid "Use packages [TARGET] or packages <TARGET> --pipe."
        | _ -> PackageStartup.NotPackageRoute
