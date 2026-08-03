namespace Dotnet.WorkspaceExplorer.Rpc

type RpcError =
    { Code: string
      Message: string
      Data: RpcValue option }

type RpcFrame =
    | Request of messageId: uint32 * methodName: string * parameters: RpcValue
    | Response of messageId: uint32 * outcome: Result<RpcValue, RpcError>
    | Notification of methodName: string * parameters: RpcValue
