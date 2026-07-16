namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.IO
open System.Text.Json
open System.Threading
open Dotnet.CLI.Plus
open Dotnet.CLI.Plus.Core
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

    let saveSolutionWithProject path projectPath =
        let serializer = SolutionSerializers.GetSerializerByMoniker path
        let model = SolutionModel()

        model.AddProject(projectPath, Path.GetFileNameWithoutExtension projectPath, null)
        |> ignore

        serializer.SaveAsync(path, model, CancellationToken.None).GetAwaiter().GetResult()

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
    member _.``malformed package project returns a single json failure envelope``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "Broken.fsproj")
            File.WriteAllText(project, "<Project>")

            let result =
                Helpers.fake "capture" [| "--json"; "package"; "add"; "Example.Package"; "--project"; project |]

            let output = new StringWriter()
            let error = new StringWriter()
            Assert.False(result.Success)
            Assert.Equal(1, Broker.Render result true output error)
            use document = JsonDocument.Parse(output.ToString())
            Assert.False(document.RootElement.GetProperty("success").GetBoolean())
            Assert.Equal("", error.ToString())
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``invalid package path is contained by direct broker execution``() =
        let result =
            Helpers.fake "capture" [| "package"; "add"; "Example.Package"; "--project"; "\u0000" |]

        Assert.False(result.Success)
        Assert.Equal("invalid_input", result.Diagnostics.Head.DiagnosticCode.Value)

    [<Fact>]
    member _.``double-star glob verifies an already present project``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "src", "Lib.fsproj")
            Directory.CreateDirectory(Path.GetDirectoryName project) |> ignore
            File.WriteAllText(project, "<Project />")
            let solution = Path.Combine(directory, "Demo.sln")
            Helpers.saveSolutionWithProject solution "src/Lib.fsproj"

            let result =
                Helpers.fake "capture" [| "solution"; solution; "add"; Path.Combine(directory, "**", "*.fsproj") |]

            Assert.True(result.Success)
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``zero match glob rejects before marker launch``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let solution = Path.Combine(directory, "Demo.sln")
            Helpers.saveSolution solution

            let result =
                Helpers.fake "marker" [| "solution"; solution; "add"; Path.Combine(directory, "none", "*.fsproj") |]

            Assert.False(result.Success)
            Assert.Equal("invalid_input", result.Diagnostics.Head.DiagnosticCode.Value)
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``post sentinel solution operand is verified``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "Lib.fsproj")
            File.WriteAllText(project, "<Project />")
            let solution = Path.Combine(directory, "Demo.sln")
            Helpers.saveSolutionWithProject solution "Lib.fsproj"
            let result = Helpers.fake "capture" [| "solution"; solution; "add"; "--"; project |]
            Assert.True(result.Success)
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``sensitive backing volume rejects case mismatched solution operand``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let actual = Path.Combine(directory, "Actual.fsproj")
            let mismatched = Path.Combine(directory, "actual.fsproj")
            File.WriteAllText(actual, "<Project />")
            let solution = Path.Combine(directory, "Demo.sln")
            Helpers.saveSolutionWithProject solution "Actual.fsproj"

            match HostFileSystemCaseDetector.DetectFromExistingPath solution with
            | HostFileSystemCaseSemantics.Insensitive ->
                Assert.True(BrokerTestHooks.PathEquals(HostFileSystemCaseSemantics.Insensitive, actual, mismatched))
            | _ ->
                let result = Helpers.fake "capture" [| "solution"; solution; "add"; mismatched |]
                let output = new StringWriter()
                let error = new StringWriter()
                Assert.False(result.Success)
                Assert.Equal(1, Broker.Render result true output error)
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``sensitive case semantics reject casing-only path mismatch``() =
        Assert.False(
            BrokerTestHooks.PathEquals(
                HostFileSystemCaseSemantics.Sensitive,
                "/workspace/Project.fsproj",
                "/workspace/project.fsproj"
            )
        )

    [<Fact>]
    member _.``insensitive case semantics accept casing-only path mismatch``() =
        Assert.True(
            BrokerTestHooks.PathEquals(
                HostFileSystemCaseSemantics.Insensitive,
                "/workspace/Project.fsproj",
                "/workspace/project.fsproj"
            )
        )

    [<Fact>]
    member _.``unknown command is invalid input``() =
        let result = Helpers.fake "capture" [| "publish" |]
        Assert.False(result.Success)
        Assert.Equal("invalid_input", result.Diagnostics.Head.DiagnosticCode.Value)

    [<Fact>]
    member _.``package help is forwarded without mutation verification``() =
        let result = Helpers.fake "capture" [| "package"; "add"; "--help" |]
        Assert.True(result.Success)

    [<Fact>]
    member _.``new dry run is read only``() =
        let result = Helpers.fake "capture" [| "new"; "console"; "--dry-run" |]
        Assert.True(result.Success)

    [<Fact>]
    member _.``nonzero child exit remains external failure``() =
        let result = Helpers.fake "failure" [| "build" |]
        Assert.False(result.Success)
        Assert.Equal(Some 23, result.ExternalExitCode)
        Assert.Equal("external_tool_failed", result.Diagnostics.Head.DiagnosticCode.Value)

    [<Fact>]
    member _.``package default project ambiguity is invalid input``() =
        let directory = Helpers.temporaryDirectory ()
        let current = Directory.GetCurrentDirectory()

        try
            File.WriteAllText(Path.Combine(directory, "One.fsproj"), "<Project />")
            File.WriteAllText(Path.Combine(directory, "Two.fsproj"), "<Project />")
            Directory.SetCurrentDirectory directory
            let result = Helpers.fake "capture" [| "package"; "add"; "Example.Package" |]
            Assert.False(result.Success)
            Assert.Equal("invalid_input", result.Diagnostics.Head.DiagnosticCode.Value)
        finally
            Directory.SetCurrentDirectory current
            Helpers.delete directory

    [<Fact>]
    member _.``reference verification uses exact project reference paths``() =
        let directory = Helpers.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "App.fsproj")
            let reference = Path.Combine(directory, "Lib.fsproj")

            File.WriteAllText(
                project,
                "<Project><ItemGroup><ProjectReference Include=\"Lib.fsproj\" /></ItemGroup></Project>"
            )

            let result =
                Helpers.fake "capture" [| "reference"; "add"; reference; "--project"; project |]

            Assert.True(result.Success)
        finally
            Helpers.delete directory

    [<Fact>]
    member _.``json envelope contains captured child arguments``() =
        let result = Helpers.fake "capture" [| "build"; "--no-restore" |]
        let output = new StringWriter()
        Broker.Render result true output (new StringWriter()) |> ignore
        use document = JsonDocument.Parse(output.ToString())

        let argument =
            document.RootElement.GetProperty("result").GetProperty("childArguments")[0]

        Assert.Equal("build", argument.GetString())

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
