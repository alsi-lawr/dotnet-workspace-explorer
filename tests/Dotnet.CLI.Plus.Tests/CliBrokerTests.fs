namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.IO
open System.Text.Json
open System.Threading
open Dotnet.CLI.Plus
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private Helpers =
    let gate = obj ()

    let fakeHost =
        typeof<Dotnet.CLI.Plus.FakeHost.FakeHostAssemblyMarker>.Assembly.Location

    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-cli-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let delete path =
        if Directory.Exists path then
            Directory.Delete(path, true)

    let saveSolution path =
        let serializer = SolutionSerializers.GetSerializerByMoniker path
        serializer.SaveAsync(path, SolutionModel(), CancellationToken.None).GetAwaiter().GetResult()

    let fake mode arguments =
        lock gate (fun () ->
            let prior = Environment.GetEnvironmentVariable "DOTNET_PLUS_FAKE_HOST_MODE"
            Environment.SetEnvironmentVariable("DOTNET_PLUS_FAKE_HOST_MODE", mode)

            try
                BrokerTestHooks.ExecuteWithHostAsync(arguments, "dotnet", fakeHost, CancellationToken.None).Result
            finally
                Environment.SetEnvironmentVariable("DOTNET_PLUS_FAKE_HOST_MODE", prior))

type CliBrokerTests() =
    [<Fact>]
    member _.``preserves sln argv and a sentinel json literal``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, "Demo.sln")
            Helpers.saveSolution solution

            let result =
                Helpers.fake "capture" [| "--json"; "sln"; solution; "list"; "--"; "--json"; "space value" |]

            Assert.True(result.Success)
            use document = JsonDocument.Parse(result.Payload.StandardOutput)

            let received =
                document.RootElement.EnumerateArray()
                |> Seq.map (fun value -> value.GetString())
                |> Seq.toArray

            Assert.Equal("solution", received[0])
            Assert.Equal("--json", received[4])
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``package ids ending slnf are not solution filters``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "Demo.fsproj")

            File.WriteAllText(
                project,
                "<Project><ItemGroup><PackageReference Include=\"Example.slnf\" /></ItemGroup></Project>"
            )

            let result =
                Helpers.fake "capture" [| "package"; "add"; "Example.slnf"; "--project"; project |]

            Assert.True(result.Success)
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``solution filter mutation is refused before host launch``() =
        let result =
            Helpers.fake "capture" [| "solution"; "read-only.slnf"; "add"; "project.fsproj" |]

        Assert.False(result.Success)
        Assert.Equal("unsupported_capability", result.Diagnostics.Head.DiagnosticCode.Value)

    [<Theory>]
    [<InlineData(".sln", "directory")>]
    [<InlineData(".slnx", "dir")>]
    member _.``legacy directory aliases update sln formats``(extension: string, alias: string) =
        let directory = Helpers.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, $"Demo{extension}")
            let folder = Directory.CreateDirectory(Path.Combine(directory, "src", "nested"))
            Helpers.saveSolution solution

            let result =
                Broker
                    .ExecuteAsync([| "sln"; solution; "add"; alias; folder.FullName |], Json, CancellationToken.None)
                    .Result

            Assert.True(result.Success)
            Assert.Equal("solution", result.CommandId)
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``zero child exit with failed verification renders process exit one``() =
        let result =
            Helpers.fake "capture" [| "solution"; "missing.sln"; "add"; "project.fsproj" |]

        let output = new StringWriter()
        let error = new StringWriter()
        Assert.Equal(1, Broker.Render result true output error)
        Assert.Contains("\"success\":false", output.ToString())
        Assert.Equal("", error.ToString())

    [<Fact>]
    member _.``json strips ansi and serializes full diagnostic fields``() =
        let result = Helpers.fake "failure" [| "build" |]
        let output = new StringWriter()
        Broker.Render result true output (new StringWriter()) |> ignore
        use document = JsonDocument.Parse(output.ToString())
        Assert.False(output.ToString().Contains("\u001b", StringComparison.Ordinal))
        let diagnostic = document.RootElement.GetProperty("diagnostics")[0]
        let mutable severity = Unchecked.defaultof<JsonElement>
        let mutable correlation = Unchecked.defaultof<JsonElement>
        Assert.True(diagnostic.TryGetProperty("severity", &severity))
        Assert.True(diagnostic.TryGetProperty("correlationId", &correlation))

    [<Fact>]
    member _.``actual sdk help and default solution list smoke tests succeed``() =
        let help =
            Broker.ExecuteAsync([| "new"; "--help" |], Json, CancellationToken.None).Result

        Assert.True(help.Success)
        let directory = Helpers.temporaryDirectory ()
        let current = Directory.GetCurrentDirectory()

        try
            Helpers.saveSolution (Path.Combine(directory, "Smoke.sln"))
            Directory.SetCurrentDirectory directory

            let listed =
                Broker.ExecuteAsync([| "solution"; "list" |], Json, CancellationToken.None).Result

            Assert.True(listed.Success)
            Assert.True(listed.Revision.IsSome)
        finally
            Directory.SetCurrentDirectory current
            Helpers.delete directory
