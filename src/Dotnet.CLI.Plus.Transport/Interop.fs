namespace Dotnet.CLI.Plus.Transport

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

[<Sealed>]
type RpcInteropResponse(result: RpcValue, rpcError: RpcError, stopAfterResponse: bool) =
    member _.Result = result
    member _.RpcError = rpcError
    member _.StopAfterResponse = stopAfterResponse

    static member Ok(result: RpcValue, stopAfterResponse: bool) =
        RpcInteropResponse(result, Unchecked.defaultof<RpcError>, stopAfterResponse)

    static member Fail(error: RpcError) =
        RpcInteropResponse(RpcValue.Nil, error, false)

[<AbstractClass; Sealed>]
type RpcHost private () =
    static member CreateProfile(name: string, major: int, minor: int, methods: IEnumerable<string>) =
        methods
        |> Seq.map (fun methodName ->
            { Name = methodName
              Classification =
                if methodName = "initialize" || methodName = "shutdown" then
                    RpcMethodClassification.Control
                else
                    RpcMethodClassification.Read })
        |> RpcProfile.create name major minor

    static member RunAsync
        (
            profile: RpcProfile,
            input: Stream,
            output: Stream,
            error: TextWriter,
            initialize: Func<RpcValue, CancellationToken, Task<RpcInteropResponse>>,
            dispatch: Func<string, RpcValue, CancellationToken, Task<RpcInteropResponse>>,
            cancellationToken: CancellationToken
        ) =
        let convertInitialize (response: RpcInteropResponse) =
            if isNull (box response.RpcError) then
                Result.Ok response.Result
            else
                Result.Error response.RpcError

        let convertDispatch (response: RpcInteropResponse) =
            if isNull (box response.RpcError) then
                Result.Ok
                    { Result = response.Result
                      Notifications = []
                      BackgroundWork = None
                      AfterResponse = None
                      StopAfterResponse = response.StopAfterResponse }
            else
                Result.Error response.RpcError

        let configuration =
            { Profile = profile
              Limits = RpcCodec.secureLimits
              GetOutboundFrameLimit = fun () -> RpcCodec.secureLimits.MaximumValueBytes
              Initialize =
                fun parameters token ->
                    task {
                        let! response = initialize.Invoke(parameters, token)
                        return convertInitialize response
                    }
              Dispatch =
                fun _ methodName parameters token ->
                    task {
                        let! response = dispatch.Invoke(methodName, parameters, token)
                        return convertDispatch response
                    } }

        RpcSession.runAsync configuration input output error cancellationToken
