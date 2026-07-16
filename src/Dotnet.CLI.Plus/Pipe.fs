namespace Dotnet.CLI.Plus

#nowarn "3511"

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport

module private ProjectionFingerprint =
    let private writeString (writer: BinaryWriter) (value: string) =
        let bytes = Encoding.UTF8.GetBytes value
        writer.Write bytes.Length
        writer.Write bytes

    let private writeOption (writer: BinaryWriter) value =
        match value with
        | Some text ->
            writer.Write true
            writeString writer text
        | None -> writer.Write false

    let private writeNode (writer: BinaryWriter) (node: WorkspaceNode) =
        writeString writer node.NodeId.Value
        writer.Write(int node.NodeKind)
        writeString writer node.Identity.Value
        writeString writer node.Name
        writer.Write(int node.NodeLoadState)
        writer.Write node.AvailableCapabilities.Length

        for capability in node.AvailableCapabilities do
            writeString writer capability.Value

    let create (workspace: SolutionWorkspace) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        let root = workspace.RootProjection
        writer.Write root.Nodes.Length

        for node in root.Nodes do
            writeNode writer node

        writer.Write root.Folders.Length

        for folder in root.Folders do
            writeNode writer folder.Node
            writeString writer folder.Path
            writeOption writer folder.ParentPath

        writer.Write root.Items.Length

        for item in root.Items do
            writeNode writer item.Node
            writeOption writer item.FolderPath
            writeString writer item.RelativePath

        writer.Write root.Projects.Length

        for project in root.Projects do
            writeNode writer project.Node
            writeString writer project.Path.AbsolutePath.Value
            writeString writer project.Path.SolutionRelativePath
            writer.Write project.Path.IsExternal
            writeOption writer project.ParentFolderPath
            writer.Write project.IsFilteredOut
            writer.Write project.ConfigurationRules.Length

            for rule in project.ConfigurationRules do
                writeString writer rule.SolutionBuildType
                writeString writer rule.SolutionPlatform
                writeString writer rule.Dimension
                writeString writer rule.ProjectValue

            writer.Write project.ConfigurationMappings.Length

            for mapping in project.ConfigurationMappings do
                writeString writer mapping.SolutionBuildType
                writeString writer mapping.SolutionPlatform
                writeString writer mapping.ProjectBuildType
                writeString writer mapping.ProjectPlatform
                writer.Write mapping.Builds
                writer.Write mapping.Deploys

        writer.Write root.Dependencies.Length

        for dependency in root.Dependencies do
            writeNode writer dependency.Node
            writeString writer dependency.ProjectId.Value
            writeString writer dependency.DependsOnProjectId.Value

        writer.Flush()
        stream.ToArray()

    let changed (left: byte array) (right: byte array) =
        not (left.AsSpan().SequenceEqual(right))

    let canonicalStrings (groups: seq<seq<string>>) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        let values = groups |> Seq.map Seq.toArray |> Seq.toArray
        writer.Write values.Length

        for group in values do
            writer.Write group.Length

            for value in group do
                writeString writer value

        writer.Flush()
        stream.ToArray()

type internal ExportOperationState(sessionToken: CancellationToken) =
    let cancellation = CancellationTokenSource.CreateLinkedTokenSource sessionToken

    let cancellationResponseFlushed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable state = 0 // 0 running, 1 cancellation reserved, 2 success reserved, 3 complete
    let mutable cancellationCommitted = 0

    let cancelAndRelease () =
        if Interlocked.CompareExchange(&cancellationCommitted, 1, 0) = 0 then
            try
                cancellation.Cancel()
            finally
                cancellationResponseFlushed.TrySetResult() |> ignore

    member _.Token = cancellation.Token
    member _.IsCancellationReserved = Volatile.Read(&state) = 1

    member _.TryReserveCancellation() =
        Interlocked.CompareExchange(&state, 1, 0) = 0

    member _.TryReserveCompletion() =
        Interlocked.CompareExchange(&state, 2, 0) = 0

    member _.WaitForCancellationResponseAsync() = cancellationResponseFlushed.Task

    member _.CommitCancellationAfterResponse() = cancelAndRelease ()

    member _.CancelForShutdown() =
        if Interlocked.CompareExchange(&state, 1, 0) = 0 || Volatile.Read(&state) = 1 then
            cancelAndRelease ()

    member _.Complete() =
        Volatile.Write(&state, 3)
        cancellation.Dispose()

module internal PipeTestHooks =
    let canonicalSignature groups =
        ProjectionFingerprint.canonicalStrings groups

    let nextRevision revision before after =
        if ProjectionFingerprint.changed before after then
            revision + 1L
        else
            revision

