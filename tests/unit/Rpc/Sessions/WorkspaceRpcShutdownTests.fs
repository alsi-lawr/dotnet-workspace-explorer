namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type WorkspaceRpcShutdownTests() =
    [<Fact>]
    member _.``should exit 130 without protocol output after read cancellation``() =
        use cancellation = new CancellationTokenSource()
        use source = new CancellingReadStream(cancellation)

        let exitCode, stdout, stderr =
            Test.runStream
                (Test.defaultConfiguration WorkspaceRpcProfile.current)
                source
                cancellation.Token
            |> _.Result

        (exitCode) |> should equal (130)
        (stdout) |> should be Empty
        (stderr) |> should equal (String.Empty)

    [<Fact>]
    member _.``should cancel background work before the final shutdown response``() =
        let profile = Test.profile "background" [ "start", Read; "shutdown", Control ]

        let dispatch _ methodName _ _ =
            if methodName = "start" then
                let background (sink: RpcNotificationSink) cancellationToken =
                    task {
                        try
                            do! Task.Delay(Timeout.Infinite, cancellationToken)
                        with :? OperationCanceledException ->
                            do!
                                sink.WriteAsync(
                                    Notification("workspace/operations/completed", Test.empty)
                                )
                    }

                Task.FromResult(
                    Ok
                        { Test.dispatchResult Test.empty false with
                            BackgroundWork = Some background }
                )
            else
                Task.FromResult(Ok(Test.dispatchResult Test.empty true))

        let input =
            Array.concat
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  Test.request 3u "shutdown" Test.empty ]

        let configuration =
            Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch

        let exitCode, stdout, stderr = Test.run configuration input
        (exitCode) |> should equal (0)
        (stderr) |> should equal (String.Empty)

        match Test.frames stdout with
        | [ Response(1u, None, _)
            Response(2u, None, _)
            Notification("workspace/operations/completed", RpcValue.Map fields)
            Response(3u, None, _) ] -> (fields) |> should be Empty
        | frames -> failwithf "Shutdown ordering changed: %A" frames
