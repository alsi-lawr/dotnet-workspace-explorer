namespace Dotnet.CLI.Plus.Transport

open System
open System.Collections.Generic
open System.Collections.Immutable
open Dotnet.CLI.Plus.Core

type PublicInitializeRequest =
    { ProtocolMinor: int
      ClientName: string
      Capabilities: ImmutableArray<string>
      MaximumFrameBytes: int
      MaximumPageSize: int }

[<RequireQualifiedAccess>]
type PublicRequest =
    | Root
    | Children of parentId: string * pageSize: int option * continuationToken: string option
    | Export
    | Refresh of expectedRevision: int64 option
    | CommandList of targetId: string option
    | CommandDescribe of commandId: string * targetId: string option
    | CommandPreview of commandId: string * targetId: string option * arguments: RpcValue * expectedRevision: int64
    | CommandExecute of
        commandId: string *
        targetId: string option *
        arguments: RpcValue *
        expectedRevision: int64 *
        previewId: string option
    | Cancel of operationId: string
    | Shutdown

[<RequireQualifiedAccess>]
type PublicOperationOutcome =
    | Succeeded
    | Cancelled
    | Failed of code: string * message: string

[<RequireQualifiedAccess>]
module PublicProtocol =
    let private map values = RpcValue.map values
    let private text value = RpcValue.String value
    let private integer value = RpcValue.Integer value
    let private boolean value = RpcValue.Boolean value

    let private supportedCapabilities =
        ImmutableHashSet.CreateRange<string>(
            StringComparer.Ordinal,
            [ "workspace.root"
              "workspace.children"
              "workspace.export"
              "workspace.refresh"
              "workspace.delta"
              "workspace.reset"
              "operation.cancel" ]
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

    let private outcomeName =
        function
        | PublicOperationOutcome.Succeeded -> "succeeded"
        | PublicOperationOutcome.Cancelled -> "cancelled"
        | PublicOperationOutcome.Failed _ -> "failed"

    let node (workspaceId: WorkspaceId) revision (value: WorkspaceNode) =
        map
            [ "workspaceId", text workspaceId.Value
              "revision", integer revision
              "id", text value.NodeId.Value
              "kind", text (nodeKind value.NodeKind)
              "name", text value.Name
              "loadState", text (loadState value.NodeLoadState)
              "capabilities",
              value.AvailableCapabilities
              |> Seq.map (fun capability -> text capability.Value)
              |> RpcValue.array ]

    let workspace (descriptor: WorkspaceDescriptor) revision =
        map
            [ "id", text descriptor.WorkspaceId.Value
              "path", text descriptor.Path.Value
              "format", text (format descriptor.WorkspaceFormat)
              "readOnly", boolean descriptor.IsReadOnly
              "revision", integer revision ]

    let diagnostic (workspaceId: WorkspaceId) revision (value: WorkspaceDiagnostic) =
        let fields =
            ResizeArray<string * RpcValue>(
                [ "workspaceId", text workspaceId.Value
                  "revision", integer revision
                  "severity", text (severity value.DiagnosticSeverity)
                  "code", text value.DiagnosticCode.Value
                  "message", text value.Message
                  "retryable", boolean value.Retryable
                  "correlationId", text (value.DiagnosticCorrelationId.ToString()) ]
            )

        value.DiagnosticArtifactPath
        |> Option.iter (fun path -> fields.Add("path", text path.Value))

        value.DiagnosticLocation
        |> Option.iter (fun location ->
            fields.Add(
                "location",
                map
                    [ "line", integer (int64 location.Line)
                      "column", integer (int64 location.Column) ]
            ))

        map fields

    let private simpleDiagnostic (workspaceId: WorkspaceId) revision code message =
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

    let private invalid message =
        Error
            { Code = "invalid_params"
              Message = message
              Data = None }

    let private positiveInt minimum maximum name value =
        let parsed = RpcValue.requireInteger name value

        if parsed < int64 minimum || parsed > int64 maximum then
            invalidArg name $"Expected an integer between {minimum} and {maximum}."

        int parsed

    let private requiredString name fields =
        fields |> RpcValue.requireField name |> RpcValue.requireString name

    let private optionalString name fields =
        RpcValue.optionalField name fields |> Option.map (RpcValue.requireString name)

    let private requireEmpty parameters =
        let fields = RpcValue.requireMap "params" parameters

        if fields.Count <> 0 then
            invalidArg "params" "This method does not accept parameters."

    let parseInitialize parameters =
        try
            let fields = RpcValue.requireMap "initialize.params" parameters
            RpcValue.ensureOnly "initialize.params" [ "protocolVersion"; "clientInfo"; "capabilities"; "limits" ] fields

            let version =
                fields
                |> RpcValue.requireField "protocolVersion"
                |> RpcValue.requireMap "protocolVersion"

            RpcValue.ensureOnly "protocolVersion" [ "major"; "minor" ] version

            let major =
                version
                |> RpcValue.requireField "major"
                |> RpcValue.requireInteger "protocolVersion.major"

            let minor =
                version
                |> RpcValue.requireField "minor"
                |> RpcValue.requireInteger "protocolVersion.minor"

            if major <> 1L then
                invalidArg "protocolVersion.major" "Only protocol major version 1 is supported."

            if minor < 0L || minor > int64 Int32.MaxValue then
                invalidArg "protocolVersion.minor" "The protocol minor version is invalid."

            let clientInfo =
                fields |> RpcValue.requireField "clientInfo" |> RpcValue.requireMap "clientInfo"

            let clientName = requiredString "name" clientInfo

            if String.IsNullOrWhiteSpace clientName then
                invalidArg "clientInfo.name" "Client name must be a non-empty string."

            let capabilities =
                fields
                |> RpcValue.requireField "capabilities"
                |> RpcValue.requireArray "capabilities"

            let capabilityNames = ImmutableArray.CreateBuilder<string>(capabilities.Length)
            let unique = HashSet<string>(StringComparer.Ordinal)

            for capability in capabilities do
                let name = RpcValue.requireString "capabilities" capability

                if String.IsNullOrWhiteSpace name then
                    invalidArg "capabilities" "Capability names must be non-empty strings."

                if not (unique.Add name) then
                    invalidArg "capabilities" "Capability names must be unique."

                capabilityNames.Add name

            let maximumFrameBytes, maximumPageSize =
                match RpcValue.optionalField "limits" fields with
                | None -> RpcCodec.secureLimits.MaximumValueBytes, 256
                | Some value ->
                    let limits = RpcValue.requireMap "limits" value
                    RpcValue.ensureOnly "limits" [ "maxFrameBytes"; "maxPageSize" ] limits

                    let frame =
                        match RpcValue.optionalField "maxFrameBytes" limits with
                        | Some requested -> positiveInt 1024 Int32.MaxValue "limits.maxFrameBytes" requested
                        | None -> RpcCodec.secureLimits.MaximumValueBytes

                    let page =
                        match RpcValue.optionalField "maxPageSize" limits with
                        | Some requested -> positiveInt 1 4096 "limits.maxPageSize" requested
                        | None -> 256

                    min frame RpcCodec.secureLimits.MaximumValueBytes, page

            Ok
                { ProtocolMinor = 0
                  ClientName = clientName
                  Capabilities = capabilityNames.MoveToImmutable()
                  MaximumFrameBytes = maximumFrameBytes
                  MaximumPageSize = maximumPageSize }
        with
        | :? ArgumentException as error -> invalid error.Message
        | _ -> invalid "Initialize parameters are invalid."

    let parseRequest methodName parameters =
        try
            let parsed =
                match methodName with
                | "workspace/root" ->
                    requireEmpty parameters
                    PublicRequest.Root
                | "workspace/export" ->
                    requireEmpty parameters
                    PublicRequest.Export
                | "workspace/refresh" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "expectedRevision" ] fields

                    PublicRequest.Refresh(
                        RpcValue.optionalField "expectedRevision" fields
                        |> Option.map (RpcValue.requireInteger "expectedRevision")
                    )
                | "workspace/children" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "parentId"; "pageSize"; "continuationToken" ] fields

                    PublicRequest.Children(
                        requiredString "parentId" fields,
                        RpcValue.optionalField "pageSize" fields
                        |> Option.map (positiveInt 1 4096 "pageSize"),
                        optionalString "continuationToken" fields
                    )
                | "command/list" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "targetId" ] fields
                    PublicRequest.CommandList(optionalString "targetId" fields)
                | "command/describe" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "commandId"; "targetId" ] fields
                    PublicRequest.CommandDescribe(requiredString "commandId" fields, optionalString "targetId" fields)
                | "command/preview" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "commandId"; "targetId"; "arguments"; "expectedRevision" ] fields

                    let arguments = fields |> RpcValue.requireField "arguments"
                    RpcValue.requireMap "arguments" arguments |> ignore

                    let expectedRevision =
                        fields
                        |> RpcValue.requireField "expectedRevision"
                        |> RpcValue.requireInteger "expectedRevision"

                    PublicRequest.CommandPreview(
                        requiredString "commandId" fields,
                        optionalString "targetId" fields,
                        arguments,
                        expectedRevision
                    )
                | "command/execute" ->
                    let fields = RpcValue.requireMap "params" parameters

                    RpcValue.ensureOnly
                        "params"
                        [ "commandId"; "targetId"; "arguments"; "expectedRevision"; "previewId" ]
                        fields

                    let arguments = fields |> RpcValue.requireField "arguments"
                    RpcValue.requireMap "arguments" arguments |> ignore

                    let expectedRevision =
                        fields
                        |> RpcValue.requireField "expectedRevision"
                        |> RpcValue.requireInteger "expectedRevision"

                    PublicRequest.CommandExecute(
                        requiredString "commandId" fields,
                        optionalString "targetId" fields,
                        arguments,
                        expectedRevision,
                        optionalString "previewId" fields
                    )
                | "operation/cancel" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "operationId" ] fields
                    PublicRequest.Cancel(requiredString "operationId" fields)
                | "shutdown" ->
                    requireEmpty parameters
                    PublicRequest.Shutdown
                | _ -> invalidArg "methodName" "The method is not part of the public protocol."

            Ok parsed
        with
        | :? ArgumentException as error -> invalid error.Message
        | _ -> invalid "Request parameters are invalid."

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
              "serverInfo", map [ "name", text "dotnet-cli-plus"; "version", text "1" ]
              "workspace", workspace descriptor revision
              "capabilities", negotiatedCapabilities
              "limits",
              map
                  [ "maxFrameBytes", integer (int64 request.MaximumFrameBytes)
                    "maxPageSize", integer (int64 request.MaximumPageSize) ] ]

    let rootResult (descriptor: WorkspaceDescriptor) revision nodes =
        map
            [ "revision", integer revision
              "nodes", nodes |> Seq.map (node descriptor.WorkspaceId revision) |> RpcValue.array ]

    let childrenResult
        (descriptor: WorkspaceDescriptor)
        revision
        (parentId: NodeId)
        nodes
        (nextToken: ContinuationToken option)
        =
        let values =
            ResizeArray<string * RpcValue>(
                [ "revision", integer revision
                  "parentId", text parentId.Value
                  "nodes", nodes |> Seq.map (node descriptor.WorkspaceId revision) |> RpcValue.array ]
            )

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

    let private optionalNodeId (value: NodeId option) =
        match value with
        | Some value -> text value.Value
        | None -> RpcValue.Nil

    let private change (workspaceId: WorkspaceId) revision =
        function
        | WorkspaceChange.Added(nodeValue, parentId, placementKey) ->
            map
                [ "kind", text "add"
                  "parentId", optionalNodeId parentId
                  "placementKey", text placementKey
                  "node", node workspaceId revision nodeValue ]
        | WorkspaceChange.Removed(nodeId, parentId, placementKey) ->
            map
                [ "kind", text "remove"
                  "id", text nodeId.Value
                  "parentId", optionalNodeId parentId
                  "placementKey", text placementKey ]
        | WorkspaceChange.Updated(nodeValue, parentId, placementKey) ->
            map
                [ "kind", text "update"
                  "parentId", optionalNodeId parentId
                  "placementKey", text placementKey
                  "node", node workspaceId revision nodeValue ]
        | WorkspaceChange.Moved(nodeId, fromParentId, toParentId, placementKey) ->
            map
                [ "kind", text "move"
                  "id", text nodeId.Value
                  "fromParentId", optionalNodeId fromParentId
                  "toParentId", optionalNodeId toParentId
                  "placementKey", text placementKey ]
        | WorkspaceChange.Replaced(replacement, parentId, placementKey) ->
            map
                [ "kind", text "replace"
                  "oldId", text replacement.OldId.Value
                  "newId", text replacement.NewId.Value
                  "parentId", optionalNodeId parentId
                  "placementKey", text placementKey ]

    let workspaceDelta (delta: WorkspaceDelta) =
        Notification(
            "workspace/delta",
            map
                [ "workspaceId", text delta.WorkspaceId.Value
                  "baseRevision", integer delta.BaseRevision.Value
                  "newRevision", integer delta.NewRevision.Value
                  "changes",
                  delta.Changes
                  |> Seq.map (change delta.WorkspaceId delta.NewRevision.Value)
                  |> RpcValue.array
                  "diagnostics",
                  delta.Diagnostics
                  |> Seq.map (diagnostic delta.WorkspaceId delta.NewRevision.Value)
                  |> RpcValue.array ]
        )

    let workspaceReset (reset: WorkspaceReset) =
        Notification(
            "workspace/reset",
            map
                [ "workspaceId", text reset.WorkspaceId.Value
                  "revision", integer reset.Revision.Value
                  "diagnostics",
                  reset.Diagnostics
                  |> Seq.map (diagnostic reset.WorkspaceId reset.Revision.Value)
                  |> RpcValue.array ]
        )

    let exportChunk (descriptor: WorkspaceDescriptor) operationId sequence revision (nodes: seq<WorkspaceNode>) last =
        Notification(
            "workspace/exportChunk",
            map
                [ "workspaceId", text descriptor.WorkspaceId.Value
                  "operationId", text operationId
                  "sequence", integer (int64 sequence)
                  "revision", integer revision
                  "nodes", nodes |> Seq.map (node descriptor.WorkspaceId revision) |> RpcValue.array
                  "last", boolean last
                  "diagnostics", RpcValue.array [] ]
        )

    let operationCompleted (descriptor: WorkspaceDescriptor) operationId sequence revision outcome =
        let diagnostics =
            match outcome with
            | PublicOperationOutcome.Succeeded -> RpcValue.array []
            | PublicOperationOutcome.Cancelled ->
                RpcValue.array
                    [ simpleDiagnostic descriptor.WorkspaceId revision "cancelled" "The workspace export was cancelled." ]
            | PublicOperationOutcome.Failed(code, message) ->
                RpcValue.array [ simpleDiagnostic descriptor.WorkspaceId revision code message ]

        Notification(
            "operation/completed",
            map
                [ "workspaceId", text descriptor.WorkspaceId.Value
                  "operationId", text operationId
                  "sequence", integer (int64 sequence)
                  "revision", integer revision
                  "outcome", text (outcomeName outcome)
                  "diagnostics", diagnostics ]
        )
