namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.CommandLine
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type SdkSelectionTests() =
    [<Fact>]
    member _.``should recover global json selection invalidation through public refresh``() =
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

                Assert.Equal(RpcValue.Boolean true, Test.field "reset" unavailable)

                let unavailableRevision =
                    Test.field "revision" unavailable |> RpcValue.requireInteger "revision"

                Assert.True(unavailableRevision > observedRevision)

                let reset = Test.readMatchingWorkspaceReset unavailableRevision app

                Assert.Contains(
                    Test.values "diagnostics" reset,
                    fun diagnostic ->
                        Test.stringField "code" diagnostic = "workspace.refresh_unverified"
                )

                Test.writeGlobalJson directory version
                Test.send app 101u "workspace/refresh" RpcValue.emptyMap

                let recovered, recoveredObservedRevision =
                    Test.requireSuccessAfterWorkspaceNotifications 101u unavailableRevision app

                Assert.Equal(RpcValue.Boolean false, Test.field "reset" recovered)

                let recoveredRevision =
                    Test.field "revision" recovered |> RpcValue.requireInteger "revision"

                Assert.True(recoveredRevision >= recoveredObservedRevision)

                let freshRevision = Test.hydrateProject app 200u
                Assert.True(freshRevision > recoveredRevision)
                Assert.True(File.Exists globalJson)
                300u)
        finally
            Directory.Delete(directory, true)
