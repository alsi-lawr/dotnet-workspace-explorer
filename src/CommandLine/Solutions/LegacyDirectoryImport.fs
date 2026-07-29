namespace Dotnet.WorkspaceExplorer.CommandLine

#nowarn "3261"

open System
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

module internal LegacyDirectoryImport =
    let verify
        (solutionPath: string)
        (directoryPath: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let solutionDirectory =
                Path.GetDirectoryName solutionPath
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let relativePath =
                let fullPath = Path.GetFullPath(directoryPath, solutionDirectory)

                Path.GetRelativePath(solutionDirectory, fullPath)
                |> fun path -> path.Replace('\\', '/').Trim '/'

            let expectedPath = $"/{relativePath}/"
            let! opened = SolutionWorkspaceReader.OpenAsync(solutionPath, cancellationToken)

            return
                match opened with
                | Failure failure -> Error failure
                | Success workspace when
                    workspace.Contents.Folders
                    |> Seq.exists (fun folder ->
                        String.Equals(folder.Path, expectedPath, StringComparison.Ordinal))
                    ->
                    Ok(Some workspace.Descriptor.Revision)
                | Success _ ->
                    Error(
                        DirectCommandFailures.verification
                            "The imported solution folder was not present after the command."
                    )
        }

    let import
        (solutionPath: string)
        (directoryPath: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let! opened = SolutionWorkspaceReader.OpenAsync(solutionPath, cancellationToken)

            match opened with
            | Failure failure -> return Error failure
            | Success workspace when workspace.Descriptor.IsReadOnly ->
                return
                    Error(
                        DirectCommandFailures.unsupported
                            ".slnf workspaces are read-only and cannot be mutated."
                    )
            | Success workspace ->
                let solutionDirectory =
                    Path.GetDirectoryName workspace.SolutionPath.Value
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory())

                let command =
                    { CommandId = CommandId.Create "solution.folder.import-directory"
                      TargetWorkspaceNodeId = None
                      Arguments =
                        CommandArguments.Create
                            [ { ParameterId = CommandParameterId.Create "path"
                                Value =
                                  Path(
                                      WorkspaceArtifactPath.Create(
                                          Path.GetFullPath(directoryPath, solutionDirectory)
                                      )
                                  ) } ]
                      ExpectedRevision = workspace.Descriptor.Revision }

                let! planned = SolutionEditor.PlanAsync(workspace, command, cancellationToken)

                match planned with
                | Failure failure -> return Error failure
                | Success plan ->
                    let actions =
                        seq {
                            match plan.FileRename with
                            | Some rename ->
                                yield
                                    WorkspaceEditAction.Rename(
                                        rename.Source.Value,
                                        rename.Destination.Value
                                    )
                            | None -> ()

                            yield
                                WorkspaceEditAction.ReplaceFile(
                                    plan.BackingPath.Value,
                                    plan.Contents
                                )
                        }

                    let coordinator =
                        WorkspaceEditTransaction.CreateProduction(
                            WorkspaceArtifactPath.Create solutionDirectory,
                            fun () -> workspace.Descriptor.Revision
                        )

                    match coordinator.Prepare(plan.Request, actions) with
                    | Failure failure -> return Error failure
                    | Success preview ->
                        match
                            coordinator.Execute(
                                plan.Request,
                                actions,
                                preview.Confirmation,
                                cancellationToken
                            )
                        with
                        | Failure failure -> return Error failure
                        | Success Applied -> return Ok()
                        | Success(RolledBack failure) -> return Error failure
        }
