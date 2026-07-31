namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type EvaluationWorkerCompatibilityTests() =
    [<Fact>]
    member _.``incompatible private worker initialization is rejected and the worker shuts down cleanly``
        ()
        =
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
                (rejected.Value.Code) |> should equal ("invalid_params")

                Test.send worker 2u "initialize" (Test.workerInitializeVersion 1L 0L 4096)
                let incompatible, _ = Test.readFrame worker |> Test.response 2u
                (incompatible.Value.Code) |> should equal ("invalid_params")

                Test.send worker 3u "initialize" (Test.workerInitialize 4096)
                let initialized = Test.requireSuccess 3u worker

                (initialized
                 |> Test.field "protocolVersion"
                 |> Test.field "major"
                 |> RpcValue.requireInteger "major")
                |> should equal (2L)

                (initialized
                 |> Test.field "limits"
                 |> Test.field "maxFrameBytes"
                 |> RpcValue.requireInteger "maxFrameBytes")
                |> should equal (4096L)

                Test.shutdown worker 4u
            finally
                Test.disposeProcess worker
        finally
            Directory.Delete(directory, true)