module internal Pipe =
    let private openWorkspace target cancellationToken =
        task {
            let! outcome = SolutionStore.OpenAsync(target, cancellationToken)

            return
                match outcome with
                | WorkspaceOutcome.Success workspace -> Ok workspace
                | WorkspaceOutcome.Failure failure -> Error(PublicProtocol.failureError failure)
        }

    let private chunkNodes
        maximumFrameBytes
        (descriptor: WorkspaceDescriptor)
        operationId
        revision
        (nodes: WorkspaceNode array)
        =
        let chunks = ResizeArray<WorkspaceNode array>()
        let current = ResizeArray<WorkspaceNode>()

        let encodedSize candidate =
            PublicProtocol.exportChunk descriptor operationId chunks.Count revision candidate false
            |> RpcCodec.encodeFrame
            |> _.Length

        let flush () =
            if current.Count > 0 then
                chunks.Add(current.ToArray())
                current.Clear()

        for node in nodes do
            let candidate = Array.append (current.ToArray()) [| node |]

            if encodedSize candidate <= maximumFrameBytes then
                current.Add node
            else
                flush ()

                let actual = encodedSize [| node |]

                if actual > maximumFrameBytes then
                    raise (RpcOutboundFrameTooLargeException(maximumFrameBytes, actual))

                current.Add node

        flush ()

        if chunks.Count = 0 then
            chunks.Add Array.empty

        chunks.ToArray()

    let isPipeInvocation (arguments: string array) =
        match arguments with
        | [| ("solution" | "sln"); target; "--pipe" |] -> Some target
        | _ -> None

    let runAsync
        (target: string)
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            let! opened = openWorkspace target cancellationToken

            match opened with
            | Error rpcError ->
                do! error.WriteLineAsync($"dotnet-plus pipe startup failure: {rpcError.Message}")
                do! error.FlushAsync()
                return 64
            | Ok initialWorkspace ->
                let mutable workspace = initialWorkspace
                let mutable fingerprint = ProjectionFingerprint.create initialWorkspace
                let mutable revision = workspace.WorkspaceDescriptor.WorkspaceRevision.Value
                let mutable maximumFrameBytes = RpcCodec.secureLimits.MaximumValueBytes

                let activeExports =
                    ConcurrentDictionary<string, ExportOperationState>(StringComparer.Ordinal)

                let initialize parameters _ =
                    task {
                        match PublicProtocol.parseInitialize parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            maximumFrameBytes <- request.MaximumFrameBytes
                            return Ok(PublicProtocol.initializeResult workspace.WorkspaceDescriptor revision request)
                    }

                let dispatch (_: RpcSessionContext) methodName parameters requestCancellationToken =
                    task {
                        match PublicProtocol.parseRequest methodName parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            match request with
                            | PublicRequest.Root ->
                                return
                                    Ok
                                        { Result =
                                            PublicProtocol.rootResult
                                                workspace.WorkspaceDescriptor
                                                revision
                                                workspace.RootProjection.Nodes
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = None
                                          StopAfterResponse = false }
                            | PublicRequest.Refresh expectedRevision ->
                                match expectedRevision with
                                | Some expected when expected <> revision ->
                                    return Error(PublicProtocol.workspaceConflict revision)
                                | _ ->
                                    let! reopened = openWorkspace target requestCancellationToken

                                    match reopened with
                                    | Error rpcError -> return Error rpcError
                                    | Ok next ->
                                        let nextFingerprint = ProjectionFingerprint.create next

                                        let nextRevision =
                                            PipeTestHooks.nextRevision revision fingerprint nextFingerprint

                                        let changed = nextRevision <> revision

                                        if changed then
                                            workspace <- next
                                            fingerprint <- nextFingerprint
                                            revision <- nextRevision

                                        return
                                            Ok
                                                { Result = PublicProtocol.refreshResult revision changed
                                                  Notifications = []
                                                  BackgroundWork = None
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                            | PublicRequest.Export ->
                                let snapshot = workspace
                                let snapshotRevision = revision
                                let descriptor = snapshot.WorkspaceDescriptor
                                let operationId = Guid.NewGuid().ToString("N")
                                let operation = ExportOperationState(requestCancellationToken)

                                if not (activeExports.TryAdd(operationId, operation)) then
                                    operation.Complete()
                                    return Error RpcErrors.internalError
                                else
                                    let background (sink: RpcNotificationSink) sessionToken =
                                        task {
                                            let mutable sequence = 0
                                            let mutable outcome = PublicOperationOutcome.Succeeded

                                            let reserveFailure failure =
                                                task {
                                                    if operation.TryReserveCompletion() then
                                                        outcome <- failure
                                                    else
                                                        do! operation.WaitForCancellationResponseAsync()
                                                        outcome <- PublicOperationOutcome.Cancelled
                                                }

                                            try
                                                try
                                                    use linked =
                                                        CancellationTokenSource.CreateLinkedTokenSource(
                                                            operation.Token,
                                                            sessionToken
                                                        )

                                                    let chunks =
                                                        chunkNodes
                                                            maximumFrameBytes
                                                            descriptor
                                                            operationId
                                                            snapshotRevision
                                                            (snapshot.RootProjection.Nodes |> Seq.toArray)

                                                    for index in 0 .. chunks.Length - 1 do
                                                        if operation.IsCancellationReserved then
                                                            raise (OperationCanceledException())

                                                        linked.Token.ThrowIfCancellationRequested()

                                                        do!
                                                            sink.WriteAsync(
                                                                PublicProtocol.exportChunk
                                                                    descriptor
                                                                    operationId
                                                                    sequence
                                                                    snapshotRevision
                                                                    chunks[index]
                                                                    (index = chunks.Length - 1)
                                                            )

                                                        sequence <- sequence + 1

                                                    if operation.TryReserveCompletion() then
                                                        outcome <- PublicOperationOutcome.Succeeded
                                                    else
                                                        do! operation.WaitForCancellationResponseAsync()
                                                        outcome <- PublicOperationOutcome.Cancelled
                                                with
                                                | :? OperationCanceledException ->
                                                    if operation.IsCancellationReserved then
                                                        do! operation.WaitForCancellationResponseAsync()

                                                    outcome <- PublicOperationOutcome.Cancelled
                                                | :? RpcOutboundFrameTooLargeException ->
                                                    do!
                                                        reserveFailure (
                                                            PublicOperationOutcome.Failed(
                                                                "response_too_large",
                                                                "The workspace export exceeded the negotiated outbound frame limit."
                                                            )
                                                        )
                                                | :? InvalidOperationException ->
                                                    do!
                                                        reserveFailure (
                                                            PublicOperationOutcome.Failed(
                                                                "export_failed",
                                                                "The workspace export could not be framed safely."
                                                            )
                                                        )

                                                do!
                                                    sink.WriteAsync(
                                                        PublicProtocol.operationCompleted
                                                            descriptor
                                                            operationId
                                                            sequence
                                                            snapshotRevision
                                                            outcome
                                                    )
                                            finally
                                                activeExports.TryRemove operationId |> ignore
                                                operation.Complete()
                                        }

                                    return
                                        Ok
                                            { Result = PublicProtocol.exportResult operationId snapshotRevision
                                              Notifications = []
                                              BackgroundWork = Some background
                                              AfterResponse = None
                                              StopAfterResponse = false }
                            | PublicRequest.Cancel operationId ->
                                let accepted, afterResponse =
                                    match activeExports.TryGetValue operationId with
                                    | true, operation when operation.TryReserveCancellation() ->
                                        true, Some operation.CommitCancellationAfterResponse
                                    | _ -> false, None

                                return
                                    Ok
                                        { Result = PublicProtocol.cancelResult accepted
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = afterResponse
                                          StopAfterResponse = false }
                            | PublicRequest.Shutdown ->
                                for operation in activeExports.Values do
                                    operation.CancelForShutdown()

                                return
                                    Ok
                                        { Result = PublicProtocol.shutdownResult
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = None
                                          StopAfterResponse = true }
                            | PublicRequest.Children _ ->
                                return
                                    Error(RpcErrors.unsupported "Workspace children are not implemented until T-006.")
                            | PublicRequest.CommandList _
                            | PublicRequest.CommandDescribe _ ->
                                return Error(RpcErrors.unsupported "Command discovery is not implemented until T-007.")
                            | PublicRequest.CommandPreview _
                            | PublicRequest.CommandExecute _ ->
                                if workspace.WorkspaceDescriptor.IsReadOnly then
                                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                                else
                                    return
                                        Error(
                                            RpcErrors.unsupported "Workspace mutations are not implemented until T-007."
                                        )
                    }

                let configuration =
                    { Profile = RpcProfile.publicProfile
                      Limits = RpcCodec.secureLimits
                      GetOutboundFrameLimit = fun () -> maximumFrameBytes
                      Initialize = initialize
                      Dispatch = dispatch }

                return! RpcSession.runAsync configuration input output error cancellationToken
        }
