namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex

module internal ProjectEditing =
    let all =
        Seq.append ProjectItemCommands.all ProjectFolderCommands.all
        |> System.Collections.Immutable.ImmutableArray.CreateRange

    let tryDescribe id =
        ProjectItemCommands.tryDescribe id
        |> Option.orElseWith (fun () -> ProjectFolderCommands.tryDescribe id)

    let discover (workspace: SolutionWorkspace) targetNodeId =
        Seq.append
            (ProjectItemCommands.discover workspace targetNodeId)
            (ProjectFolderCommands.discover workspace targetNodeId)
        |> System.Collections.Immutable.ImmutableArray.CreateRange

    let readDocument path =
        MsBuildProjectDocument.readDocument path

    let saveDocument document encoding hasPreamble lineEnding =
        MsBuildProjectDocument.saveDocument document encoding hasPreamble lineEnding

    let plan
        (workspace: SolutionWorkspace)
        project
        snapshot
        (command: CommandMutationRequest)
        cancellationToken
        =
        match ProjectFolderCommands.tryDescribe command.CommandId with
        | Some _ ->
            ProjectFolderEditPlanning.plan workspace project snapshot command cancellationToken
        | None -> ProjectItemEditPlanning.plan workspace project snapshot command cancellationToken
