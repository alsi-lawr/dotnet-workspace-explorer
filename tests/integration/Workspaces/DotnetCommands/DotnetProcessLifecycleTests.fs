namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open FsUnit.Xunit
open Xunit

[<Collection("Delegated dotnet processes")>]
type DotnetProcessLifecycleTests() =
    [<Fact>]
    member _.``should sanitize output and preserve exit mapping in the json failure envelope``() =
        let directory = DirectCommandProcess.temporaryDirectory "direct command-failure"

        try
            let result = DirectCommandProcess.run directory "failure" [ "--json"; "build" ] []
            (result.ExitCode) |> should equal (23)
            (result.StandardError) |> should equal (String.Empty)
            use document = DirectCommandProcess.json result

            let diagnostic =
                document.RootElement.GetProperty("diagnostics").EnumerateArray() |> Seq.head

            (document.RootElement.GetProperty("success").GetBoolean()) |> should equal false

            (document.RootElement.GetProperty("externalExitCode").GetInt32())
            |> should equal (23)

            (diagnostic.GetProperty("code").GetString())
            |> should equal ("external_tool_failed")

            (document.RootElement.GetProperty("result").GetProperty("standardError").GetString())
            |> should equal ("failure")
        finally
            DirectCommandProcess.delete directory

    [<Fact>]
    member _.``should sanitize and stream redirected human output before child completion``() =
        let directory = DirectCommandProcess.temporaryDirectory "direct command-stream"

        try
            let marker = Path.Combine(directory, "first")
            let release = Path.Combine(directory, "release")

            use child =
                DirectCommandProcess.start
                    directory
                    "stream"
                    [ "build" ]
                    [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH", marker
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CONTINUE_PATH", release ]

            let buffer = Array.zeroCreate<char> 5
            let first = child.StandardOutput.ReadAsync(buffer, 0, buffer.Length)
            DirectCommandProcess.waitForFile marker
            (first.Wait 5000) |> should equal true
            (String buffer) |> should equal ("first")
            File.WriteAllText(release, "continue")
            (child.WaitForExit 10000) |> should equal true
            (child.StandardOutput.ReadToEnd()) |> should equal ("second")
            (child.ExitCode) |> should equal (0)
        finally
            DirectCommandProcess.delete directory

    [<Fact>]
    member _.``should reap the command-owned child tree after interrupt cancellation``() =
        if not (OperatingSystem.IsWindows()) then
            let directory = DirectCommandProcess.temporaryDirectory "direct command-cancel"

            try
                let pidFile = Path.Combine(directory, "child.pid")

                use child =
                    DirectCommandProcess.start
                        directory
                        "tree"
                        [ "--json"; "build" ]
                        [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CHILD_PID_PATH", pidFile ]

                DirectCommandProcess.waitForFile pidFile
                let childPid = File.ReadAllText pidFile |> Int32.Parse
                use signal = Process.Start("kill", $"-INT {child.Id}")
                signal.WaitForExit()
                (signal.ExitCode) |> should equal (0)
                (child.WaitForExit 10000) |> should equal true
                (child.ExitCode) |> should equal (1)

                (DirectCommandProcess.diagnosticCode
                    { ExitCode = child.ExitCode
                      StandardOutput = child.StandardOutput.ReadToEnd()
                      StandardError = child.StandardError.ReadToEnd() })
                |> should equal ("cancelled")

                (fun () -> Process.GetProcessById childPid |> ignore)
                |> should throw typeof<ArgumentException>
                |> ignore
            finally
                DirectCommandProcess.delete directory
