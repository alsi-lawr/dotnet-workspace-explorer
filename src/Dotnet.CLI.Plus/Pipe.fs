namespace Dotnet.CLI.Plus

open System
open System.Collections.Generic
open System.IO
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

    let private format =
        function
        | WorkspaceFormat.Sln -> "sln"
        | WorkspaceFormat.Slnx -> "slnx"
        | WorkspaceFormat.Slnf -> "slnf"
        | _ -> "unknown"

    let private nodeKind =
        function
        | WorkspaceNodeKind.Workspace -> "workspace"
        | WorkspaceNodeKind.SolutionFolder -> "solutionFolder"
        | WorkspaceNodeKind.Project -> "project"
        | WorkspaceNodeKind.ProjectItem -> "projectItem"
        | WorkspaceNodeKind.SolutionItem -> "solutionItem"
        | WorkspaceNodeKind.Configuration -> "configuration"
        | WorkspaceNodeKind.Platform -> "platform"
        | WorkspaceNodeKind.Placeholder -> "placeholder"
        | _ -> "unknown"

    let private loadState =
        function
        | WorkspaceNodeLoadState.Hydrated -> "hydrated"
        | WorkspaceNodeLoadState.Unhydrated -> "unhydrated"
        | WorkspaceNodeLoadState.FilteredOut -> "filteredOut"
        | _ -> "unknown"

    let private nodeValue (node: WorkspaceNode) =
        map
            [ "id", text node.NodeId.Value
              "kind", text (nodeKind node.NodeKind)
              "name", text node.Name
              "loadState", text (loadState node.NodeLoadState)
              "capabilities",
              node.AvailableCapabilities
              |> Seq.map (fun capability -> text capability.Value)
              |> RpcValue.array ]

    let private workspaceValue (workspace: SolutionWorkspace) revision =
        let descriptor = workspace.WorkspaceDescriptor

        map
            [ "id", text descriptor.WorkspaceId.Value
              "path", text descriptor.Path.Value
              "format", text (format descriptor.WorkspaceFormat)
              "readOnly", boolean descriptor.IsReadOnly
              "revision", integer revision ]

    let private optionalField
        (name: string)
        (fields: System.Collections.Immutable.ImmutableDictionary<string, RpcValue>)
        =
        fields.TryGetValue name
        |> function
            | true, value -> Some value
            | _ -> None

    let private requireEmpty parameters =
        let fields = RpcValue.requireMap "params" parameters

        if fields.Count <> 0 then
            invalidArg "params" "This method does not accept parameters."

    let private requestRevision name fields =
        match optionalField name fields with
        | None -> None
        | Some value -> Some(RpcValue.requireInteger name value)

    let private protocolVersion parameters =
        let fields = RpcValue.requireMap "initialize.params" parameters
        let version = fields["protocolVersion"] |> RpcValue.requireMap "protocolVersion"
        let major = version["major"] |> RpcValue.requireInteger "protocolVersion.major"
        let minor = version["minor"] |> RpcValue.requireInteger "protocolVersion.minor"

        if major <> 1L then
            invalidArg "protocolVersion.major" "Only protocol major version 1 is supported."

        if minor < 0L then
            invalidArg "protocolVersion.minor" "The protocol minor version cannot be negative."

        fields

    let private failureError (failure: WorkspaceFailure) =
        RpcErrors.create failure.Code.Value failure.Diagnostic.Message None

    let private unsupported message = Error(RpcErrors.unsupported message)

    let private openWorkspace target cancellationToken =
        task {
            let! outcome = SolutionStore.OpenAsync(target, cancellationToken)

            return
                match outcome with
                | WorkspaceOutcome.Success workspace -> Ok workspace
                | WorkspaceOutcome.Failure failure -> Error(failureError failure)
        }

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
                let mutable revision = workspace.WorkspaceDescriptor.WorkspaceRevision.Value

                let activeExports =
                    Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal)

                let initialize parameters cancellationToken =
                    task {
                        try
                            let fields = protocolVersion parameters

                            let capabilities =
                                match optionalField "capabilities" fields with
                                | None -> RpcValue.array []
                                | Some(RpcValue.Array values) -> RpcValue.array values
                                | Some _ -> invalidArg "capabilities" "Expected an array."

                            return
                                Ok(
                                    map
                                        [ "protocolVersion", map [ "major", integer 1L; "minor", integer 0L ]
                                          "serverInfo", map [ "name", text "dotnet-cli-plus"; "version", text "1" ]
                                          "workspace", workspaceValue workspace revision
                                          "capabilities", capabilities
                                          "limits",
                                          map
                                              [ "maxFrameBytes", integer (int64 RpcCodec.secureLimits.MaximumValueBytes)
                                                "maxPageSize", integer 1000L ] ]
                                )
                        with :? ArgumentException as error ->
                            return Error(RpcErrors.invalidParams error.Message)
                    }

                let dispatch (_: RpcSessionContext) methodName parameters cancellationToken =
                    task {
                        try
                            let root () =
                                requireEmpty parameters

                                Ok
                                    { Result =
                                        map
                                            [ "revision", integer revision
                                              "nodes",
                                              workspace.RootProjection.Nodes |> Seq.map nodeValue |> RpcValue.array ]
                                      Notifications = []
                                      StopAfterResponse = false }

                            let refresh () =
                                task {
                                    let fields = RpcValue.requireMap "params" parameters

                                    match requestRevision "expectedRevision" fields with
                                    | Some expected when expected <> revision ->
                                        return
                                            Error(
                                                RpcErrors.create
                                                    "workspace_conflict"
                                                    "The expected workspace revision is stale."
                                                    (Some(map [ "actualRevision", integer revision ]))
                                            )
                                    | _ ->
                                        let! reopened = openWorkspace target cancellationToken

                                        match reopened with
                                        | Error rpcError -> return Error rpcError
                                        | Ok next ->
                                            workspace <- next
                                            revision <- revision + 1L

                                            return
                                                Ok
                                                    { Result =
                                                        map
                                                            [ "revision", integer revision
                                                              "reset", boolean true
                                                              "diagnostics", RpcValue.array [] ]
                                                      Notifications = []
                                                      StopAfterResponse = false }
                                }

                            let export () =
                                requireEmpty parameters
                                let operationId = Guid.NewGuid().ToString("N")
                                use source = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                                activeExports[operationId] <- source
                                let nodes = workspace.RootProjection.Nodes |> Seq.map nodeValue |> Seq.toArray
                                let chunkSize = 128

                                let chunks =
                                    nodes
                                    |> Array.chunkBySize chunkSize
                                    |> Array.mapi (fun sequence chunk ->
                                        Notification(
                                            "workspace/exportChunk",
                                            map
                                                [ "operationId", text operationId
                                                  "sequence", integer (int64 sequence)
                                                  "revision", integer revision
                                                  "nodes", RpcValue.array chunk
                                                  "last",
                                                  boolean (sequence = (nodes.Length + chunkSize - 1) / chunkSize - 1) ]
                                        ))
                                    |> Array.toList

                                activeExports.Remove operationId |> ignore

                                Ok
                                    { Result = map [ "operationId", text operationId; "revision", integer revision ]
                                      Notifications =
                                        chunks
                                        @ [ Notification(
                                                "operation/completed",
                                                map
                                                    [ "operationId", text operationId
                                                      "sequence", integer (int64 chunks.Length)
                                                      "revision", integer revision
                                                      "diagnostics", RpcValue.array []
                                                      "outcome", text "succeeded" ]
                                            ) ]
                                      StopAfterResponse = false }

                            let cancel () =
                                let fields = RpcValue.requireMap "params" parameters
                                let operationId = fields["operationId"] |> RpcValue.requireString "operationId"

                                let accepted =
                                    match activeExports.TryGetValue operationId with
                                    | true, source ->
                                        source.Cancel()
                                        true
                                    | _ -> false

                                Ok
                                    { Result = map [ "accepted", boolean accepted ]
                                      Notifications = []
                                      StopAfterResponse = false }

                            match methodName with
                            | "workspace/root" -> return root ()
                            | "workspace/refresh" -> return! refresh ()
                            | "workspace/export" -> return export ()
                            | "operation/cancel" -> return cancel ()
                            | "shutdown" ->
                                requireEmpty parameters

                                return
                                    Ok
                                        { Result = map [ "accepted", boolean true ]
                                          Notifications = []
                                          StopAfterResponse = true }
                            | "workspace/children" ->
                                let fields = RpcValue.requireMap "params" parameters
                                fields["parentId"] |> RpcValue.requireString "parentId" |> ignore
                                return unsupported "Workspace children are not materialized until T-006."
                            | "command/list" ->
                                let fields = RpcValue.requireMap "params" parameters

                                match optionalField "targetId" fields with
                                | Some value -> RpcValue.requireString "targetId" value |> ignore
                                | None -> ()

                                return unsupported "Command discovery is not implemented until T-007."
                            | "command/describe" ->
                                let fields = RpcValue.requireMap "params" parameters
                                fields["commandId"] |> RpcValue.requireString "commandId" |> ignore
                                return unsupported "Command discovery is not implemented until T-007."
                            | "command/preview"
                            | "command/execute" ->
                                let fields = RpcValue.requireMap "params" parameters
                                fields["commandId"] |> RpcValue.requireString "commandId" |> ignore
                                fields["arguments"] |> RpcValue.requireMap "arguments" |> ignore

                                fields["expectedRevision"]
                                |> RpcValue.requireInteger "expectedRevision"
                                |> ignore

                                if workspace.WorkspaceDescriptor.IsReadOnly then
                                    return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
                                else
                                    return unsupported "Workspace mutations are not implemented until T-007."
                            | _ -> return Error(RpcErrors.unknownMethod methodName)
                        with :? ArgumentException as error ->
                            return Error(RpcErrors.invalidParams error.Message)
                    }

                let configuration =
                    { Profile = RpcProfile.publicProfile
                      Limits = RpcCodec.secureLimits
                      Initialize = initialize
                      Dispatch = dispatch }

                return! RpcSession.runAsync configuration input output error cancellationToken
        }
