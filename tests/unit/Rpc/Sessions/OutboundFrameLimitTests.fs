namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type OutboundFrameLimitTests() =
    [<Fact>]
    member _.``should bound responses and background notifications with outbound limits``() =
        let limit = 1024
        let oversized = Test.map [ "payload", RpcValue.String(String('x', 5000)) ]

        let profile =
            Test.profile "limits" [ "big", Read; "start", Read; "shutdown", Control ]

        let run initialize dispatch input =
            let mutable outboundLimit = MessagePackRpcCodec.secureLimits.MaximumValueBytes

            let configuration =
                Test.configurationWithLimit
                    profile
                    (fun () -> outboundLimit)
                    (fun parameters token ->
                        outboundLimit <- limit
                        initialize parameters token)
                    dispatch

            Test.run configuration input

        let assertBounded name stdout =
            Test.decode stdout
            |> List.iter (fun (_, size) -> Assert.True(size <= limit, $"{name}: {size}-byte frame"))

        let initializeInput =
            Array.concat
                [ Test.request 1u "initialize" Test.empty; Test.request 2u "big" Test.empty ]

        let initializeExit, initializeOutput, initializeError =
            run
                (fun _ _ -> Task.FromResult(Ok oversized))
                (fun _ _ _ _ -> Task.FromResult(Ok(Test.dispatchResult Test.empty false)))
                initializeInput

        Assert.Equal(0, initializeExit)
        Assert.Equal(String.Empty, initializeError)
        assertBounded "initialize" initializeOutput

        Assert.Equal<(uint32 * string) list>(
            [ 1u, "response_too_large"; 2u, "not_initialized" ],
            Test.responseErrors initializeOutput
        )

        let requestInput =
            Array.concat
                [ Test.request 1u "initialize" Test.empty; Test.request 2u "big" Test.empty ]

        let requestExit, requestOutput, requestError =
            run
                (fun _ _ -> Task.FromResult(Ok Test.empty))
                (fun _ _ _ _ -> Task.FromResult(Ok(Test.dispatchResult oversized false)))
                requestInput

        Assert.Equal(0, requestExit)
        Assert.Equal(String.Empty, requestError)
        assertBounded "request" requestOutput

        Assert.Equal<(uint32 * string) list>(
            [ 2u, "response_too_large" ],
            Test.responseErrors requestOutput
        )

        let backgroundDispatch _ methodName _ _ =
            if methodName = "start" then
                let background (sink: RpcNotificationSink) _ =
                    task {
                        try
                            do!
                                sink.WriteAsync(
                                    Notification("workspace/operations/output", oversized)
                                )
                        with :? RpcFrameLimitExceededException ->
                            do!
                                sink.WriteAsync(
                                    Notification(
                                        "workspace/operations/completed",
                                        Test.map
                                            [ "outcome", RpcValue.String "failed"
                                              "code", RpcValue.String "response_too_large" ]
                                    )
                                )
                    }

                Task.FromResult(
                    Ok
                        { Test.dispatchResult Test.empty false with
                            BackgroundWork = Some background }
                )
            else
                Task.FromResult(Ok(Test.dispatchResult Test.empty true))

        let backgroundInput =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let backgroundExit, backgroundOutput, backgroundError =
            run (fun _ _ -> Task.FromResult(Ok Test.empty)) backgroundDispatch backgroundInput

        Assert.Equal(0, backgroundExit)
        Assert.Equal(String.Empty, backgroundError)
        assertBounded "background" backgroundOutput

        Assert.Contains(
            Test.frames backgroundOutput,
            function
            | Notification("workspace/operations/completed", parameters) ->
                RpcValue.tryField "code" parameters = Some(RpcValue.String "response_too_large")
            | _ -> false
        )

    [<Fact>]
    member _.``should synchronize and limit prepared notification writes without re-encoding``() =
        let limit = 512
        let profile = Test.profile "prepared" [ "start", Read ]
        use cancellation = new CancellationTokenSource()

        let completed =
            TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

        let dispatch _ _ _ _ =
            let background (sink: RpcNotificationSink) _ =
                task {
                    let notifications =
                        [| for index in 0..31 ->
                               Notification(
                                   "prepared/value",
                                   Test.map
                                       [ "index", RpcValue.Integer(int64 index)
                                         "payload",
                                         RpcValue.String(String(char (65 + index % 26), 80)) ]
                               )
                               |> EncodedRpcNotification.Create |]

                    let! _ = notifications |> Array.map sink.WriteEncodedAsync |> Task.WhenAll

                    ()

                    let oversized =
                        Notification(
                            "prepared/oversized",
                            Test.map [ "payload", RpcValue.String(String('x', 5000)) ]
                        )
                        |> EncodedRpcNotification.Create

                    try
                        do! sink.WriteEncodedAsync oversized
                    with :? RpcFrameLimitExceededException as failure ->
                        Assert.Equal(limit, failure.Limit)
                        Assert.Equal(oversized.Length, failure.Actual)

                    do!
                        Notification(
                            "prepared/completed",
                            Test.map [ "count", RpcValue.Integer(int64 notifications.Length) ]
                        )
                        |> EncodedRpcNotification.Create
                        |> sink.WriteEncodedAsync

                    completed.TrySetResult() |> ignore
                }

            Task.FromResult(
                Ok
                    { Test.dispatchResult Test.empty false with
                        BackgroundWork = Some background }
            )

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty; Test.request 2u "start" Test.empty ]

        use source = new BlockingAfterDataStream(input)

        let configuration =
            Test.configurationWithLimit
                profile
                (fun () -> limit)
                (fun _ _ -> Task.FromResult(Ok Test.empty))
                dispatch

        let running = Test.runStream configuration source cancellation.Token
        Assert.True(completed.Task.Wait(TimeSpan.FromSeconds 10.0))
        cancellation.Cancel()
        let exitCode, stdout, stderr = running.Result
        Assert.Equal(130, exitCode)
        Assert.Equal(String.Empty, stderr)

        let decoded = Test.decode stdout
        decoded |> List.iter (fun (_, size) -> Assert.True(size <= limit))

        let indices =
            decoded
            |> List.choose (function
                | Notification("prepared/value", parameters), _ ->
                    Some(
                        RpcValue.tryField "index" parameters
                        |> Option.map (RpcValue.requireInteger "index")
                        |> Option.defaultWith (fun () -> failwith "Prepared index was absent.")
                    )
                | _ -> None)
            |> List.sort

        Assert.Equal<int64 list>([ 0L .. 31L ], indices)

        Assert.Contains(
            decoded,
            function
            | Notification("prepared/completed", parameters), _ ->
                RpcValue.tryField "count" parameters
                |> Option.exists (fun value -> RpcValue.requireInteger "count" value = 32L)
            | _ -> false
        )

        Assert.DoesNotContain(
            decoded,
            function
            | Notification("prepared/oversized", _), _ -> true
            | _ -> false
        )

    [<Fact>]
    member _.``should cancel a prepared notification write without partial-frame success``() =
        let profile = Test.profile "prepared-cancel" [ "start", Read ]
        use cancellation = new CancellationTokenSource()

        let notificationStarted =
            TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

        let dispatch _ _ _ _ =
            let background (sink: RpcNotificationSink) _ =
                Notification(
                    "prepared/blocked",
                    Test.map [ "payload", RpcValue.String(String('z', 128)) ]
                )
                |> EncodedRpcNotification.Create
                |> sink.WriteEncodedAsync

            Task.FromResult(
                Ok
                    { Test.dispatchResult Test.empty false with
                        BackgroundWork = Some background }
            )

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty; Test.request 2u "start" Test.empty ]

        use source = new BlockingAfterDataStream(input)
        use output = new BlockingNotificationWriteStream(notificationStarted)
        use errors = new StringWriter()

        let configuration =
            Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch

        let running =
            RpcSession.runAsync configuration source output errors cancellation.Token

        Assert.True(notificationStarted.Task.Wait(TimeSpan.FromSeconds 10.0))
        cancellation.Cancel()
        Assert.Equal(130, running.Result)
        Assert.Equal(String.Empty, errors.ToString())

        match Test.frames (output.ToArray()) with
        | [ Response(1u, None, _); Response(2u, None, _) ] -> ()
        | frames -> failwithf "A cancelled prepared write produced partial output: %A" frames
