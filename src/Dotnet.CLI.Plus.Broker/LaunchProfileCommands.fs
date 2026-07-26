namespace Dotnet.CLI.Plus

open System
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

module internal DirectLaunchProfileCommands =
    let private profileNames workspace =
        LaunchProfiles.names workspace
        |> Result.mapError BrokerFailure.invalid
        |> Result.map (fun names ->
            let output = String.concat "\n" names
            (if output = "" then "" else output + "\n"), None)

    let private update workspace name projects =
        LaunchProfiles.set workspace name projects
        |> Result.mapError BrokerFailure.invalid
        |> Result.map (fun () -> "", Some workspace.WorkspaceDescriptor.WorkspaceRevision)

    let private remove workspace name =
        LaunchProfiles.remove workspace name
        |> Result.mapError BrokerFailure.invalid
        |> Result.map (fun () -> "", Some workspace.WorkspaceDescriptor.WorkspaceRevision)

    let execute target operation name projects (cancellationToken: CancellationToken) =
        task {
            let! opened = SolutionStore.OpenAsync(target, cancellationToken)

            match opened with
            | Failure failure -> return Error failure
            | Success workspace ->
                match operation, name with
                | LaunchList, _ -> return profileNames workspace
                | LaunchSet, Some profile -> return update workspace profile projects
                | LaunchRemove, Some profile -> return remove workspace profile
                | _ -> return Error(BrokerFailure.invalid "Launch profile name is required.")
        }
