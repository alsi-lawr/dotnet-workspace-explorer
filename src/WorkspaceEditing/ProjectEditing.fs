namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

module internal ProjectEditing =
    let tryDescribeItem id = ProjectItemCommands.tryDescribe id
    let tryDescribeProperty id = ProjectPropertyCommands.tryDescribe id
    let tryDescribeFolder id = ProjectFolderCommands.tryDescribe id

    let discoverItems workspace targetNodeId =
        ProjectItemCommands.discover workspace targetNodeId

    let discoverProperties workspace targetNodeId =
        ProjectPropertyCommands.discover workspace targetNodeId

    let discoverFolders workspace targetNodeId =
        ProjectFolderCommands.discover workspace targetNodeId

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
        match ProjectPropertyCommands.tryDescribe command.CommandId with
        | Some _ ->
            ProjectPropertyEditPlanning.plan workspace project snapshot command cancellationToken
        | None ->
            match ProjectFolderCommands.tryDescribe command.CommandId with
            | Some _ ->
                ProjectFolderEditPlanning.plan workspace project snapshot command cancellationToken
            | None ->
                ProjectItemEditPlanning.plan workspace project snapshot command cancellationToken
