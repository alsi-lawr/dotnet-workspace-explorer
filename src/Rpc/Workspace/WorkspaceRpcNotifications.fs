namespace Dotnet.WorkspaceExplorer.Rpc

open Dotnet.WorkspaceExplorer.Workspaces

open System
open System.Collections.Generic
open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
type WorkspaceOperationCompletion =
    | Succeeded
    | Cancelled
    | Failed of code: string * message: string

open WorkspaceRpcResponses

[<RequireQualifiedAccess>]
module WorkspaceRpcNotifications =
    let private outcomeName =
        function
        | WorkspaceOperationCompletion.Succeeded -> "succeeded"
        | WorkspaceOperationCompletion.Cancelled -> "cancelled"
        | WorkspaceOperationCompletion.Failed _ -> "failed"

    let operationProgress (descriptor: WorkspaceDescriptor) operationId sequence revision message =
        map
            [ "workspaceId", text descriptor.Id.Value
              "operationId", text operationId
              "sequence", integer (int64 sequence)
              "revision", integer revision
              "message", text message ]
        |> fun parameters -> Notification("workspace/operations/progress", parameters)

    let operationOutput
        (descriptor: WorkspaceDescriptor)
        operationId
        sequence
        revision
        stream
        value
        =
        map
            [ "workspaceId", text descriptor.Id.Value
              "operationId", text operationId
              "sequence", integer (int64 sequence)
              "revision", integer revision
              "stream", text stream
              "text", text value ]
        |> fun parameters -> Notification("workspace/operations/output", parameters)

    let private optionalWorkspaceNodeId (value: WorkspaceNodeId option) =
        match value with
        | Some value -> text value.Value
        | None -> RpcValue.Nil

    let private change (workspaceId: WorkspaceId) revision =
        function
        | Added(nodeValue, parentNodeId, index) ->
            map
                [ "kind", text "add"
                  "parentNodeId", optionalWorkspaceNodeId parentNodeId
                  "index", integer (int64 index)
                  "node", node workspaceId revision nodeValue ]
        | Removed(nodeId, parentNodeId, index) ->
            map
                [ "kind", text "remove"
                  "id", text nodeId.Value
                  "parentNodeId", optionalWorkspaceNodeId parentNodeId
                  "index", integer (int64 index) ]
        | Updated(nodeValue, parentNodeId, index) ->
            map
                [ "kind", text "update"
                  "parentNodeId", optionalWorkspaceNodeId parentNodeId
                  "index", integer (int64 index)
                  "node", node workspaceId revision nodeValue ]
        | Moved(nodeId, oldParentId, oldIndex, newParentId, newIndex) ->
            map
                [ "kind", text "move"
                  "id", text nodeId.Value
                  "oldParentId", optionalWorkspaceNodeId oldParentId
                  "oldIndex", integer (int64 oldIndex)
                  "newParentId", optionalWorkspaceNodeId newParentId
                  "newIndex", integer (int64 newIndex) ]
        | Replaced(oldWorkspaceNodeId, newNode, parentNodeId, index) ->
            map
                [ "kind", text "replace"
                  "oldId", text oldWorkspaceNodeId.Value
                  "parentNodeId", optionalWorkspaceNodeId parentNodeId
                  "index", integer (int64 index)
                  "node", node workspaceId revision newNode ]

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

    let exportChunk
        (descriptor: WorkspaceDescriptor)
        operationId
        sequence
        revision
        (nodes: seq<WorkspaceNode>)
        last
        =
        Notification(
            "workspace/export/chunk",
            map
                [ "workspaceId", text descriptor.Id.Value
                  "operationId", text operationId
                  "sequence", integer (int64 sequence)
                  "revision", integer revision
                  "nodes", nodes |> Seq.map (node descriptor.Id revision) |> RpcValue.array
                  "last", boolean last
                  "diagnostics", RpcValue.array [] ]
        )

    let operationCompleted (descriptor: WorkspaceDescriptor) operationId sequence revision outcome =
        let diagnostics =
            match outcome with
            | WorkspaceOperationCompletion.Succeeded -> RpcValue.array []
            | WorkspaceOperationCompletion.Cancelled ->
                RpcValue.array
                    [ simpleDiagnostic
                          descriptor.Id
                          revision
                          "cancelled"
                          "The workspace operation was cancelled." ]
            | WorkspaceOperationCompletion.Failed(code, message) ->
                RpcValue.array [ simpleDiagnostic descriptor.Id revision code message ]

        Notification(
            "workspace/operations/completed",
            map
                [ "workspaceId", text descriptor.Id.Value
                  "operationId", text operationId
                  "sequence", integer (int64 sequence)
                  "revision", integer revision
                  "outcome", text (outcomeName outcome)
                  "diagnostics", diagnostics ]
        )
