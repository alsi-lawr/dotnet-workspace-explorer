namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Xml

module internal DirectCommandRunner =
    let private completed command revision output =
        Ok
            { CommandId = command
              Revision = revision
              Output = output }

    let private failed command (failure: WorkspaceFailure) =
        Error
            { CommandId = command
              Diagnostic = failure.Diagnostic }

    let private executeCore arguments cancellationToken =
        task {
            let _, parsed = DirectCommandParser.parse arguments

            match parsed with
            | Error failure -> return failed "" failure
            | Ok(LaunchProfile(target, operation, name, projects)) ->
                let! launchProfileResult =
                    CommandLineSolutionLaunchProfiles.execute
                        target
                        operation
                        name
                        projects
                        cancellationToken

                return
                    match launchProfileResult with
                    | Error failure -> failed "solution.launch" failure
                    | Ok(output, revision) -> completed "solution.launch" revision (Some output)
            | Ok(ImportDirectory(solution, directory)) ->
                let! imported = LegacyDirectoryImport.import solution directory cancellationToken

                match imported with
                | Error failure -> return failed "solution.directory" failure
                | Ok() ->
                    let! verified =
                        LegacyDirectoryImport.verify solution directory cancellationToken

                    return
                        match verified with
                        | Error failure -> failed "solution.directory" failure
                        | Ok revision -> completed "solution.directory" revision None
        }

    let ExecuteAsync (arguments: string array, cancellationToken: CancellationToken) =
        task {
            try
                return! executeCore arguments cancellationToken
            with
            | :? OperationCanceledException -> return failed "" (DirectCommandFailures.cancelled ())
            | :? XmlException
            | :? JsonException
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException ->
                return
                    failed
                        ""
                        (DirectCommandFailures.invalid "The command target is invalid or malformed.")
            | :? IOException
            | :? UnauthorizedAccessException ->
                return
                    failed
                        ""
                        (DirectCommandFailures.internalFailure
                            "The command target could not be read.")
            | _ ->
                return
                    failed
                        ""
                        (DirectCommandFailures.internalFailure
                            "The Workspace Explorer command line encountered an internal failure.")
        }
