namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Dotnet.CLI.Plus
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private Helpers =
    let hostLock = obj ()

    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-cli-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let deleteDirectory path =
        if Directory.Exists path then
            Directory.Delete(path, true)

    let fakeHost =
        typeof<Dotnet.CLI.Plus.FakeHost.FakeHostAssemblyMarker>.Assembly.Location

    let withFakeHost mode settings action =
        lock hostLock (fun () ->
            let names =
                [ "DOTNET_PLUS_DOTNET_HOST"
                  "DOTNET_PLUS_DOTNET_PREFIX"
                  "DOTNET_PLUS_FAKE_HOST_MODE"
                  "DOTNET_PLUS_FAKE_HOST_MARKER"
                  "DOTNET_PLUS_FAKE_HOST_CHILD_PID" ]

            let previous =
                names |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)

            Environment.SetEnvironmentVariable("DOTNET_PLUS_DOTNET_HOST", "dotnet")
            Environment.SetEnvironmentVariable("DOTNET_PLUS_DOTNET_PREFIX", fakeHost)
            Environment.SetEnvironmentVariable("DOTNET_PLUS_FAKE_HOST_MODE", mode)

            settings
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

            try
                action ()
            finally
                previous
                |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value)))

    let runFake mode settings arguments =
        withFakeHost mode settings (fun () -> CliBroker.ExecuteAsync(arguments, CancellationToken.None).Result)

    let saveSolution path =
        let serializer = SolutionSerializers.GetSerializerByMoniker path
        let model = SolutionModel()
        serializer.SaveAsync(path, model, CancellationToken.None).GetAwaiter().GetResult()

    let waitForFile path =
        let deadline = DateTime.UtcNow.AddSeconds 3

        while not (File.Exists path) && DateTime.UtcNow < deadline do
            Thread.Sleep 20

        Assert.True(File.Exists path, $"Expected {path} to be created.")

