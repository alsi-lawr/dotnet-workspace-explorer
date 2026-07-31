namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type SdkSelectionTests() =
    [<Fact>]
    member _.``public workspace refresh recovers project evaluation after global.json SDK selection invalidation``
        ()
        =
        let directory = Test.temporaryDirectory "global-json"

        try
            let version = Test.currentSdkVersion directory
            let globalJson = Path.Combine(directory, "global.json")
            Test.writeGlobalJson directory version
            let project = Test.simpleProject directory "Selected" ".csproj"
            let solution = Test.writeSolution directory [ project ]

            Test.withWorkspaceRpc solution (fun app ->
                let hydratedRevision = Test.hydrateProject app 2u
                Test.writeGlobalJson directory "99.0.100"
                Test.send app 100u "workspace/refresh" RpcValue.emptyMap

                let unavailable, observedRevision =
                    Test.requireSuccessAfterWorkspaceNotifications 100u hydratedRevision app

                (Test.field "reset" unavailable) |> should equal (RpcValue.Boolean true)

                let unavailableRevision =
                    Test.field "revision" unavailable |> RpcValue.requireInteger "revision"

                (unavailableRevision > observedRevision) |> should equal true

                let reset = Test.readMatchingWorkspaceReset unavailableRevision app

                (Test.values "diagnostics" reset)
                |> Seq.exists (fun diagnostic ->
                    Test.stringField "code" diagnostic = "workspace.refresh_unverified")
                |> should equal true

                Test.writeGlobalJson directory version
                Test.send app 101u "workspace/refresh" RpcValue.emptyMap

                let recovered, recoveredObservedRevision =
                    Test.requireSuccessAfterWorkspaceNotifications 101u unavailableRevision app

                (Test.field "reset" recovered) |> should equal (RpcValue.Boolean false)

                let recoveredRevision =
                    Test.field "revision" recovered |> RpcValue.requireInteger "revision"

                (recoveredRevision >= recoveredObservedRevision) |> should equal true

                let freshRevision = Test.hydrateProject app 200u
                (freshRevision > recoveredRevision) |> should equal true
                (File.Exists globalJson) |> should equal true
                300u)
        finally
            Directory.Delete(directory, true)
