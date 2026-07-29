namespace Dotnet.WorkspaceExplorer.Rpc


open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

[<Sealed>]
type internal HostedRpcResponse
    private (outcome: Result<RpcValue, RpcError>, stopAfterResponse: bool) =
    member internal _.Outcome = outcome
    member internal _.StopAfterResponse = stopAfterResponse

    static member Ok(result: RpcValue, stopAfterResponse: bool) =
        HostedRpcResponse(Ok result, stopAfterResponse)

    static member Fail(error: RpcError) = HostedRpcResponse(Error error, false)

[<AbstractClass; Sealed>]
type internal RpcHost private () =
    static member CreateProfile
        (name: string, major: int, minor: int, methods: IEnumerable<string>)
        =
        methods
        |> Seq.map (fun methodName ->
            { Name = methodName
              Classification =
                if methodName = "initialize" || methodName = "shutdown" then
                    Control
                else
                    Read })
        |> RpcProfile.create name major minor

    static member RunAsync
        (
            profile: RpcProfile,
            input: Stream,
            output: Stream,
            error: TextWriter,
            getOutboundFrameLimit: Func<int>,
            initialize: Func<RpcValue, CancellationToken, Task<HostedRpcResponse>>,
            dispatch: Func<string, RpcValue, CancellationToken, Task<HostedRpcResponse>>,
            cancellationToken: CancellationToken
        ) =
        let convertDispatch result stopAfterResponse =
            result
            |> Result.map (fun value ->
                { Result = value
                  Notifications = []
                  BackgroundWork = None
                  AfterResponse = None
                  StopAfterResponse = stopAfterResponse })

        let configuration =
            { Profile = profile
              Limits = MessagePackRpcCodec.secureLimits
              GetOutboundFrameLimit = getOutboundFrameLimit.Invoke
              Initialize =
                fun parameters token ->
                    task {
                        let! response = initialize.Invoke(parameters, token)
                        return response.Outcome
                    }
              Dispatch =
                fun _ methodName parameters token ->
                    task {
                        let! response = dispatch.Invoke(methodName, parameters, token)
                        return convertDispatch response.Outcome response.StopAfterResponse
                    } }

        RpcSession.runAsync configuration input output error cancellationToken
