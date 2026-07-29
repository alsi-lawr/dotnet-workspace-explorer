namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Xml.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
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

[<Collection("Workspace scenarios")>]
type ProjectRenameTests() =
    [<Fact>]
    member _.``should isolate startup fatal and direct cli output in the built executable``() =
        let missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.slnx")
        use startup = WorkspaceRpcScenario.startWorkspaceRpc "solution" missing
        startup.StandardInput.Close()
        Assert.True(startup.WaitForExit 5000)
        Assert.Equal(64, startup.ExitCode)
        Assert.Empty(WorkspaceRpcScenario.readRemaining startup.StandardOutput.BaseStream)
        Assert.Contains("startup failure", startup.StandardError.ReadToEnd())

        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-fatal"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            WorkspaceRpcScenario.save solution (SolutionModel())
            use fatal = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution
            fatal.StandardInput.BaseStream.Write [| 0xd4uy; 0uy; 0uy |]
            fatal.StandardInput.Close()
            Assert.True(fatal.WaitForExit 5000)
            Assert.Equal(65, fatal.ExitCode)
            Assert.Empty(WorkspaceRpcScenario.readRemaining fatal.StandardOutput.BaseStream)
            Assert.Contains("protocol failure", fatal.StandardError.ReadToEnd())

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

            Assert.True(
                orderlyEof.WaitForExit 5000,
                "The watched workspace RPC did not exit after stdin closed."
            )

            Assert.Equal(0, orderlyEof.ExitCode)
            Assert.Equal(String.Empty, orderlyEof.StandardError.ReadToEnd())
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

            Assert.Equal("invalid_params", initializeError.Value.Code)
            invalidInitialize.StandardInput.Close()
            Assert.True(invalidInitialize.WaitForExit 5000)
            Assert.Equal(0, invalidInitialize.ExitCode)
            Assert.Equal(String.Empty, invalidInitialize.StandardError.ReadToEnd())
        finally
            if Directory.Exists invalidDirectory then
                Directory.Delete(invalidDirectory, true)

        let start = ProcessStartInfo WorkspaceRpcScenario.executable
        start.ArgumentList.Add "--json"
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        use direct = Process.Start start
        Assert.NotNull direct
        Assert.True(direct.WaitForExit 5000)
        Assert.NotEqual(0, direct.ExitCode)
        Assert.StartsWith("{", direct.StandardOutput.ReadToEnd().TrimStart())
