namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type OutboundFrameLimitTests() =
    [<Fact>]
    member _.``responses and notifications stay within negotiated limits with error outcomes``() =
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
            |> List.iter (fun (_, size) -> (size <= limit) |> should equal true)

        let initializeInput =
            Array.concat
                [ Test.request 1u "initialize" Test.empty; Test.request 2u "big" Test.empty ]

        let initializeExit, initializeOutput, initializeError =
            run
                (fun _ _ -> Task.FromResult(Ok oversized))
                (fun _ _ _ _ -> Task.FromResult(Ok(Test.dispatchResult Test.empty false)))
                initializeInput

        (initializeExit) |> should equal (0)
        (initializeError) |> should equal (String.Empty)
        assertBounded "initialize" initializeOutput

        (Test.responseErrors initializeOutput)
        |> should equal ([ 1u, "response_too_large"; 2u, "not_initialized" ])

        let requestInput =
            Array.concat
                [ Test.request 1u "initialize" Test.empty; Test.request 2u "big" Test.empty ]

        let requestExit, requestOutput, requestError =
            run
                (fun _ _ -> Task.FromResult(Ok Test.empty))
                (fun _ _ _ _ -> Task.FromResult(Ok(Test.dispatchResult oversized false)))
                requestInput

        (requestExit) |> should equal (0)
        (requestError) |> should equal (String.Empty)
        assertBounded "request" requestOutput

        (Test.responseErrors requestOutput)
        |> should equal ([ 2u, "response_too_large" ])

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

                Task.FromResult(Ok(Test.dispatchResultWithBackground Test.empty background))
            else
                Task.FromResult(Ok(Test.dispatchResult Test.empty true))

        let backgroundInput =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let backgroundExit, backgroundOutput, backgroundError =
            run (fun _ _ -> Task.FromResult(Ok Test.empty)) backgroundDispatch backgroundInput

        (backgroundExit) |> should equal (0)
        (backgroundError) |> should equal (String.Empty)
        assertBounded "background" backgroundOutput

        (Test.frames backgroundOutput)
        |> Seq.exists (function
            | Notification("workspace/operations/completed", parameters) ->
                RpcValue.tryField "code" parameters = Some(RpcValue.String "response_too_large")
            | _ -> false)
        |> should equal true

    [<Fact>]
    member _.``prepared notifications serialize writes and reject oversized payloads``() =
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
                        (failure.Limit) |> should equal (limit)
                        (failure.Actual) |> should equal (oversized.Length)

                    do!
                        Notification(
                            "prepared/completed",
                            Test.map [ "count", RpcValue.Integer(int64 notifications.Length) ]
                        )
                        |> EncodedRpcNotification.Create
                        |> sink.WriteEncodedAsync

                    completed.TrySetResult() |> ignore
                }

            Task.FromResult(Ok(Test.dispatchResultWithBackground Test.empty background))

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
        (completed.Task.Wait(TimeSpan.FromSeconds 10.0)) |> should equal true
        cancellation.Cancel()
        let exitCode, stdout, stderr = running.Result
        (exitCode) |> should equal (130)
        (stderr) |> should equal (String.Empty)

        let decoded = Test.decode stdout
        decoded |> List.iter (fun (_, size) -> (size <= limit) |> should equal true)

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

        (indices) |> should equal ([ 0L .. 31L ])

        (decoded)
        |> Seq.exists (function
            | Notification("prepared/completed", parameters), _ ->
                RpcValue.tryField "count" parameters
                |> Option.exists (fun value -> RpcValue.requireInteger "count" value = 32L)
            | _ -> false)
        |> should equal true

        (decoded)
        |> Seq.exists (function
            | Notification("prepared/oversized", _), _ -> true
            | _ -> false)
        |> should equal false

    [<Fact>]
    member _.``cancellation during a prepared notification write emits no partial frame``() =
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

            Task.FromResult(Ok(Test.dispatchResultWithBackground Test.empty background))

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

        (notificationStarted.Task.Wait(TimeSpan.FromSeconds 10.0)) |> should equal true
        cancellation.Cancel()
        (running.Result) |> should equal (130)
        (errors.ToString()) |> should equal (String.Empty)

        match Test.frames (output.ToArray()) with
        | [ Response(1u, Ok _); Response(2u, Ok _) ] -> ()
        | frames -> failwithf "A cancelled prepared write produced partial output: %A" frames
