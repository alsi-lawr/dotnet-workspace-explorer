namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Rpc
open Xunit

[<Collection("RPC scenarios")>]
type WorkspaceRpcFailureTests() =
    [<Fact>]
    member _.``should treat output failure as fatal``() =
        use input = new MemoryStream(Test.request 1u "initialize" Test.empty)
        use output = new FailingWriteStream()
        use errors = new StringWriter()

        let exitCode =
            RpcSession.runAsync
                (Test.defaultConfiguration WorkspaceRpcProfile.current)
                input
                output
                errors
                CancellationToken.None
            |> _.Result

        Assert.Equal(65, exitCode)
        Assert.Contains("failed while reading or writing", errors.ToString())

    [<Fact>]
    member _.``should treat background faults as fatal while reading and during shutdown``() =
        let cases = [ "while reading", false; "during shutdown", true ]

        for name, failOnCancellation in cases do
            let methods =
                if failOnCancellation then
                    [ "start", Read; "shutdown", Control ]
                else
                    [ "start", Read ]

            let profile = Test.profile $"fault-{name}" methods

            let dispatch _ methodName _ _ =
                if methodName = "start" then
                    let background (_: RpcNotificationSink) cancellationToken =
                        if failOnCancellation then
                            task {
                                try
                                    do! Task.Delay(Timeout.Infinite, cancellationToken)
                                with :? OperationCanceledException ->
                                    return raise (InvalidOperationException "shutdown fault")
                            }
                        else
                            Task.FromException<unit>(InvalidOperationException "background fault")

                    Task.FromResult(
                        Ok
                            { Test.dispatchResult Test.empty false with
                                BackgroundWork = Some background }
                    )
                else
                    Task.FromResult(Ok(Test.dispatchResult Test.empty true))

            let input =
                [ Test.request 1u "initialize" Test.empty
                  Test.request 2u "start" Test.empty
                  if failOnCancellation then
                      Test.request 3u "shutdown" Test.empty ]
                |> Array.concat

            use source =
                if failOnCancellation then
                    new MemoryStream(input) :> Stream
                else
                    new BlockingAfterDataStream(input)

            let configuration =
                Test.configuration profile (fun _ _ -> Task.FromResult(Ok Test.empty)) dispatch

            let exitCode, stdout, stderr =
                Test.runStream configuration source CancellationToken.None |> _.Result

            Assert.True((exitCode = 65), $"{name}: exit {exitCode}")
            Assert.Equal(2, (Test.frames stdout).Length)
            Assert.Contains("background RPC operation failed", stderr)
