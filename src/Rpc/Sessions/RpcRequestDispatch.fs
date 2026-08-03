namespace Dotnet.WorkspaceExplorer.Rpc


#nowarn "3511"

open System.Threading
open System.Threading.Tasks

type RpcResponseEffects =
    { Result: RpcValue
      Notifications: RpcFrame list
      BackgroundWork: (RpcNotificationSink -> CancellationToken -> Task<unit>) option
      AfterResponse: (unit -> unit) option }

[<RequireQualifiedAccess>]
type RpcRequestResult =
    | Continue of RpcResponseEffects
    | Stop of RpcValue

type RpcSessionContext =
    { Profile: RpcProfile
      IsInitialized: bool
      Limits: MessagePackRpcLimits }

type RpcSessionOptions =
    { Profile: RpcProfile
      Limits: MessagePackRpcLimits
      GetOutboundFrameLimit: unit -> int
      Initialize: RpcValue -> CancellationToken -> Task<Result<RpcValue, RpcError>>
      Dispatch:
          RpcSessionContext
              -> string
              -> RpcValue
              -> CancellationToken
              -> Task<Result<RpcRequestResult, RpcError>> }
