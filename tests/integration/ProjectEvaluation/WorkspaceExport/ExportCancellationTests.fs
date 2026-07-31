namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type ExportCancellationTests() =
    [<Fact>]
    member _.``should complete cancelled export once and reap executable resources on shutdown``() =
        let directory = Test.temporaryDirectory "cancellation"

        try
            let projects =
                [ for index in 1..20 -> Test.simpleProject directory $"Project{index}" ".fsproj" ]

            let solution = Test.writeSolution directory projects

            Test.withWorkspaceRpc solution (fun app ->
                Test.send app 2u "workspace/export/start" RpcValue.emptyMap
                let export = Test.requireSuccess 2u app
                let operationId = Test.stringField "operationId" export

                Test.send
                    app
                    3u
                    "workspace/operations/cancel"
                    (RpcValue.map [ "operationId", RpcValue.String operationId ])

                let mutable cancelAccepted = false
                let mutable completions = 0

                while completions = 0 do
                    match Test.readFrame app with
                    | Notification("workspace/export/chunk", _) -> ()
                    | Response(3u, error, result) ->
                        (error.IsNone) |> should equal true
                        (Test.field "accepted" result) |> should equal (RpcValue.Boolean true)
                        cancelAccepted <- true
                    | Notification("workspace/operations/completed", parameters) ->
                        (Test.stringField "operationId" parameters) |> should equal (operationId)
                        (Test.stringField "outcome" parameters) |> should equal ("cancelled")
                        completions <- completions + 1
                    | frame -> failwithf "Unexpected cancellation frame: %A" frame

                (cancelAccepted) |> should equal true
                (completions) |> should equal (1)
                4u)
        finally
            Directory.Delete(directory, true)
