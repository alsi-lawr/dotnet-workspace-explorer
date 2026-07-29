namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

type internal PackageUpdateTarget =
    | ProjectTarget of string
    | FileTarget of string
    | SolutionTarget of string * string list

module internal PackageUpdateTargetResolver =
    let private differentPaths left right =
        not (String.Equals(left, right, StringComparison.Ordinal))

    let Resolve (project: string option, file: string option) =
        let selected =
            match project, file with
            | Some left, Some right when differentPaths left right ->
                Error(DirectCommandFailures.invalid "Package update target options conflict.")
            | Some path, _
            | _, Some path -> Ok path
            | None, None -> Ok(Directory.GetCurrentDirectory())

        match selected with
        | Error failure -> Error failure
        | Ok target when File.Exists target ->
            match Path.GetExtension(target).ToLowerInvariant() with
            | ".csproj"
            | ".fsproj"
            | ".vbproj" -> Ok(ProjectTarget target)
            | ".cs" -> Ok(FileTarget target)
            | ".sln"
            | ".slnx" -> Ok(SolutionTarget(target, []))
            | ".slnf" -> Error(DirectCommandFailures.unsupported ".slnf targets are read-only.")
            | _ -> Error(DirectCommandFailures.invalid "Package update target is unsupported.")
        | Ok target when Directory.Exists target ->
            let solutions =
                Directory.EnumerateFiles(target, "*.sln*", SearchOption.TopDirectoryOnly)
                |> Seq.toList

            let projects =
                Directory.EnumerateFiles(target, "*.*proj", SearchOption.TopDirectoryOnly)
                |> Seq.toList

            match solutions, projects with
            | [ solution ], _ -> Ok(SolutionTarget(solution, []))
            | [], [ project ] -> Ok(ProjectTarget project)
            | _ ->
                Error(
                    DirectCommandFailures.invalid "Package update target is missing or ambiguous."
                )
        | Ok _ -> Error(DirectCommandFailures.invalid "Package update target does not exist.")
