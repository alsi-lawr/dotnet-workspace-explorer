namespace Dotnet.WorkspaceExplorer.Rpc


open System.Collections.Immutable

type WorkspaceInitializeRequest =
    { ProtocolMinor: int
      ClientName: string
      Capabilities: ImmutableArray<string>
      MaximumFrameBytes: int
      MaximumPageSize: int }

[<RequireQualifiedAccess>]
type WorkspaceRpcRequest =
    | Root
    | Children of parentNodeId: string * pageSize: int option * continuationToken: string option
    | ResolveFile of targetNodeId: string * expectedRevision: int64
    | Export
    | Refresh of expectedRevision: int64 option
    | CreateOptions of targetNodeId: string * expectedRevision: int64
    | CommandList of targetNodeId: string option
    | CommandDescribe of commandId: string * targetNodeId: string option
    | CommandPreview of
        commandId: string *
        targetNodeId: string option *
        arguments: RpcValue *
        expectedRevision: int64
    | CommandExecute of
        commandId: string *
        targetNodeId: string option *
        arguments: RpcValue *
        expectedRevision: int64 *
        confirmationToken: string option
    | Cancel of operationId: string
    | Shutdown
