namespace Dotnet.CLI.Plus

module internal ProjectMutations =
    let all = ProjectMutationCommands.all
    let tryDescribe id = ProjectMutationCommands.tryDescribe id

    let discover workspace targetId =
        ProjectMutationCommands.discover workspace targetId

    let readDocument path = ProjectXml.readDocument path

    let saveDocument document encoding hasPreamble lineEnding =
        ProjectXml.saveDocument document encoding hasPreamble lineEnding

    let plan workspace project snapshot command cancellationToken =
        ProjectMutationPlanning.plan workspace project snapshot command cancellationToken
