namespace Dotnet.WorkspaceExplorer.Rpc

open System
open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces

module WorkspaceRpcResponses =
    let internal map values = RpcValue.map values
    let internal text value = RpcValue.String value
    let internal integer value = RpcValue.Integer value
    let internal boolean value = RpcValue.Boolean value

    let private supportedCapabilities =
        ImmutableHashSet.CreateRange<string>(
            StringComparer.Ordinal,
            [ "workspace.root"
              "workspace.children"
              "workspace.file.resolve"
              "workspace.export.start"
              "workspace.refresh"
              "workspace.delta"
              "workspace.reset"
              "workspace.create.options"
              "workspace.commands.list"
              "workspace.commands.describe"
              "workspace.commands.preview"
              "workspace.commands.execute"
              "workspace.operations.cancel"
              "workspace.operations.progress"
              "workspace.operations.output"
              "workspace.operations.completed" ]
        )

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
        | WorkspaceNodeKind.ProjectFolder -> "projectFolder"
        | WorkspaceNodeKind.ProjectFile -> "projectFile"
        | WorkspaceNodeKind.DependencyContainer -> "dependencyContainer"
        | WorkspaceNodeKind.Dependency -> "dependency"
        | _ -> "unknown"

    let private loadState =
        function
        | WorkspaceNodeLoadState.Hydrated -> "hydrated"
        | WorkspaceNodeLoadState.Unhydrated -> "unhydrated"
        | WorkspaceNodeLoadState.FilteredOut -> "filteredOut"
        | _ -> "unknown"

    let private severity =
        function
        | WorkspaceDiagnosticSeverity.Information -> "information"
        | WorkspaceDiagnosticSeverity.Warning -> "warning"
        | WorkspaceDiagnosticSeverity.Error -> "error"
        | _ -> "error"

    let private commandAccess =
        function
        | CommandAccess.Read -> "read"
        | _ -> "write"

    let private commandParameterType =
        function
        | CommandParameterType.Text -> "text"
        | CommandParameterType.Path -> "path"
        | CommandParameterType.Boolean -> "boolean"
        | CommandParameterType.Integer -> "integer"
        | CommandParameterType.NodeId -> "nodeId"
        | CommandParameterType.Choice -> "choice"
        | CommandParameterType.TextArray -> "textArray"
        | _ -> "unknown"

    let commandDescriptor (value: CommandDescriptor) =
        map
            [ "id", text value.Id.Value
              "name", text value.Name
              "access", text (commandAccess value.Access)
              "parameters",
              value.Parameters
              |> Seq.map (fun parameter ->
                  map
                      [ "id", text parameter.Id.Value
                        "name", text parameter.Name
                        "type", text (commandParameterType parameter.Type)
                        "required", boolean parameter.Required ])
              |> RpcValue.array
              "targetKinds", value.TargetKinds |> Seq.map nodeKind |> Seq.map text |> RpcValue.array ]

    let commandListResult (commands: seq<CommandDescriptor>) =
        map [ "commands", commands |> Seq.map commandDescriptor |> RpcValue.array ]

    let commandDescribeResult (command: CommandDescriptor) =
        map [ "command", commandDescriptor command ]

    let commandPreviewResult
        (preview: WorkspaceEditPreview)
        summary
        (effects: seq<string * string * bool>)
        =
        map
            [ "confirmationToken", text preview.Confirmation.Value
              "expiresAtUtc", text (preview.ExpiresAtUtc.ToString "O")
              "summary", text summary
              "effects",
              effects
              |> Seq.map (fun (operation, target, recursive) ->
                  map
                      [ "operation", text operation
                        "target", text target
                        "recursive", boolean recursive ])
              |> RpcValue.array ]

    let commandExecuteResult revision =
        map [ "applied", boolean true; "revision", integer revision ]

    let commandOperationResult operationId revision =
        map [ "operationId", text operationId; "revision", integer revision ]

    let createOptionsResult revision (options: seq<RpcValue>) =
        map [ "revision", integer revision; "options", RpcValue.array options ]

    let fileResolveResult revision targetNodeId path =
        map
            [ "revision", integer revision
              "targetNodeId", text targetNodeId
              "path", text path ]

    let node (workspaceId: WorkspaceId) revision (value: WorkspaceNode) =
        map
            [ "workspaceId", text workspaceId.Value
              "revision", integer revision
              "id", text value.Id.Value
              "kind", text (nodeKind value.Kind)
              "name", text value.Name
              "loadState", text (loadState value.LoadState)
              "capabilities",
              value.Capabilities
              |> Seq.map (fun capability -> text capability.Value)
              |> RpcValue.array ]

    let workspace (descriptor: WorkspaceDescriptor) revision =
        map
            [ "id", text descriptor.Id.Value
              "path", text descriptor.Path.Value
              "format", text (format descriptor.Format)
              "readOnly", boolean descriptor.IsReadOnly
              "revision", integer revision ]

    let diagnostic (workspaceId: WorkspaceId) revision (value: WorkspaceDiagnostic) =
        let fields =
            ResizeArray<string * RpcValue>
                [ "workspaceId", text workspaceId.Value
                  "revision", integer revision
                  "severity", text (severity value.Severity)
                  "code", text value.Code.Value
                  "message", text value.Message
                  "retryable", boolean value.Retryable
                  "correlationId", text (value.CorrelationId.ToString()) ]


        value.ArtifactPath
        |> Option.iter (fun path -> fields.Add("path", text path.Value))

        value.Location
        |> Option.iter (fun location ->
            fields.Add(
                "location",
                map
                    [ "line", integer (int64 location.Line)
                      "column", integer (int64 location.Column) ]
            ))

        map fields

    let simpleDiagnostic (workspaceId: WorkspaceId) revision code message =
        map
            [ "workspaceId", text workspaceId.Value
              "revision", integer revision
              "severity", text "error"
              "code", text code
              "message", text message
              "retryable", boolean false ]

    let failureError (failure: WorkspaceFailure) =
        { Code = failure.Code.Value
          Message = failure.Diagnostic.Message
          Data = None }

    let workspaceConflict actualRevision =
        RpcErrors.create
            "workspace_conflict"
            "The expected workspace revision is stale."
            (Some(map [ "actualRevision", integer actualRevision ]))

    let initializeResult (descriptor: WorkspaceDescriptor) revision request =
        let negotiatedCapabilities =
            request.Capabilities
            |> Seq.filter supportedCapabilities.Contains
            |> Seq.distinct
            |> Seq.sort
            |> Seq.map text
            |> RpcValue.array

        map
            [ "protocolVersion", map [ "major", integer 1L; "minor", integer 0L ]
              "serverInfo", map [ "name", text "dotnet-workspace-explorer"; "version", text "1" ]
              "workspace", workspace descriptor revision
              "capabilities", negotiatedCapabilities
              "limits",
              map
                  [ "maxFrameBytes", integer (int64 request.MaximumFrameBytes)
                    "maxPageSize", integer (int64 request.MaximumPageSize) ] ]

    let rootResult (descriptor: WorkspaceDescriptor) revision nodes =
        map
            [ "revision", integer revision
              "nodes", nodes |> Seq.map (node descriptor.Id revision) |> RpcValue.array ]

    let childrenResult
        (descriptor: WorkspaceDescriptor)
        revision
        (parentNodeId: WorkspaceNodeId)
        nodes
        (nextToken: WorkspacePageToken option)
        =
        let values =
            ResizeArray<string * RpcValue>
                [ "revision", integer revision
                  "parentNodeId", text parentNodeId.Value
                  "nodes", nodes |> Seq.map (node descriptor.Id revision) |> RpcValue.array ]


        nextToken
        |> Option.iter (fun token -> values.Add("nextToken", text token.Value))

        map values

    let refreshResult revision reset =
        map
            [ "revision", integer revision
              "reset", boolean reset
              "diagnostics", RpcValue.array [] ]

    let exportResult operationId revision =
        map [ "operationId", text operationId; "revision", integer revision ]

    let cancelResult accepted = map [ "accepted", boolean accepted ]
    let shutdownResult = map [ "accepted", boolean true ]
