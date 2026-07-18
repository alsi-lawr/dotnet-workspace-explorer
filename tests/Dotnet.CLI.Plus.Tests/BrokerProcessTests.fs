namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private BrokerProcess =
    type Result =
        { ExitCode: int
          StandardOutput: string
          StandardError: string }

    let temporaryDirectory name =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-{name}-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let rec repositoryRoot directory =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            directory
        else
            repositoryRoot (Directory.GetParent(directory).FullName)

    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let root = repositoryRoot AppContext.BaseDirectory

    let executable project =
        let name =
            if OperatingSystem.IsWindows() then
                $"{project}.exe"
            else
                project

        Path.Combine(root, "tests", project, "bin", configuration, "net10.0", name)

    let product =
        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.CLI.Plus.exe"
            else
                "Dotnet.CLI.Plus"

        Path.Combine(root, "src", "Dotnet.CLI.Plus", "bin", configuration, "net10.0", name)

    let copyFakeHost directory =
        let sourceDirectory = Path.GetDirectoryName(executable "Dotnet.CLI.Plus.FakeHost")
        let destination = Path.Combine(directory, "fake-host")
        Directory.CreateDirectory destination |> ignore

        for source in Directory.EnumerateFiles(sourceDirectory) do
            let target = Path.Combine(destination, Path.GetFileName source)
            File.Copy(source, target, true)

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(target, File.GetUnixFileMode source)

        Path.Combine(destination, Path.GetFileName(executable "Dotnet.CLI.Plus.FakeHost"))

    let saveSolution path projects =
        let model = SolutionModel()

        for project in projects do
            model.AddProject(project, Path.GetFileNameWithoutExtension project, null)
            |> ignore

        SolutionSerializers
            .GetSerializerByMoniker(path)
            .SaveAsync(path, model, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    let start directory mode arguments environment =
        let info = ProcessStartInfo(product)
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.WorkingDirectory <- directory
        info.Environment["DOTNET_HOST_PATH"] <- copyFakeHost directory
        info.Environment["DOTNET_PLUS_FAKE_HOST_MODE"] <- mode

        for name, value in environment do
            info.Environment[name] <- value

        for argument in arguments do
            info.ArgumentList.Add argument

        Process.Start info

    let run directory mode arguments environment =
        use child = start directory mode arguments environment
        Assert.NotNull child
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        Assert.True(child.WaitForExit(10000), "The CLI child did not exit.")

        { ExitCode = child.ExitCode
          StandardOutput = output.Result
          StandardError = error.Result }

    let json result =
        JsonDocument.Parse(result.StandardOutput)

    let success result =
        use document = json result
        document.RootElement.GetProperty("success").GetBoolean()

    let diagnosticCode result =
        use document = json result
        let diagnostics = document.RootElement.GetProperty("diagnostics")
        diagnostics[0].GetProperty("code").GetString()

    let waitForFile path =
        if not (File.Exists path) then
            use watcher =
                new FileSystemWatcher(Path.GetDirectoryName path, Path.GetFileName path)

            watcher.EnableRaisingEvents <- true

            Assert.False(watcher.WaitForChanged(WatcherChangeTypes.Created, 10000).TimedOut)

    let delete path =
        if Directory.Exists path then
            Directory.Delete(path, true)

type BrokerProcessTests() =
    [<Fact>]
    member _.``should reject unsafe mutation targets before launching dotnet``() =
        let directory = BrokerProcess.temporaryDirectory "broker-preflight"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let marker = Path.Combine(directory, "launched")
            BrokerProcess.saveSolution solution []

            let cases =
                [ [ "package"; "add"; "Example.Package"; "--project"; solution ], "invalid_input"
                  [ "reference"; "add"; "Other.fsproj"; "--project"; solution ], "invalid_input"
                  [ "solution"; "read-only.slnf"; "add"; "App.fsproj" ], "unsupported_capability"
                  [ "solution"; solution; "add"; Path.Combine(directory, "none", "*.fsproj") ], "invalid_input" ]

            for arguments, expectedCode in cases do
                let result =
                    BrokerProcess.run
                        directory
                        "marker"
                        ("--json" :: arguments)
                        [ "DOTNET_PLUS_FAKE_HOST_MARKER", marker ]

                Assert.False(BrokerProcess.success result)
                Assert.Equal(expectedCode, BrokerProcess.diagnosticCode result)
                Assert.False(File.Exists marker)
        finally
            BrokerProcess.delete directory

    [<Fact>]
    member _.``should require verified postconditions for package reference template file and output mutations``() =
        let directory = BrokerProcess.temporaryDirectory "broker-postconditions"

        try
            let project = Path.Combine(directory, "App.fsproj")
            let reference = Path.Combine(directory, "Other.fsproj")
            let source = Path.Combine(directory, "app.cs")
            File.WriteAllText(reference, "<Project />")

            File.WriteAllText(
                project,
                "<Project><ItemGroup><PackageReference Include=\"Example.Package\" Version=\"2.0.0\" /><ProjectReference Include=\"Other.fsproj\" /></ItemGroup></Project>"
            )

            File.WriteAllText(source, "#:package Example.Package@2.0.0\nConsole.WriteLine(1);")

            let home = Path.Combine(directory, "home")

            let cache =
                Path.Combine(home, ".templateengine", "dotnetcli", "test", "templatecache.json")

            Directory.CreateDirectory(Path.GetDirectoryName cache) |> ignore
            File.WriteAllText(cache, "{\"MountPointsInfo\":{\"Example.Template\":{}}}")

            let cases =
                [ "capture",
                  [ "package"
                    "add"
                    "Example.Package"
                    "--version"
                    "2.0.0"
                    "--project"
                    project ],
                  []
                  "capture", [ "reference"; "add"; reference; "--project"; project ], []
                  "capture", [ "package"; "add"; "Example.Package@2.0.0"; "--file"; source ], []
                  "capture", [ "new"; "install"; "Example.Template" ], [ "DOTNET_CLI_HOME", home ]
                  "create-output", [ "new"; "console"; "--output"; Path.Combine(directory, "created") ], [] ]

            for mode, arguments, environment in cases do
                let result = BrokerProcess.run directory mode ("--json" :: arguments) environment
                Assert.True(BrokerProcess.success result, result.StandardOutput)
        finally
            BrokerProcess.delete directory

    [<Fact>]
    member _.``should verify exact solution membership with glob sentinel and backing-volume case rules``() =
        let directory = BrokerProcess.temporaryDirectory "broker-paths"

        try
            let project = Path.Combine(directory, "src", "Actual.fsproj")
            Directory.CreateDirectory(Path.GetDirectoryName project) |> ignore
            File.WriteAllText(project, "<Project />")
            let solution = Path.Combine(directory, "Demo.sln")
            BrokerProcess.saveSolution solution [ "src/Actual.fsproj" ]

            for operand in [ Path.Combine(directory, "**", "*.fsproj"); "--"; project ] do
                let arguments =
                    if operand = "--" then
                        [ "--json"; "solution"; solution; "add"; "--"; project ]
                    else
                        [ "--json"; "solution"; solution; "add"; operand ]

                Assert.True(BrokerProcess.success (BrokerProcess.run directory "capture" arguments []))

            if
                Dotnet.CLI.Plus.Core.HostFileSystemCaseDetector.DetectFromExistingPath solution = Dotnet.CLI.Plus.Core.HostFileSystemCaseSemantics.Sensitive
            then
                let mismatched = Path.Combine(directory, "src", "actual.fsproj")

                let result =
                    BrokerProcess.run directory "capture" [ "--json"; "solution"; solution; "add"; mismatched ] []

                Assert.False(BrokerProcess.success result)
        finally
            BrokerProcess.delete directory

    [<Theory>]
    [<InlineData(".sln", "directory")>]
    [<InlineData(".slnx", "dir")>]
    member _.``should persist nested folders for legacy directory aliases without invoking dotnet``
        (extension: string, alias: string)
        =
        let directory = BrokerProcess.temporaryDirectory "broker-legacy"

        try
            let solution = Path.Combine(directory, $"Demo{extension}")
            let folder = Directory.CreateDirectory(Path.Combine(directory, "src", "nested"))
            let marker = Path.Combine(directory, "launched")
            BrokerProcess.saveSolution solution []

            let result =
                BrokerProcess.run
                    directory
                    "marker"
                    [ "--json"; "sln"; solution; "add"; alias; folder.FullName ]
                    [ "DOTNET_PLUS_FAKE_HOST_MARKER", marker ]

            Assert.True(BrokerProcess.success result)
            Assert.False(File.Exists marker)

            match Dotnet.CLI.Plus.Solution.SolutionStore.OpenAsync(solution).Result with
            | Dotnet.CLI.Plus.Core.Success workspace ->
                Assert.Contains(workspace.RootProjection.Folders, fun item -> item.Path = "/src/nested/")
            | outcome -> failwithf "Expected the persisted folder, got %A" outcome
        finally
            BrokerProcess.delete directory

    [<Fact>]
    member _.``should sanitize child output and preserve external exit mapping in the json failure envelope``() =
        let directory = BrokerProcess.temporaryDirectory "broker-failure"

        try
            let result = BrokerProcess.run directory "failure" [ "--json"; "build" ] []
            Assert.Equal(23, result.ExitCode)
            Assert.Equal(String.Empty, result.StandardError)
            use document = BrokerProcess.json result

            let diagnostic =
                document.RootElement.GetProperty("diagnostics").EnumerateArray() |> Seq.head

            Assert.False(document.RootElement.GetProperty("success").GetBoolean())
            Assert.Equal(23, document.RootElement.GetProperty("externalExitCode").GetInt32())
            Assert.Equal("external_tool_failed", diagnostic.GetProperty("code").GetString())
            Assert.Equal("failure", document.RootElement.GetProperty("result").GetProperty("standardError").GetString())
        finally
            BrokerProcess.delete directory

    [<Fact>]
    member _.``should sanitize and stream redirected human output before child completion``() =
        let directory = BrokerProcess.temporaryDirectory "broker-stream"

        try
            let marker = Path.Combine(directory, "first")
            let release = Path.Combine(directory, "release")

            use child =
                BrokerProcess.start
                    directory
                    "stream"
                    [ "build" ]
                    [ "DOTNET_PLUS_FAKE_HOST_MARKER", marker
                      "DOTNET_PLUS_FAKE_HOST_RELEASE", release ]

            let buffer = Array.zeroCreate<char> 5
            let first = child.StandardOutput.ReadAsync(buffer, 0, buffer.Length)
            BrokerProcess.waitForFile marker
            Assert.True(first.Wait(5000), "The first output chunk was not streamed.")
            Assert.Equal("first", String buffer)
            File.WriteAllText(release, "continue")
            Assert.True(child.WaitForExit(10000))
            Assert.Equal("second", child.StandardOutput.ReadToEnd())
            Assert.Equal(0, child.ExitCode)
        finally
            BrokerProcess.delete directory

    [<Fact>]
    member _.``should reap the broker-owned child tree after interrupt cancellation``() =
        if not (OperatingSystem.IsWindows()) then
            let directory = BrokerProcess.temporaryDirectory "broker-cancel"

            try
                let pidFile = Path.Combine(directory, "child.pid")

                use child =
                    BrokerProcess.start
                        directory
                        "tree"
                        [ "--json"; "build" ]
                        [ "DOTNET_PLUS_FAKE_HOST_CHILD_PID", pidFile ]

                BrokerProcess.waitForFile pidFile
                let childPid = File.ReadAllText(pidFile) |> Int32.Parse
                use signal = Process.Start("kill", $"-INT {child.Id}")
                signal.WaitForExit()
                Assert.Equal(0, signal.ExitCode)
                Assert.True(child.WaitForExit(10000), "The interrupted broker did not exit.")
                Assert.Equal(1, child.ExitCode)

                Assert.Equal(
                    "cancelled",
                    BrokerProcess.diagnosticCode
                        { ExitCode = child.ExitCode
                          StandardOutput = child.StandardOutput.ReadToEnd()
                          StandardError = child.StandardError.ReadToEnd() }
                )

                Assert.Throws<ArgumentException>(fun () -> Process.GetProcessById(childPid) |> ignore)
                |> ignore
            finally
                BrokerProcess.delete directory
