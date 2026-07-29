namespace Dotnet.WorkspaceExplorer.Rpc

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3511"

open System
open System.IO
open System.Threading
open System.Threading.Tasks

type RpcRequestResult =
    { Result: RpcValue
      Notifications: RpcFrame list
      BackgroundWork: (RpcNotificationSink -> CancellationToken -> Task<unit>) option
      AfterResponse: (unit -> unit) option
      StopAfterResponse: bool }

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
