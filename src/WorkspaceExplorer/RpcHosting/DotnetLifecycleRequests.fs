namespace Dotnet.WorkspaceExplorer

#nowarn "3511"

open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open WorkspaceCommandArguments

module internal DotnetLifecycleRequests =
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

    let tryExecute
        (context: WorkspaceCommandContext)
        (workspace: SolutionWorkspace)
        (target: WorkspaceNodeId option)
        (descriptor: CommandDescriptor)
        arguments
        expectedRevision
        (_confirmationToken: string option)
        (cancellationToken: CancellationToken)
        =
        task {
            let lifecycle = DotnetLifecycleCommands.tryDescribe descriptor.Id |> Option.isSome

            if not lifecycle then
                return None
            elif
                DotnetLifecycleCommands.discover workspace target
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

                    match DotnetLifecycleCommands.argv workspace request with
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
        }