type CliBrokerTests() =
    [<Fact>]
    member _.``preserves child arguments including a sentinel literal json option``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, "Demo.sln")
            Helpers.saveSolution solution

            let result =
                Helpers.runFake
                    "capture"
                    []
                    [| "--json"
                       "sln"
                       solution
                       "list"
                       "--"
                       "a value with spaces"
                       "--json"
                       "a\"quote" |]

            Assert.True(result.Success)
            Assert.Equal("solution", result.CommandId)
            use arguments = JsonDocument.Parse(result.Result.StandardOutput)

            let captured =
                arguments.RootElement.EnumerateArray()
                |> Seq.map (fun argument -> argument.GetString() |> Option.ofObj |> Option.defaultValue "")
                |> Seq.toArray

            let expected =
                [| "solution"
                   solution
                   "list"
                   "--"
                   "a value with spaces"
                   "--json"
                   "a\"quote" |]

            if captured <> expected then
                failwith "The fake host did not receive the exact child argument list."
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``rejects unsupported commands without launching a host``() =
        let result = CliBroker.ExecuteAsync([| "publish" |], CancellationToken.None).Result
        Assert.False(result.Success)
        Assert.Equal("unsupported_capability", Assert.Single(result.Diagnostics).Code)
        Assert.Null(result.ExternalExitCode)

    [<Fact>]
    member _.``json failure cases emit one envelope and no stderr``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let cases =
                [ [| "--json" |]
                  [| "--json"; "publish" |]
                  [| "--json"
                     "solution"
                     Path.Combine(directory, "missing.sln")
                     "add"
                     "project.csproj" |] ]

            for arguments in cases do
                let result =
                    if arguments[1..] |> Array.tryHead = Some "solution" then
                        Helpers.runFake "capture" [] arguments
                    else
                        CliBroker.ExecuteAsync(arguments, CancellationToken.None).Result

                let output = new StringWriter()
                let error = new StringWriter()
                CliBroker.Render(result, true, output, error) |> ignore
                use document = JsonDocument.Parse(output.ToString())
                Assert.False(document.RootElement.GetProperty("success").GetBoolean())
                Assert.Equal("", error.ToString())
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``json rendering is one document with structured child capture and empty stderr``() =
        let result = Helpers.runFake "capture" [] [| "build"; "--no-restore" |]
        let output = new StringWriter()
        let error = new StringWriter()
        let exitCode = CliBroker.Render(result, true, output, error)
        use document = JsonDocument.Parse(output.ToString())

        Assert.Equal(0, exitCode)
        Assert.Equal("build", document.RootElement.GetProperty("commandId").GetString())
        Assert.Equal("", error.ToString())

        Assert.Equal(
            result.Result.StandardOutput,
            document.RootElement.GetProperty("result").GetProperty("standardOutput").GetString()
        )

        let mutable topLevelOutput = Unchecked.defaultof<JsonElement>
        Assert.False(document.RootElement.TryGetProperty("standardOutput", &topLevelOutput))

    [<Fact>]
    member _.``human rendering strips ansi for redirected writers``() =
        let result =
            { CommandId = "build"
              Success = true
              Revision = None
              Result =
                { Summary = Some "completed"
                  ChildArguments = [ "build" ]
                  StandardOutput = "\u001b[31mchild output\u001b[0m\n"
                  StandardError = "" }
              Diagnostics = []
              ExternalExitCode = Some 0
              StandardOutput = "\u001b[31mchild output\u001b[0m\n"
              StandardError = "" }

        let output = new StringWriter()
        CliBroker.Render(result, false, output, new StringWriter()) |> ignore

        if
            output.ToString().Contains("\u001b", StringComparison.Ordinal)
            || output.ToString().Contains("[31m", StringComparison.Ordinal)
        then
            failwith "Redirected human output retained ANSI formatting."

    [<Fact>]
    member _.``maps a nonzero child exit code``() =
        let result = Helpers.runFake "failure" [] [| "build"; "--no-restore" |]
        Assert.False(result.Success)
        Assert.Equal(Some 23, result.ExternalExitCode)
        Assert.Equal("external_tool_failed", Assert.Single(result.Diagnostics).Code)
        Assert.Equal("failure", result.Result.StandardError)

    [<Fact>]
    member _.``zero exit with failed refreshed verification fails``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let missing = Path.Combine(directory, "missing.sln")

            let result =
                Helpers.runFake "capture" [] [| "solution"; missing; "add"; "project.csproj" |]

            Assert.False(result.Success)
            Assert.Equal(Some 0, result.ExternalExitCode)
            Assert.Equal("internal_error", Assert.Single(result.Diagnostics).Code)
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``package verification accepts an idempotent requested effect``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "Demo.fsproj")

            File.WriteAllText(
                project,
                "<Project><ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup></Project>"
            )

            let result =
                Helpers.runFake "capture" [] [| "package"; "add"; "Example.Package"; "--project"; project |]

            Assert.True(result.Success)
            Assert.Equal(Some 0, result.ExternalExitCode)
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``solution filter mutation is rejected before fake host launch``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let marker = Path.Combine(directory, "launched")

            let result =
                Helpers.runFake
                    "marker"
                    [ "DOTNET_PLUS_FAKE_HOST_MARKER", marker ]
                    [| "solution"; "read-only.slnf"; "add"; "project.csproj" |]

            Assert.False(result.Success)
            Assert.Equal("unsupported_capability", Assert.Single(result.Diagnostics).Code)
            Assert.False(File.Exists marker)
        finally
            Helpers.deleteDirectory directory

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``legacy add directory works for sln and slnx``(extension: string) =
        let directory = Helpers.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, $"Demo{extension}")
            let folder = Directory.CreateDirectory(Path.Combine(directory, "src", "nested"))
            Helpers.saveSolution solution

            let result =
                CliBroker
                    .ExecuteAsync([| "sln"; solution; "add"; "dir"; folder.FullName |], CancellationToken.None)
                    .Result

            Assert.True(result.Success)
            Assert.Equal("solution", result.CommandId)
            Assert.Equal(Some 0L, result.Revision)
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``target omitted solution list is verified against the current directory``() =
        let directory = Helpers.temporaryDirectory ()
        let original = Directory.GetCurrentDirectory()

        try
            let solution = Path.Combine(directory, "Demo.sln")
            Helpers.saveSolution solution
            Directory.SetCurrentDirectory directory
            let result = Helpers.runFake "capture" [] [| "solution"; "list" |]
            Assert.True(result.Success)
            Assert.Equal(Some 0L, result.Revision)
        finally
            Directory.SetCurrentDirectory original
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``cancellation kills and reaps the managed fake-host process tree``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let childPid = Path.Combine(directory, "child.pid")
            use cancellation = new CancellationTokenSource(300)

            let result =
                Helpers.withFakeHost "tree" [ "DOTNET_PLUS_FAKE_HOST_CHILD_PID", childPid ] (fun () ->
                    CliBroker.ExecuteAsync([| "build" |], cancellation.Token).Result)

            Helpers.waitForFile childPid
            Assert.False(result.Success)
            Assert.Equal("cancelled", Assert.Single(result.Diagnostics).Code)

            let exited =
                try
                    use child = Process.GetProcessById(int (File.ReadAllText(childPid).Trim()))
                    child.WaitForExit 3000 |> ignore
                    child.HasExited
                with :? ArgumentException ->
                    true

            Assert.True(exited)
        finally
            Helpers.deleteDirectory directory

    [<Fact>]
    member _.``actual sdk help smoke test uses locale neutral facts``() =
        let result =
            CliBroker.ExecuteAsync([| "solution"; "--help" |], CancellationToken.None).Result

        Assert.True(result.Success)
        Assert.Equal("solution", result.CommandId)
        Assert.Equal(Some 0, result.ExternalExitCode)
        Assert.NotEmpty(result.Result.StandardOutput)

    [<Fact>]
    member _.``actual sdk solution list smoke test verifies a refreshed read state``() =
        let directory = Helpers.temporaryDirectory ()
        let original = Directory.GetCurrentDirectory()

        try
            Helpers.saveSolution (Path.Combine(directory, "Smoke.sln"))
            Directory.SetCurrentDirectory directory

            let result =
                CliBroker.ExecuteAsync([| "solution"; "list" |], CancellationToken.None).Result

            Assert.True(result.Success)
            Assert.Equal(Some 0L, result.Revision)
            Assert.Equal(Some 0, result.ExternalExitCode)
        finally
            Directory.SetCurrentDirectory original
            Helpers.deleteDirectory directory
