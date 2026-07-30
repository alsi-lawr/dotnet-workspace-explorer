namespace Dotnet.WorkspaceExplorer

#nowarn "3511"

open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open WorkspaceCommandEditing
open WorkspaceCommandArguments

module internal SolutionLaunchProfileRequests =
    let private operationContext (context: WorkspaceCommandContext) workspace =
        { Workspace = workspace
          State = context.State
          Watcher = context.Watcher
          Coordinator = context.Coordinator
          PublicationGate = context.PublicationGate
          ActiveOperations = context.ActiveOperations
          WorkspaceRoot = context.WorkspaceRoot
          MaximumFrameBytes = context.MaximumFrameBytes
          RebuildWatcher = context.RebuildWatcher
          MutationNotifications = context.MutationNotifications }

    let private executeProfileMutation
        (context: WorkspaceCommandContext)
        (workspace: SolutionWorkspace)
        (request: CommandMutationRequest)
        confirmationToken
        cancellationToken
        =
        task {
            match confirmationToken with
            | None ->
                return
                    Error(
                        RpcErrors.invalidParams
                            "workspace/commands/execute requires confirmationToken."
                    )
            | Some preview ->
                let! planned = planMutation workspace context.State request cancellationToken

                match planned with
                | Failure failure -> return Error(WorkspaceRpcResponses.failureError failure)
                | Success(LaunchProfilePlan _ as plan) ->
                    return!
                        WorkspaceEditOperation.Start(
                            operationContext context workspace,
                            plan,
                            preview,
                            "Updating launch profile.",
                            (fun () -> SolutionLaunchProfileCommands.verify workspace request),
                            cancellationToken
                        )
                | Success _ -> return Error RpcErrors.internalError
        }

    let tryExecute
        (context: WorkspaceCommandContext)
        (workspace: SolutionWorkspace)
        (target: WorkspaceNodeId option)
        (descriptor: CommandDescriptor)
        arguments
        expectedRevision
        confirmationToken
        (cancellationToken: CancellationToken)
        =
        task {
            let profile =
                SolutionLaunchProfileCommands.tryDescribe descriptor.Id |> Option.isSome

            if not profile then
                return None
            elif descriptor.Access = CommandAccess.Write && context.State.Descriptor.IsReadOnly then
                return
                    Some(Error(RpcErrors.unsupported "The selected .slnf workspace is read-only."))
            elif
                SolutionLaunchProfileCommands.discover workspace target
                |> Seq.exists (fun candidate -> candidate.Id = descriptor.Id)
                |> not
            then
                return
                    Some(
                        Error(
                            RpcErrors.create
                                "not_found"
                                "The command is not applicable to the target."
                                None
                        )
                    )
            elif context.State.Revision <> expectedRevision then
                return Some(Error(WorkspaceRpcResponses.workspaceConflict context.State.Revision))
            else
                match commandArguments workspace descriptor arguments with
                | Error rpcError -> return Some(Error rpcError)
                | Ok parsed ->
                    let request =
                        { CommandId = descriptor.Id
                          TargetWorkspaceNodeId = target
                          Arguments = parsed
                          ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                    if descriptor.Access = CommandAccess.Read then
                        match SolutionLaunchProfileCommands.argv workspace request with
                        | Error message -> return Some(Error(RpcErrors.invalidParams message))
                        | Ok argv ->
                            let! result =
                                DotnetCommandOperation.start
                                    (operationContext context workspace)
                                    descriptor
                                    request
                                    None
                                    None
                                    argv
                                    None
                                    cancellationToken

                            return Some result
                    else
                        let! result =
                            executeProfileMutation
                                context
                                workspace
                                request
                                confirmationToken
                                cancellationToken

                        return Some result
        }
