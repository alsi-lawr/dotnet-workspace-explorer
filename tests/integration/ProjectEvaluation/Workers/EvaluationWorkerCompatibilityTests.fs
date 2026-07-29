namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open Xunit

[<Collection("Project evaluation scenarios")>]
type EvaluationWorkerCompatibilityTests() =
    [<Fact>]
    member _.``should reject incompatible private worker versions and shut down cleanly``() =
        let directory = Test.temporaryDirectory "protocol"

        try
            let worker = Test.startWorker (Test.currentToolsetPath directory)

            try
                let wrongProfile =
                    Test.workerInitialize 4096
                    |> function
                        | RpcValue.Map fields ->
                            fields.SetItem(
                                "profile",
                                RpcValue.String "dotnet-workspace-explorer/workspace"
                            )
                            |> RpcValue.Map
                        | _ -> failwith "Initialize payload was not a map."

                Test.send worker 1u "initialize" wrongProfile
                let rejected, _ = Test.readFrame worker |> Test.response 1u
                Assert.Equal("invalid_params", rejected.Value.Code)

                Test.send worker 2u "initialize" (Test.workerInitializeVersion 1L 0L 4096)
                let incompatible, _ = Test.readFrame worker |> Test.response 2u
                Assert.Equal("invalid_params", incompatible.Value.Code)

                Test.send worker 3u "initialize" (Test.workerInitialize 4096)
                let initialized = Test.requireSuccess 3u worker

                Assert.Equal(
                    2L,
                    initialized
                    |> Test.field "protocolVersion"
                    |> Test.field "major"
                    |> RpcValue.requireInteger "major"
                )

                Assert.Equal(
                    4096L,
                    initialized
                    |> Test.field "limits"
                    |> Test.field "maxFrameBytes"
                    |> RpcValue.requireInteger "maxFrameBytes"
                )

                Test.shutdown worker 4u
            finally
                Test.disposeProcess worker
        finally
            Directory.Delete(directory, true)
