namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type ProjectRenameTests() =
    [<Fact>]
    member _.``should isolate startup fatal and direct cli output in the built executable``() =
        let missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.slnx")
        use startup = WorkspaceRpcScenario.startWorkspaceRpc "solution" missing
        startup.StandardInput.Close()
        (startup.WaitForExit 5000) |> should equal true
        (startup.ExitCode) |> should equal (64)

        (WorkspaceRpcScenario.readRemaining startup.StandardOutput.BaseStream)
        |> should be Empty

        (startup.StandardError.ReadToEnd()) |> should haveSubstring ("startup failure")

        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-fatal"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            WorkspaceRpcScenario.save solution (SolutionModel())
            use fatal = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution
            fatal.StandardInput.BaseStream.Write [| 0xd4uy; 0uy; 0uy |]
            fatal.StandardInput.Close()
            (fatal.WaitForExit 5000) |> should equal true
            (fatal.ExitCode) |> should equal (65)

            (WorkspaceRpcScenario.readRemaining fatal.StandardOutput.BaseStream)
            |> should be Empty

            (fatal.StandardError.ReadToEnd()) |> should haveSubstring ("protocol failure")

            use orderlyEof = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            WorkspaceRpcScenario.send
                orderlyEof
                false
                (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

            WorkspaceRpcScenario.readFrame orderlyEof
            |> WorkspaceRpcScenario.response 1u
            |> ignore

            WorkspaceRpcScenario.send
                orderlyEof
                false
                (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

            WorkspaceRpcScenario.readFrame orderlyEof
            |> WorkspaceRpcScenario.response 2u
            |> ignore

            orderlyEof.StandardInput.Close()

            (orderlyEof.WaitForExit 5000) |> should equal true

            (orderlyEof.ExitCode) |> should equal (0)
            (orderlyEof.StandardError.ReadToEnd()) |> should equal (String.Empty)
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

        let invalidDirectory =
            WorkspaceRpcScenario.temporaryDirectory "pipe-invalid-initialize"

        try
            let solution = Path.Combine(invalidDirectory, "Demo.slnx")
            WorkspaceRpcScenario.save solution (SolutionModel())
            use invalidInitialize = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            WorkspaceRpcScenario.send
                invalidInitialize
                false
                (WorkspaceRpcScenario.request 1u "initialize" RpcValue.emptyMap)

            let initializeError, _ =
                WorkspaceRpcScenario.readFrame invalidInitialize
                |> WorkspaceRpcScenario.response 1u

            (initializeError.Value.Code) |> should equal ("invalid_params")
            invalidInitialize.StandardInput.Close()
            (invalidInitialize.WaitForExit 5000) |> should equal true
            (invalidInitialize.ExitCode) |> should equal (0)
            (invalidInitialize.StandardError.ReadToEnd()) |> should equal (String.Empty)
        finally
            if Directory.Exists invalidDirectory then
                Directory.Delete(invalidDirectory, true)

        let start = ProcessStartInfo WorkspaceRpcScenario.executable
        start.ArgumentList.Add "--json"
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        use direct = Process.Start start
        (direct) |> should not' (be Null)
        (direct.WaitForExit 5000) |> should equal true
        (direct.ExitCode) |> should not' (equal (0))
        (direct.StandardOutput.ReadToEnd().TrimStart()) |> should startWith ("{")
