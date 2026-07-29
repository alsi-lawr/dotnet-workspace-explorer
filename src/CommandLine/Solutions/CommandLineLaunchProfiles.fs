namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

open System.Threading

module internal CommandLineSolutionLaunchProfiles =
    let private profileNames workspace =
        SolutionLaunchProfiles.names workspace
        |> Result.mapError DirectCommandFailures.invalid
        |> Result.map (fun names ->
            let output = String.concat "\n" names
            (if output = "" then "" else output + "\n"), None)

    let private update workspace name projects =
        SolutionLaunchProfiles.set workspace name projects
        |> Result.mapError DirectCommandFailures.invalid
        |> Result.map (fun () -> "", Some workspace.Descriptor.Revision)

    let private remove workspace name =
        SolutionLaunchProfiles.remove workspace name
        |> Result.mapError DirectCommandFailures.invalid
        |> Result.map (fun () -> "", Some workspace.Descriptor.Revision)

    let execute target operation name projects (cancellationToken: CancellationToken) =
        task {
            let! opened = SolutionWorkspaceReader.OpenAsync(target, cancellationToken)

            match opened with
            | Failure failure -> return Error failure
            | Success workspace ->
                match operation, name with
                | LaunchList, _ -> return profileNames workspace
                | LaunchSet, Some profile -> return update workspace profile projects
                | LaunchRemove, Some profile -> return remove workspace profile
                | _ ->
                    return Error(DirectCommandFailures.invalid "Launch profile name is required.")
        }
