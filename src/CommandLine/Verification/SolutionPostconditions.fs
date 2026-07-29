namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

module internal SolutionPostconditions =
    let private openSolution target cancellationToken =
        task {
            let! outcome =
                SolutionWorkspaceReader.OpenAsync(
                    target |> Option.defaultValue (Directory.GetCurrentDirectory()),
                    cancellationToken
                )

            return
                match outcome with
                | Success workspace -> Ok workspace
                | Failure failure -> Error failure
        }

    let prepareSolution
        (target: string option)
        (operation: SolutionCommand)
        (operands: string list)
        cancellationToken
        =
        task {
            if
                (operation = Add || operation = Remove)
                && (List.isEmpty operands
                    || operands
                       |> List.exists (fun operand ->
                           operand.IndexOfAny [| '*'; '?' |] >= 0
                           && List.isEmpty (CommandTargetDiscovery.expandSolutionOperand operand)))
            then
                return
                    Error(
                        DirectCommandFailures.invalid
                            "Solution add/remove requires one or more matching project operands."
                    )
            else
                match target with
                | Some path when path.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase) ->
                    return
                        Error(
                            DirectCommandFailures.unsupported
                                ".slnf workspaces are read-only and cannot be mutated."
                        )
                | _ ->
                    let! workspace = openSolution target cancellationToken

                    return
                        match workspace with
                        | Error failure -> Error failure
                        | Ok workspace when workspace.Descriptor.IsReadOnly ->
                            Error(
                                DirectCommandFailures.unsupported
                                    ".slnf workspaces are read-only and cannot be mutated."
                            )
                        | Ok workspace -> Ok workspace
        }

    let private solutionProjects (workspace: SolutionWorkspace) =
        workspace.Contents.Projects
        |> Seq.map (fun project -> project.Node.Name, project.Path.AbsolutePath.Value)
        |> Seq.toList

    let private requestedSolutionOperands operands =
        let expanded = operands |> List.collect CommandTargetDiscovery.expandSolutionOperand

        if List.isEmpty operands || List.isEmpty expanded then
            Error "Solution add/remove requires at least one verifiable project operand."
        else
            Ok expanded

    let verifySolution target operation operands cancellationToken =
        task {
            let! opened = openSolution target cancellationToken

            match opened with
            | Error failure -> return Error failure
            | Ok workspace ->
                let pathComparer =
                    match
                        FileSystemCaseSensitivityDetector.DetectFromExistingPath
                            workspace.SolutionPath.Value
                    with
                    | FileSystemCaseSensitivity.Insensitive -> StringComparer.OrdinalIgnoreCase
                    | _ -> StringComparer.Ordinal

                match operation with
                | Some Add
                | Some Remove ->
                    match requestedSolutionOperands operands with
                    | Error message -> return Error(DirectCommandFailures.invalid message)
                    | Ok requested ->
                        let projects = solutionProjects workspace

                        let matches operand =
                            projects
                            |> List.exists (fun (name, path) ->
                                String.Equals(name, operand, StringComparison.OrdinalIgnoreCase)
                                || pathComparer.Equals(path, Path.GetFullPath operand))

                        let correct =
                            match operation with
                            | Some Add -> requested |> List.forall matches
                            | _ -> requested |> List.forall (matches >> not)

                        if correct then
                            return Ok(Some workspace.Descriptor.Revision)
                        else
                            return
                                Error(
                                    DirectCommandFailures.verification (
                                        "The refreshed solution does not contain the requested "
                                        + "final project state."
                                    )
                                )
                | Some Migrate ->
                    let migrated = Path.ChangeExtension(workspace.SolutionPath.Value, ".slnx")

                    if File.Exists migrated then
                        return Ok(Some workspace.Descriptor.Revision)
                    else
                        return
                            Error(
                                DirectCommandFailures.verification
                                    "The migrated .slnx file was not created."
                            )
                | _ -> return Ok(Some workspace.Descriptor.Revision)
        }
