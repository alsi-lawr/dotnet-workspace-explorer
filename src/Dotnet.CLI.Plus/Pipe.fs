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

module internal Pipe =
    let private map values = RpcValue.map values
    let private text value = RpcValue.String value
    let private integer value = RpcValue.Integer value
    let private boolean value = RpcValue.Boolean value

    let private requireEmpty parameters =
        let fields = RpcValue.requireMap "params" parameters

        if fields.Count <> 0 then
            invalidArg "params" "This method does not accept parameters."

    let private requestRevision fields =
        match RpcValue.optionalField "expectedRevision" fields with
        | None -> None
        | Some value -> Some(RpcValue.requireInteger "expectedRevision" value)

    let private openWorkspace target cancellationToken =
        task {
            let! outcome = SolutionStore.OpenAsync(target, cancellationToken)

            return
                match outcome with
                | WorkspaceOutcome.Success workspace -> Ok workspace
                | WorkspaceOutcome.Failure failure -> Error(PublicProtocol.failureError failure)
        }

    let private projectionSignature (workspace: SolutionWorkspace) =
        let builder = StringBuilder()

        let append (values: seq<string>) =
            for value in values do
                builder.AppendLine value |> ignore

        let root = workspace.RootProjection

        append (
            root.Nodes
            |> Seq.map (fun node ->
                $"node|{node.NodeId.Value}|{node.NodeKind}|{node.Identity.Value}|{node.Name}|{node.NodeLoadState}|{String.Join(',', node.AvailableCapabilities |> Seq.map _.Value)}")
        )

        append (
            root.Folders
            |> Seq.map (fun folder -> $"folder|{folder.Node.NodeId.Value}|{folder.Path}|{folder.ParentPath}")
        )

        append (
            root.Items
            |> Seq.map (fun item -> $"item|{item.Node.NodeId.Value}|{item.FolderPath}|{item.RelativePath}")
        )

        for project in root.Projects do
            builder.AppendLine(
                $"project|{project.Node.NodeId.Value}|{project.Path.AbsolutePath.Value}|{project.Path.SolutionRelativePath}|{project.Path.IsExternal}|{project.ParentFolderPath}|{project.IsFilteredOut}"
            )
            |> ignore

            append (
                project.ConfigurationRules
                |> Seq.map (fun rule ->
                    $"rule|{rule.SolutionBuildType}|{rule.SolutionPlatform}|{rule.Dimension}|{rule.ProjectValue}")
            )

            append (
                project.ConfigurationMappings
                |> Seq.map (fun mapping ->
                    $"mapping|{mapping.SolutionBuildType}|{mapping.SolutionPlatform}|{mapping.ProjectBuildType}|{mapping.ProjectPlatform}|{mapping.Builds}|{mapping.Deploys}")
            )

        append (
            root.Dependencies
            |> Seq.map (fun dependency ->
                $"dependency|{dependency.Node.NodeId.Value}|{dependency.ProjectId.Value}|{dependency.DependsOnProjectId.Value}")
        )

        builder.ToString()

    let private chunkNotification workspaceId operationId sequence revision nodes last =
        Notification(
            "workspace/exportChunk",
            map
                [ "workspaceId", text workspaceId
                  "operationId", text operationId
                  "sequence", integer (int64 sequence)
                  "revision", integer revision
                  "nodes", RpcValue.array nodes
                  "last", boolean last
                  "diagnostics", RpcValue.array [] ]
        )

    let private chunkNodes maximumFrameBytes workspaceId operationId revision (nodes: RpcValue array) =
        let chunks = ResizeArray<RpcValue array>()
        let current = ResizeArray<RpcValue>()

        let encodedSize candidate =
            chunkNotification workspaceId operationId chunks.Count revision candidate false
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

                if encodedSize [| node |] > maximumFrameBytes then
                    invalidOp "A workspace node exceeds the negotiated outbound frame limit."

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
                let mutable signature = projectionSignature initialWorkspace
                let mutable revision = workspace.WorkspaceDescriptor.WorkspaceRevision.Value
                let mutable maximumFrameBytes = RpcCodec.secureLimits.MaximumValueBytes

                let activeExports =
                    ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal)

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
                        try
                            match methodName with
                            | "workspace/root" ->
                                requireEmpty parameters
                                let descriptor = workspace.WorkspaceDescriptor

                                return
                                    Ok
                                        { Result =
                                            map
                                                [ "revision", integer revision
                                                  "nodes",
                                                  workspace.RootProjection.Nodes
                                                  |> Seq.map (PublicProtocol.node descriptor.WorkspaceId revision)
                                                  |> RpcValue.array ]
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = None
                                          StopAfterResponse = false }
                            | "workspace/refresh" ->
                                let fields = RpcValue.requireMap "params" parameters
                                RpcValue.ensureOnly "params" [ "expectedRevision" ] fields

                                match requestRevision fields with
                                | Some expected when expected <> revision ->
                                    return
                                        Error(
                                            RpcErrors.create
                                                "workspace_conflict"
                                                "The expected workspace revision is stale."
                                                (Some(map [ "actualRevision", integer revision ]))
                                        )
                                | _ ->
                                    let! reopened = openWorkspace target requestCancellationToken

                                    match reopened with
                                    | Error rpcError -> return Error rpcError
                                    | Ok next ->
                                        let nextSignature = projectionSignature next

                                        let changed =
                                            not (String.Equals(signature, nextSignature, StringComparison.Ordinal))

                                        if changed then
                                            workspace <- next
                                            signature <- nextSignature
                                            revision <- revision + 1L

                                        return
                                            Ok
                                                { Result =
                                                    map
                                                        [ "revision", integer revision
                                                          "reset", boolean changed
                                                          "diagnostics", RpcValue.array [] ]
                                                  Notifications = []
                                                  BackgroundWork = None
                                                  AfterResponse = None
                                                  StopAfterResponse = false }
                            | "workspace/export" ->
                                requireEmpty parameters
                                let snapshot = workspace
                                let snapshotRevision = revision
                                let descriptor = snapshot.WorkspaceDescriptor
                                let workspaceId = descriptor.WorkspaceId.Value
                                let operationId = Guid.NewGuid().ToString("N")

                                let source =
                                    CancellationTokenSource.CreateLinkedTokenSource requestCancellationToken

                                if not (activeExports.TryAdd(operationId, source)) then
                                    source.Dispose()
                                    return Error RpcErrors.internalError
                                else
                                    let nodeValues =
                                        snapshot.RootProjection.Nodes
                                        |> Seq.map (PublicProtocol.node descriptor.WorkspaceId snapshotRevision)
                                        |> Seq.toArray

                                    let background (sink: RpcNotificationSink) sessionToken =
                                        task {
                                            let mutable sequence = 0
                                            let mutable outcome = "succeeded"
                                            let diagnostics = ResizeArray<RpcValue>()

                                            use linked =
                                                CancellationTokenSource.CreateLinkedTokenSource(
                                                    source.Token,
                                                    sessionToken
                                                )

                                            try
                                                do! Task.Delay(250, linked.Token)

                                                let chunks =
                                                    chunkNodes
                                                        maximumFrameBytes
                                                        workspaceId
                                                        operationId
                                                        snapshotRevision
                                                        nodeValues

                                                for index in 0 .. chunks.Length - 1 do
                                                    linked.Token.ThrowIfCancellationRequested()

                                                    do!
                                                        sink.WriteAsync(
                                                            chunkNotification
                                                                workspaceId
                                                                operationId
                                                                sequence
                                                                snapshotRevision
                                                                chunks[index]
                                                                (index = chunks.Length - 1)
                                                        )

                                                    sequence <- sequence + 1
                                            with
                                            | :? OperationCanceledException ->
                                                outcome <- "cancelled"

                                                diagnostics.Add(
                                                    PublicProtocol.simpleDiagnostic
                                                        descriptor.WorkspaceId
                                                        snapshotRevision
                                                        "cancelled"
                                                        "The workspace export was cancelled."
                                                )
                                            | _ ->
                                                outcome <- "failed"

                                                diagnostics.Add(
                                                    PublicProtocol.simpleDiagnostic
                                                        descriptor.WorkspaceId
                                                        snapshotRevision
                                                        "export_failed"
                                                        "The workspace export failed safely."
                                                )

                                            activeExports.TryRemove operationId |> ignore
                                            source.Dispose()

                                            do!
                                                sink.WriteAsync(
                                                    Notification(
                                                        "operation/completed",
                                                        map
                                                            [ "workspaceId", text workspaceId
                                                              "operationId", text operationId
                                                              "sequence", integer (int64 sequence)
                                                              "revision", integer snapshotRevision
                                                              "outcome", text outcome
                                                              "diagnostics", RpcValue.array diagnostics ]
                                                    )
                                                )
                                        }

                                    return
                                        Ok
                                            { Result =
                                                map
                                                    [ "operationId", text operationId
                                                      "revision", integer snapshotRevision ]
                                              Notifications = []
                                              BackgroundWork = Some background
                                              AfterResponse = None
                                              StopAfterResponse = false }
                            | "operation/cancel" ->
                                let fields = RpcValue.requireMap "params" parameters
                                RpcValue.ensureOnly "params" [ "operationId" ] fields

                                let operationId =
                                    fields
                                    |> RpcValue.requireField "operationId"
                                    |> RpcValue.requireString "operationId"

                                let accepted =
                                    match activeExports.TryGetValue operationId with
                                    | true, source when not source.IsCancellationRequested -> true
                                    | _ -> false

                                let cancelAfterResponse =
                                    if accepted then
                                        Some(fun () ->
                                            match activeExports.TryGetValue operationId with
                                            | true, source when not source.IsCancellationRequested -> source.Cancel()
                                            | _ -> ())
                                    else
                                        None

                                return
                                    Ok
                                        { Result = map [ "accepted", boolean accepted ]
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = cancelAfterResponse
                                          StopAfterResponse = false }
                            | "shutdown" ->
                                requireEmpty parameters

                                for source in activeExports.Values do
                                    source.Cancel()

                                return
                                    Ok
                                        { Result = map [ "accepted", boolean true ]
                                          Notifications = []
                                          BackgroundWork = None
                                          AfterResponse = None
                                          StopAfterResponse = true }
                            | "workspace/children" ->
                                let fields = RpcValue.requireMap "params" parameters

                                RpcValue.ensureOnly "params" [ "parentId"; "pageSize"; "continuationToken" ] fields

                                fields
                                |> RpcValue.requireField "parentId"
                                |> RpcValue.requireString "parentId"
                                |> ignore

                                RpcValue.optionalField "pageSize" fields
                                |> Option.iter (fun value ->
                                    let pageSize = RpcValue.requireInteger "pageSize" value

                                    if pageSize <= 0L || pageSize > 1000L then
                                        invalidArg "pageSize" "Page size must be between 1 and 1000.")

                                RpcValue.optionalField "continuationToken" fields
                                |> Option.iter (RpcValue.requireString "continuationToken" >> ignore)

                                return
                                    Error(RpcErrors.unsupported "Workspace children are not implemented until T-006.")
                            | "command/list" ->
                                let fields = RpcValue.requireMap "params" parameters
                                RpcValue.ensureOnly "params" [ "targetId" ] fields

                                RpcValue.optionalField "targetId" fields
                                |> Option.iter (RpcValue.requireString "targetId" >> ignore)

                                return Error(RpcErrors.unsupported "Command discovery is not implemented until T-007.")
                            | "command/describe" ->
                                let fields = RpcValue.requireMap "params" parameters
                                RpcValue.ensureOnly "params" [ "commandId"; "targetId" ] fields

                                fields
                                |> RpcValue.requireField "commandId"
                                |> RpcValue.requireString "commandId"
                                |> ignore

                                RpcValue.optionalField "targetId" fields
                                |> Option.iter (RpcValue.requireString "targetId" >> ignore)

                                return Error(RpcErrors.unsupported "Command discovery is not implemented until T-007.")
                            | "command/preview"
                            | "command/execute" ->
                                let fields = RpcValue.requireMap "params" parameters

                                let allowed =
                                    if methodName = "command/execute" then
                                        [ "commandId"; "targetId"; "arguments"; "expectedRevision"; "previewId" ]
                                    else
                                        [ "commandId"; "targetId"; "arguments"; "expectedRevision" ]

                                RpcValue.ensureOnly "params" allowed fields

                                fields
                                |> RpcValue.requireField "commandId"
                                |> RpcValue.requireString "commandId"
                                |> ignore

                                RpcValue.optionalField "targetId" fields
                                |> Option.iter (RpcValue.requireString "targetId" >> ignore)

                                fields
                                |> RpcValue.requireField "arguments"
                                |> RpcValue.requireMap "arguments"
                                |> ignore

                                fields
                                |> RpcValue.requireField "expectedRevision"
                                |> RpcValue.requireInteger "expectedRevision"
                                |> ignore

                                RpcValue.optionalField "previewId" fields
                                |> Option.iter (RpcValue.requireString "previewId" >> ignore)

                                if workspace.WorkspaceDescriptor.IsReadOnly then
                                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                                else
                                    return
                                        Error(
                                            RpcErrors.unsupported "Workspace mutations are not implemented until T-007."
                                        )
                            | _ -> return Error(RpcErrors.unknownMethod methodName)
                        with
                        | :? ArgumentException as error -> return Error(RpcErrors.invalidParams error.Message)
                        | :? OperationCanceledException -> return raise (OperationCanceledException())
                        | _ -> return Error RpcErrors.internalError
                    }

                let configuration =
                    { Profile = RpcProfile.publicProfile
                      Limits = RpcCodec.secureLimits
                      Initialize = initialize
                      Dispatch = dispatch }

                return! RpcSession.runAsync configuration input output error cancellationToken
        }
