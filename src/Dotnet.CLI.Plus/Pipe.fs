namespace Dotnet.CLI.Plus

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.Globalization
open System.IO
open System.Threading
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport

module internal Pipe =
    [<RequireQualifiedAccess>]
    type Invocation =
        | NotPipeRelated
        | InvalidPipeStartup
        | ValidPipeStartup of target: string * exportCapacity: int

    let private openWorkspace target cancellationToken =
        task {
            let! outcome = SolutionStore.OpenAsync(target, cancellationToken)

            return
                match outcome with
                | Success workspace -> Ok workspace
                | Failure failure -> Error(PublicProtocol.failureError failure)
        }

    let private reservedStartupToken (argument: string) =
        argument = "--pipe"
        || argument = "--export-workers"
        || argument.StartsWith("--pipe=", StringComparison.Ordinal)
        || argument.StartsWith("--export-workers=", StringComparison.Ordinal)

    let parseInvocation (arguments: string array) =
        match arguments with
        | [| "solution" | "sln"; target; "--pipe" |] -> Invocation.ValidPipeStartup(target, 3)
        | [| "solution" | "sln"; target; "--pipe"; "--export-workers"; value |] ->
            match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, capacity when capacity > 0 -> Invocation.ValidPipeStartup(target, capacity)
            | _ -> Invocation.InvalidPipeStartup
        | _ when arguments |> Array.exists reservedStartupToken -> Invocation.InvalidPipeStartup
        | _ -> Invocation.NotPipeRelated

    let runAsync
        (target: string)
        (exportCapacity: int)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            let! opened = openWorkspace target cancellationToken

            match opened with
            | Error rpcError ->
                do! error.WriteLineAsync $"dotnet-plus pipe startup failure: {rpcError.Message}"
                do! error.FlushAsync()
                return 64
            | Ok workspace ->
                let state = WorkspaceState.CreateProduction(target, workspace, exportCapacity)
                let mutable watcherStarted = false
                let mutable maximumFrameBytes = RpcCodec.secureLimits.MaximumValueBytes
                let mutable maximumPageSize = 256
                use publicationGate = new SemaphoreSlim(1, 1)

                let watcher =
                    WorkspaceWatcher(state, 128, (fun () -> maximumFrameBytes), publicationGate)

                let coordinator =
                    let root =
                        Path.GetDirectoryName workspace.BackingPath.Value
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    MutationCoordinator.CreateProduction(
                        WorkspaceArtifactPath.Create root,
                        fun () -> WorkspaceRevision.Create state.Revision
                    )

                let workspaceRoot =
                    Path.GetDirectoryName workspace.BackingPath.Value
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory())

                let activeOperations =
                    ConcurrentDictionary<string, ExportOperationState> StringComparer.Ordinal

                let startWatcher active =
                    if watcherStarted then
                        None
                    elif not active then
                        None
                    else
                        watcherStarted <- true
                        Some(fun sink token -> watcher.StartAsync(sink, token))

                let workspaceRequestContext =
                    { State = state
                      Watcher = watcher
                      ActiveOperations = activeOperations
                      MaximumFrameBytes = fun () -> maximumFrameBytes
                      MaximumPageSize = fun () -> maximumPageSize
                      StartWatcher = startWatcher }

                let commandRequestContext =
                    { State = state
                      Watcher = watcher
                      Coordinator = coordinator
                      PublicationGate = publicationGate
                      ActiveOperations = activeOperations
                      WorkspaceRoot = workspaceRoot
                      MaximumFrameBytes = fun () -> maximumFrameBytes
                      RebuildWatcher = PipeWorkspaceRequests.rebuildWatcher workspaceRequestContext
                      MutationNotifications = PipeWorkspaceRequests.mutationNotifications }

                let initialize parameters _ =
                    task {
                        match PublicProtocol.parseInitialize parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            maximumFrameBytes <- request.MaximumFrameBytes
                            maximumPageSize <- request.MaximumPageSize

                            return
                                Ok(
                                    PublicProtocol.initializeResult
                                        state.Descriptor
                                        state.Revision
                                        request
                                )
                    }

                let dispatchCore
                    (_: RpcSessionContext)
                    methodName
                    parameters
                    requestCancellationToken
                    =
                    task {
                        match PublicProtocol.parseRequest methodName parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            let! workspaceResult =
                                PipeWorkspaceRequests.tryDispatch
                                    workspaceRequestContext
                                    request
                                    requestCancellationToken

                            match workspaceResult with
                            | Some result -> return result
                            | None ->
                                match request with
                                | PublicRequest.CommandList _
                                | PublicRequest.CommandDescribe _
                                | PublicRequest.CommandPreview _
                                | PublicRequest.CommandExecute _ ->
                                    return!
                                        PipeCommandRequests.dispatch
                                            commandRequestContext
                                            request
                                            requestCancellationToken
                                | _ -> return Error RpcErrors.internalError
                    }

                let dispatch
                    (context: RpcSessionContext)
                    (methodName: string)
                    (parameters: RpcValue)
                    (requestCancellationToken: CancellationToken)
                    =
                    task {
                        let serialized =
                            match PublicProtocol.parseRequest methodName parameters with
                            | Ok PublicRequest.Root
                            | Ok(PublicRequest.Children _)
                            | Ok(PublicRequest.Refresh _)
                            | Ok(PublicRequest.CommandList _)
                            | Ok(PublicRequest.CommandDescribe _)
                            | Ok(PublicRequest.CommandPreview _)
                            | Ok(PublicRequest.CommandExecute _) -> true
                            | _ -> false

                        let mutable releaseOnExit = false

                        try
                            if serialized then
                                do! publicationGate.WaitAsync requestCancellationToken
                                releaseOnExit <- true

                            let! result =
                                dispatchCore context methodName parameters requestCancellationToken

                            return
                                match result with
                                | Ok value when serialized ->
                                    let release () = publicationGate.Release() |> ignore
                                    releaseOnExit <- false

                                    Ok
                                        { value with
                                            AfterResponse = Some release }
                                | _ -> result
                        finally
                            if releaseOnExit then
                                publicationGate.Release() |> ignore
                    }

                let configuration =
                    { Profile = RpcProfile.publicProfile
                      Limits = RpcCodec.secureLimits
                      GetOutboundFrameLimit = fun () -> maximumFrameBytes
                      Initialize = initialize
                      Dispatch = dispatch }

                try
                    let! result =
                        RpcSession.runAsync configuration input output error cancellationToken

                    do! state.DisposeAsync()
                    return result
                with exceptionValue ->
                    do! state.DisposeAsync()
                    return raise exceptionValue
        }
