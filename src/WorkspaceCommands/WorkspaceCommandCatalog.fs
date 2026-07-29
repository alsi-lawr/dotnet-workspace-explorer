namespace Dotnet.WorkspaceExplorer.WorkspaceCommands

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

open System

type internal PlannedWorkspaceCommand =
    | SolutionPlan of PlannedSolutionEdit
    | ProjectPlan of PlannedProjectEdit
    | CompositePlan of PlannedProjectEdit
    | DotnetCommandPlan of WorkspaceEditPreviewRequest * WorkspaceArtifactPath array
    | LaunchProfilePlan of WorkspaceEditPreviewRequest * WorkspaceEditAction * WorkspaceArtifactPath

[<RequireQualifiedAccess>]
module internal WorkspaceCommandCatalog =
    let tryDescribe commandId =
        try
            let id = CommandId.Create commandId

            SolutionEditor.TryDescribe id
            |> Option.orElseWith (fun () -> ProjectEditing.tryDescribeItem id)
            |> Option.orElseWith (fun () -> ProjectRelocation.tryDescribe id)
            |> Option.orElseWith (fun () -> ProjectEditing.tryDescribeProperty id)
            |> Option.orElseWith (fun () -> ProjectEditing.tryDescribeFolder id)
            |> Option.orElseWith (fun () -> DotnetCommandCatalog.tryDescribe id)
            |> Option.orElseWith (fun () -> DotnetLifecycleCommands.tryDescribe id)
            |> Option.orElseWith (fun () -> SolutionLaunchProfileCommands.tryDescribe id)
        with :? ArgumentException as error ->
            raise (ArgumentException(error.Message, "commandId"))
