namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
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

                (missing.Value.Code) |> should equal ("msbuild.project_not_found")
                let malformedFailure, _ = Test.evaluate worker 3u malformed
                (malformedFailure.Value.Code) |> should equal ("msbuild.project_malformed")
                4u)

            let incompatibleToolset = Path.Combine(directory, "not-an-sdk")
            Directory.CreateDirectory incompatibleToolset |> ignore
            use incompatible = Test.startWorker incompatibleToolset
            let stdout = incompatible.StandardOutput.ReadToEnd()
            let stderr = incompatible.StandardError.ReadToEnd()
            incompatible.WaitForExit()

            (incompatible.ExitCode) |> should equal (70)
            (stdout) |> should equal (String.Empty)
            (stderr.Trim()) |> should equal ("project-evaluation-host:sdk-load-failed")
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
                (failed.Value.Code) |> should equal ("msbuild.project_malformed")

                Test.write
                    brokenImport
                    ("<Project><PropertyGroup><RepairMarker>repaired</RepairMarker>"
                     + "</PropertyGroup></Project>")

                let repairedError, repaired = Test.evaluate worker 3u project
                (repairedError.IsNone) |> should equal true
                ((Test.values "dimensions" repaired).Length) |> should equal (3)

                let net9 =
                    Test.values "dimensions" repaired
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String "net9.0")

                (Test.values "properties" net9)
                |> Seq.exists (fun value ->
                    Test.stringField "name" value = "RepairMarker"
                    && Test.stringField "value" value = "repaired")
                |> should equal true

                4u)
        finally
            Directory.Delete(directory, true)
