namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open Xunit

[<Collection("Project evaluation scenarios")>]
type ProjectInputFailureTests() =
    [<Fact>]
    member _.``should keep stable failure mappings for missing malformed and incompatible inputs``
        ()
        =
        let directory = Test.temporaryDirectory "failures"

        try
            let malformed = Path.Combine(directory, "Malformed.csproj")
            Test.write malformed "<Project><PropertyGroup>"

            Test.withWorker directory (fun worker ->
                let missing, _ =
                    Test.evaluate worker 2u (Path.Combine(directory, "Missing.csproj"))

                Assert.Equal("msbuild.project_not_found", missing.Value.Code)
                let malformedFailure, _ = Test.evaluate worker 3u malformed
                Assert.Equal("msbuild.project_malformed", malformedFailure.Value.Code)
                4u)

            let incompatibleToolset = Path.Combine(directory, "not-an-sdk")
            Directory.CreateDirectory incompatibleToolset |> ignore
            use incompatible = Test.startWorker incompatibleToolset
            let stdout = incompatible.StandardOutput.ReadToEnd()
            let stderr = incompatible.StandardError.ReadToEnd()
            incompatible.WaitForExit()

            Assert.Equal(70, incompatible.ExitCode)
            Assert.Equal(String.Empty, stdout)
            Assert.Equal("project-evaluation-host:sdk-load-failed", stderr.Trim())
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``should repair a failed inner dimension in the same worker``() =
        let directory = Test.temporaryDirectory "dimension-recovery"

        try
            let brokenImport = Path.Combine(directory, "Broken.targets")
            Test.write brokenImport "<Project><PropertyGroup>"
            let project = Path.Combine(directory, "Repairable.csproj")

            Test.write
                project
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>
  <Import Project="Broken.targets" Condition="'$(TargetFramework)' == 'net9.0'" />
</Project>
"""

            Test.withWorker directory (fun worker ->
                let failed, _ = Test.evaluate worker 2u project
                Assert.Equal("msbuild.project_malformed", failed.Value.Code)

                Test.write
                    brokenImport
                    ("<Project><PropertyGroup><RepairMarker>repaired</RepairMarker>"
                     + "</PropertyGroup></Project>")

                let repairedError, repaired = Test.evaluate worker 3u project
                Assert.True repairedError.IsNone
                Assert.Equal(3, (Test.values "dimensions" repaired).Length)

                let net9 =
                    Test.values "dimensions" repaired
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String "net9.0")

                Assert.Contains(
                    Test.values "properties" net9,
                    fun value ->
                        Test.stringField "name" value = "RepairMarker"
                        && Test.stringField "value" value = "repaired"
                )

                4u)
        finally
            Directory.Delete(directory, true)
