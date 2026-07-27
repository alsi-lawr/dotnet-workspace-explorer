namespace Dotnet.CLI.Plus

#nowarn "3511"

open System.Threading
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport
open CanonicalMutationPlanning
open PipeCommandProtocol

module internal PipeLifecycleRequests =
    let private operationContext (context: CommandRequestContext) workspace =
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

    let private mutationRequest (descriptor: CommandDescriptor) target arguments expectedRevision =
        { CommandId = descriptor.CommandId
          TargetId = target
          Arguments = arguments
          ExpectedRevision = WorkspaceRevision.Create expectedRevision }

    let private executeOperation
        context
        workspace
        descriptor
        request
        plan
        previewId
        argv
        cancellationToken
        =
        CanonicalCommandOperation.start
            (operationContext context workspace)
            descriptor
            request
            plan
            previewId
            argv
            cancellationToken

    let private executeProfileMutation
        (context: CommandRequestContext)
        (workspace: SolutionWorkspace)
        (request: CommandMutationRequest)
        previewId
        cancellationToken
        =
        task {
            match previewId with
            | None -> return Error(RpcErrors.invalidParams "command/execute requires previewId.")
            | Some preview ->
                let! planned = planMutation workspace context.State request cancellationToken

                match planned with
                | Failure failure -> return Error(PublicProtocol.failureError failure)
                | Success(LaunchProfilePlan _ as plan) ->
                    return!
                        PlannedMutationOperation.Start(
                            operationContext context workspace,
                            plan,
                            preview,
                            "Updating launch profile.",
                            (fun () -> LaunchProfileCommandPlanning.verify workspace request),
                            cancellationToken
                        )
                | Success _ -> return Error RpcErrors.internalError
        }

    let tryExecute
        (context: CommandRequestContext)
        (workspace: SolutionWorkspace)
        (target: NodeId option)
        (descriptor: CommandDescriptor)
        arguments
        expectedRevision
        previewId
        (cancellationToken: CancellationToken)
        =
        task {
            let lifecycle = LifecycleCommands.tryDescribe descriptor.CommandId |> Option.isSome

            let profile =
                LaunchProfileCommandPlanning.tryDescribe descriptor.CommandId |> Option.isSome

            let applicable =
                if lifecycle then
                    LifecycleCommands.discover workspace target
                    |> Seq.exists (fun candidate -> candidate.CommandId = descriptor.CommandId)
                else
                    LaunchProfileCommandPlanning.discover workspace target
                    |> Seq.exists (fun candidate -> candidate.CommandId = descriptor.CommandId)

            if not lifecycle && not profile then
                return None
            elif
                profile
                && descriptor.CommandAccess = CommandAccess.Write
                && context.State.Descriptor.IsReadOnly
            then
                return
                    Some(Error(RpcErrors.unsupported "The selected .slnf workspace is read-only."))
            elif not applicable then
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
                return Some(Error(PublicProtocol.workspaceConflict context.State.Revision))
            else
                match commandArguments workspace descriptor arguments with
                | Error rpcError -> return Some(Error rpcError)
                | Ok parsed ->
                    let request = mutationRequest descriptor target parsed expectedRevision

                    if lifecycle then
                        match LifecycleCommands.argv workspace request with
                        | Error message -> return Some(Error(RpcErrors.invalidParams message))
                        | Ok argv ->
                            let! result =
                                executeOperation
                                    context
                                    workspace
                                    descriptor
                                    request
                                    None
                                    None
                                    argv
                                    cancellationToken

                            return Some result
                    elif descriptor.CommandAccess = CommandAccess.Read then
                        match LaunchProfileCommandPlanning.argv workspace request with
                        | Error message -> return Some(Error(RpcErrors.invalidParams message))
                        | Ok argv ->
                            let! result =
                                executeOperation
                                    context
                                    workspace
                                    descriptor
                                    request
                                    None
                                    None
                                    argv
                                    cancellationToken

                            return Some result
                    else
                        let! result =
                            executeProfileMutation
                                context
                                workspace
                                request
                                previewId
                                cancellationToken

                        return Some result
        }
