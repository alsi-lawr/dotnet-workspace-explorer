namespace Dotnet.CLI.Plus

module internal ProjectMutations =
    let all =
        Seq.append ProjectMutationCommands.all ProjectFolderCommands.all
        |> System.Collections.Immutable.ImmutableArray.CreateRange

    let tryDescribe id =
        ProjectMutationCommands.tryDescribe id
        |> Option.orElseWith (fun () -> ProjectFolderCommands.tryDescribe id)

    let discover (workspace: Solution.SolutionWorkspace) targetId =
        Seq.append
            (ProjectMutationCommands.discover workspace targetId)
            (ProjectFolderCommands.discover workspace targetId)
        |> System.Collections.Immutable.ImmutableArray.CreateRange

    let readDocument path = ProjectXml.readDocument path

    let saveDocument document encoding hasPreamble lineEnding =
        ProjectXml.saveDocument document encoding hasPreamble lineEnding

    let plan
        (workspace: Solution.SolutionWorkspace)
        project
        snapshot
        (command: Core.CommandMutationRequest)
        cancellationToken
        =
        match ProjectFolderCommands.tryDescribe command.CommandId with
        | Some _ -> ProjectFolderPlanning.plan workspace project snapshot command cancellationToken
        | None -> ProjectMutationPlanning.plan workspace project snapshot command cancellationToken
