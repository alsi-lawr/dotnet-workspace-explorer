namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open Xunit

[<Collection("Delegated dotnet processes")>]
type DotnetProcessLifecycleTests() =
    [<Fact>]
    member _.``should sanitize output and preserve exit mapping in the json failure envelope``() =
        let directory = DirectCommandProcess.temporaryDirectory "direct command-failure"

        try
            let result = DirectCommandProcess.run directory "failure" [ "--json"; "build" ] []
            Assert.Equal(23, result.ExitCode)
            Assert.Equal(String.Empty, result.StandardError)
            use document = DirectCommandProcess.json result

            let diagnostic =
                document.RootElement.GetProperty("diagnostics").EnumerateArray() |> Seq.head

            Assert.False(document.RootElement.GetProperty("success").GetBoolean())
            Assert.Equal(23, document.RootElement.GetProperty("externalExitCode").GetInt32())
            Assert.Equal("external_tool_failed", diagnostic.GetProperty("code").GetString())

            Assert.Equal(
                "failure",
                document.RootElement.GetProperty("result").GetProperty("standardError").GetString()
            )
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
            Assert.True(first.Wait 5000, "The first output chunk was not streamed.")
            Assert.Equal("first", String buffer)
            File.WriteAllText(release, "continue")
            Assert.True(child.WaitForExit 10000)
            Assert.Equal("second", child.StandardOutput.ReadToEnd())
            Assert.Equal(0, child.ExitCode)
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
                Assert.Equal(0, signal.ExitCode)
                Assert.True(child.WaitForExit 10000, "The interrupted direct command did not exit.")
                Assert.Equal(1, child.ExitCode)

                Assert.Equal(
                    "cancelled",
                    DirectCommandProcess.diagnosticCode
                        { ExitCode = child.ExitCode
                          StandardOutput = child.StandardOutput.ReadToEnd()
                          StandardError = child.StandardError.ReadToEnd() }
                )

                Assert.Throws<ArgumentException>(fun () ->
                    Process.GetProcessById childPid |> ignore)
                |> ignore
            finally
                DirectCommandProcess.delete directory
