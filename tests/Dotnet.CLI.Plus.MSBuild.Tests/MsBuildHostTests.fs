namespace Dotnet.CLI.Plus.MSBuild.Tests

#nowarn "3261"

open System
open System.Collections.Concurrent
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Threading
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.FakeHost
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Transport
open Xunit

module private Test =
    let temporaryDirectory name =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-msbuild-{name}-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let rec repositoryRoot (directory: string) =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            directory
        else
            match Directory.GetParent directory with
            | null -> failwith "Could not locate the repository root."
            | parent -> repositoryRoot parent.FullName

    let configuration =
        let baseDirectory = DirectoryInfo(AppContext.BaseDirectory)

        match baseDirectory.Parent with
        | null -> failwith "Could not determine the build configuration."
        | parent -> parent.Name

    let apphost =
        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.CLI.Plus.exe"
            else
                "Dotnet.CLI.Plus"

        Path.Combine(
            repositoryRoot AppContext.BaseDirectory,
            "src",
            "Dotnet.CLI.Plus",
            "bin",
            configuration,
            "net10.0",
            name
        )

    let fakeHostAssembly = typeof<FakeHostAssemblyMarker>.Assembly.Location
    let path (value: string) = WorkspaceArtifactPath.Create value
    let write (path: string) (contents: string) = File.WriteAllText(path, contents)

    let success outcome =
        match outcome with
        | WorkspaceOutcome.Success value -> value
        | WorkspaceOutcome.Failure failure ->
            failwithf "%s: %s" failure.Diagnostic.DiagnosticCode.Value failure.Diagnostic.Message

    let failure outcome =
        match outcome with
        | WorkspaceOutcome.Success _ -> failwith "Expected a typed workspace failure."
        | WorkspaceOutcome.Failure failure -> failure

    let settings onStarted =
        WorkerLaunchSettings(apphost, null, "dotnet", onStarted)

    let client onStarted =
        new MsBuildEvaluationClient(settings onStarted)

    let writeGlobalJson directory version =
        let contents =
            sprintf """{"sdk":{"version":"%s","rollForward":"disable","allowPrerelease":false}}""" version

        write (Path.Combine(directory, "global.json")) contents

    let simpleProject directory name extension =
        let project = Path.Combine(directory, name + extension)

        write
            project
            """
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
"""

        project

    let currentSdkVersion workingDirectory =
        let start = ProcessStartInfo("dotnet")
        start.WorkingDirectory <- workingDirectory
        start.ArgumentList.Add "--version"
        start.RedirectStandardOutput <- true
        start.UseShellExecute <- false
        use child = Process.Start start
        child.WaitForExit()
        child.StandardOutput.ReadToEnd().Trim()

    let installedSdks () =
        let start = ProcessStartInfo("dotnet")
        start.ArgumentList.Add "--list-sdks"
        start.RedirectStandardOutput <- true
        start.UseShellExecute <- false
        use child = Process.Start start
        let output = child.StandardOutput.ReadToEnd()
        child.WaitForExit()

        output.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.choose (fun line ->
            let parts = line.Split(" [", StringSplitOptions.TrimEntries)
            if parts.Length = 2 then Some parts[0] else None)

    let processExists processId =
        try
            use child = Process.GetProcessById processId
            not child.HasExited
        with :? ArgumentException ->
            false

    let request id name parameters =
        RpcCodec.encodeFrame (Request(id, name, parameters))

    let startWorker toolsetPath =
        let start = ProcessStartInfo(apphost)
        start.ArgumentList.Add "internal"
        start.ArgumentList.Add "msbuild-host"
        start.ArgumentList.Add "--toolset"
        start.ArgumentList.Add toolsetPath
        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false
        Process.Start start

    let send (child: Process) bytes =
        child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
        child.StandardInput.BaseStream.Flush()

    let readFrame (child: Process) =
        let bytes = ResizeArray<byte>()
        let mutable frame = None

        while frame.IsNone do
            let value = child.StandardOutput.BaseStream.ReadByte()

            if value < 0 then
                failwith "Worker stdout ended before a complete frame."

            bytes.Add(byte value)

            match RpcCodec.tryReadValueLength RpcCodec.secureLimits (bytes.ToArray()) with
            | Error RpcDecodeError.Incomplete -> ()
            | Error error -> failwithf "Invalid worker frame: %A" error
            | Ok length when length = bytes.Count ->
                match RpcCodec.decodeFrame RpcCodec.secureLimits (bytes.ToArray()) with
                | Ok(RpcFrameDecodeResult.Frame decoded) -> frame <- Some decoded
                | decoded -> failwithf "Invalid worker frame: %A" decoded
            | Ok _ -> failwith "Worker frame length was inconsistent."

        frame.Value

    let response expectedId =
        function
        | Response(id, error, result) when id = expectedId -> error, result
        | frame -> failwithf "Expected response %d, got %A" expectedId frame

    let currentToolset directory =
        DotnetSdkDiscovery.DiscoverAsync(path directory, "dotnet", CancellationToken.None)
        |> _.GetAwaiter().GetResult()
        |> success

type MsBuildHostTests() =
    [<Fact>]
    member _.``rich project snapshot is dimensional invalidatable and evaluation-only``() =
        let directory = Test.temporaryDirectory "rich"

        try
            Directory.CreateDirectory(Path.Combine(directory, "lib")) |> ignore
            Directory.CreateDirectory(Path.Combine(directory, "analyzers")) |> ignore
            Test.write (Path.Combine(directory, "lib", "Thing.dll")) String.Empty
            Test.write (Path.Combine(directory, "analyzers", "Rules.dll")) String.Empty
            Test.write (Path.Combine(directory, "Linked.cs")) "class Linked {}"
            Test.write (Path.Combine(directory, "Nine.cs")) "class Nine {}"

            Test.write
                (Path.Combine(directory, "Other.csproj"))
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"

            Test.write
                (Path.Combine(directory, "Directory.Build.props"))
                "<Project><PropertyGroup><ImportedProperty>imported</ImportedProperty></PropertyGroup></Project>"

            Test.write
                (Path.Combine(directory, "Directory.Packages.props"))
                "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Example.Package\" Version=\"2.0.0\" /></ItemGroup></Project>"

            let project = Path.Combine(directory, "Demo.csproj")

            Test.write
                project
                """
<Project Sdk="Microsoft.NET.Sdk" DefaultTargets="Marker">
  <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'"><ConditionalProperty>eight</ConditionalProperty></PropertyGroup>
  <ItemGroup>
    <Compile Include="Linked.cs"><Link>Virtual/Linked.cs</Link></Compile>
    <Compile Include="Generated/**/*.cs" />
    <Compile Include="Nine.cs" Condition="'$(TargetFramework)' == 'net9.0'" />
    <ProjectReference Include="Other.csproj" />
    <Reference Include="Thing"><HintPath>lib/Thing.dll</HintPath></Reference>
    <PackageReference Include="Example.Package" />
    <Analyzer Include="analyzers/Rules.dll" />
  </ItemGroup>
  <Target Name="Marker" BeforeTargets="Build"><WriteLinesToFile File="marker-ran.txt" Lines="ran" /></Target>
</Project>
"""

            let client = Test.client null

            let snapshot =
                client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()
                |> Test.success

            Assert.Equal(3, snapshot.Dimensions.Length)
            let outer = snapshot.Dimensions |> Seq.find _.IsOuterBuild

            let net8 =
                snapshot.Dimensions
                |> Seq.find (fun dimension ->
                    dimension.TargetFramework.HasValue
                    && dimension.TargetFramework.Value.Value = "net8.0")

            let net9 =
                snapshot.Dimensions
                |> Seq.find (fun dimension ->
                    dimension.TargetFramework.HasValue
                    && dimension.TargetFramework.Value.Value = "net9.0")

            Assert.Contains(
                outer.Properties,
                fun property -> property.Name = "ImportedProperty" && property.Value = "imported"
            )

            Assert.Contains(
                net8.Properties,
                fun property -> property.Name = "ConditionalProperty" && property.Value = "eight"
            )

            Assert.DoesNotContain(net8.Items, fun item -> item.EvaluatedInclude = "Nine.cs")
            Assert.Contains(net9.Items, fun item -> item.EvaluatedInclude = "Nine.cs")

            Assert.Contains(
                net8.Items,
                fun item ->
                    item.Metadata
                    |> Seq.exists (fun metadata -> metadata.Name = "Link" && metadata.Value = "Virtual/Linked.cs")
            )

            Assert.Contains(
                net8.ProjectReferences,
                fun reference -> reference.ResolvedPath.Value.EndsWith("Other.csproj", StringComparison.Ordinal)
            )

            Assert.Contains(
                net8.References,
                fun reference -> reference.ResolvedPath.Value.EndsWith("Thing.dll", StringComparison.Ordinal)
            )

            Assert.Contains(net8.Packages, fun package -> package.Id = "Example.Package" && package.Version = "2.0.0")

            Assert.Contains(
                net8.Analyzers,
                fun analyzer -> analyzer.Value.EndsWith("Rules.dll", StringComparison.Ordinal)
            )

            Assert.Contains(
                snapshot.Imports,
                fun import -> import.Value.EndsWith("Directory.Build.props", StringComparison.Ordinal)
            )

            Assert.Contains(
                snapshot.Imports,
                fun import -> import.Value.EndsWith("Directory.Packages.props", StringComparison.Ordinal)
            )

            Assert.Contains(snapshot.GlobRoots, fun root -> root.Value = Path.Combine(directory, "Generated"))

            Assert.Contains(
                snapshot.Diagnostics,
                fun diagnostic -> diagnostic.DiagnosticCode.Value = "msbuild.assets_missing"
            )

            Assert.False(File.Exists(Path.Combine(directory, "marker-ran.txt")))
            Assert.False(Directory.Exists(Path.Combine(directory, "obj")))

            let unrelatedPath =
                Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt")

            let unrelated =
                client.InvalidateAsync([ Test.path unrelatedPath ]).GetAwaiter().GetResult()
                |> Test.success

            Assert.Equal(MsBuildInvalidationKind.None, unrelated)
            Directory.CreateDirectory(Path.Combine(directory, "Generated")) |> ignore
            let generated = Path.Combine(directory, "Generated", "New.cs")
            Test.write generated "class New {}"

            let invalidated =
                client.InvalidateAsync([ Test.path generated ]).GetAwaiter().GetResult()
                |> Test.success

            Assert.Equal(MsBuildInvalidationKind.ProjectOrImport, invalidated)

            let refreshed =
                client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()
                |> Test.success

            Assert.Contains(
                refreshed.Dimensions |> Seq.collect _.Items,
                fun item ->
                    item.EvaluatedInclude.EndsWith("Generated/New.cs", StringComparison.Ordinal)
                    || item.EvaluatedInclude.EndsWith("Generated\\New.cs", StringComparison.Ordinal)
            )

            let directoryProps = Path.Combine(directory, "Directory.Build.props")

            Test.write
                directoryProps
                "<Project><PropertyGroup><ImportedProperty>changed</ImportedProperty></PropertyGroup></Project>"

            let importInvalidation =
                client.InvalidateAsync([ Test.path directoryProps ]).GetAwaiter().GetResult()
                |> Test.success

            Assert.Equal(MsBuildInvalidationKind.ProjectOrImport, importInvalidation)

            let imported =
                client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()
                |> Test.success

            Assert.Contains(
                imported.Dimensions |> Seq.collect _.Properties,
                fun property -> property.Name = "ImportedProperty" && property.Value = "changed"
            )

            client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``managed languages use Core full capabilities and unknown projects are read-only``() =
        let directory = Test.temporaryDirectory "capabilities"

        try
            let projects =
                [ Test.simpleProject directory "FSharp" ".fsproj", WorkspaceCapabilityProfile.Full
                  Test.simpleProject directory "CSharp" ".csproj", WorkspaceCapabilityProfile.Full
                  Test.simpleProject directory "VisualBasic" ".vbproj", WorkspaceCapabilityProfile.Full
                  let unknown = Path.Combine(directory, "Unknown.proj")
                  Test.write unknown "<Project><PropertyGroup><Value>readable</Value></PropertyGroup></Project>"
                  unknown, WorkspaceCapabilityProfile.UnknownProjectSystem ]

            let client = Test.client null

            for project, expectedProfile in projects do
                let outcome =
                    client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()

                let snapshot =
                    match outcome with
                    | WorkspaceOutcome.Success value -> value
                    | WorkspaceOutcome.Failure failure ->
                        failwithf
                            "%s: %s: %s"
                            project
                            failure.Diagnostic.DiagnosticCode.Value
                            failure.Diagnostic.Message

                Assert.Equal(expectedProfile, snapshot.CapabilityProfile)
                Assert.Contains(WorkspaceCapabilityId.Read, snapshot.Capabilities)

                let supportsWrite = snapshot.Capabilities.Contains(WorkspaceCapabilityId.Write)
                Assert.Equal((expectedProfile = WorkspaceCapabilityProfile.Full), supportsWrite)

            client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``private host enforces strict initialize profile isolation and shutdown``() =
        let directory = Test.temporaryDirectory "protocol"

        try
            let toolset = Test.currentToolset directory
            use worker = Test.startWorker toolset.ToolsetPath.Value

            let malformed =
                RpcValue.map
                    [ "profile", RpcValue.String "dotnet-cli-plus/msbuild"
                      "protocolVersion", RpcValue.map [ "major", RpcValue.Integer 1L ]
                      "limits", RpcValue.map [ "maxFrameBytes", RpcValue.Integer 1024L ] ]

            let wrongProfile =
                RpcValue.map
                    [ "profile", RpcValue.String "dotnet-cli-plus/workspace"
                      "protocolVersion", RpcValue.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                      "limits", RpcValue.map [ "maxFrameBytes", RpcValue.Integer 1024L ] ]

            let extraLimit =
                RpcValue.map
                    [ "profile", RpcValue.String "dotnet-cli-plus/msbuild"
                      "protocolVersion", RpcValue.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                      "limits", RpcValue.map [ "maxFrameBytes", RpcValue.Integer 1024L; "extra", RpcValue.Integer 1L ] ]

            for id, parameters in [ 1u, malformed; 2u, wrongProfile; 3u, extraLimit ] do
                Test.send worker (Test.request id "initialize" parameters)
                let error, _ = Test.readFrame worker |> Test.response id
                Assert.Equal("invalid_params", error.Value.Code)

            Test.send worker (Test.request 4u "initialize" (WorkerProtocol.InitializeRequest 4096))
            let initializeError, initializeResult = Test.readFrame worker |> Test.response 4u
            Assert.True(initializeError.IsNone)
            Assert.Equal(4096, WorkerProtocol.DecodeInitializeResult initializeResult)

            for id, methodName in [ 5u, "workspace/root"; 6u, "workspace/exportChunk"; 7u, "command/list" ] do
                Test.send worker (Test.request id methodName RpcValue.emptyMap)
                let error, _ = Test.readFrame worker |> Test.response id
                Assert.Equal("unknown_method", error.Value.Code)

            Test.send worker (Test.request 8u "shutdown" RpcValue.emptyMap)
            let shutdownError, shutdownResult = Test.readFrame worker |> Test.response 8u
            Assert.True(shutdownError.IsNone)
            WorkerProtocol.ValidateShutdownResult shutdownResult
            worker.StandardInput.Close()
            Assert.True(worker.WaitForExit 5000)
            Assert.Equal(0, worker.ExitCode)
            Assert.Equal(String.Empty, worker.StandardError.ReadToEnd())
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``refresh waits for active evaluation and disposal is idempotent``() =
        let directory = Test.temporaryDirectory "lifetime"

        try
            let project = Test.simpleProject directory "Lifetime" ".csproj"
            use releaseFirstLaunch = new ManualResetEventSlim(false)

            let firstStarted =
                Threading.Tasks.TaskCompletionSource<unit>(
                    Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                )

            let firstExit =
                Threading.Tasks.TaskCompletionSource<int>(
                    Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                )

            let secondExit =
                Threading.Tasks.TaskCompletionSource<int>(
                    Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                )

            let mutable launchCount = 0

            let started =
                Action<Process>(fun child ->
                    let launch = Interlocked.Increment(&launchCount)
                    child.EnableRaisingEvents <- true

                    child.Exited.Add(fun _ ->
                        let completion = if launch = 1 then firstExit else secondExit
                        completion.TrySetResult child.ExitCode |> ignore)

                    if launch = 1 then
                        firstStarted.TrySetResult() |> ignore
                        releaseFirstLaunch.Wait())

            let client = Test.client started

            try
                let evaluation = client.EvaluateAsync(Test.path project, Test.path directory)

                firstStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                let refresh = client.RefreshAsync()
                Assert.False(refresh.IsCompleted)
                releaseFirstLaunch.Set()

                evaluation.GetAwaiter().GetResult() |> Test.success |> ignore
                refresh.GetAwaiter().GetResult()
                Assert.Equal(0, firstExit.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())

                client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()
                |> Test.success
                |> ignore

                client.DisposeAsync().AsTask().GetAwaiter().GetResult()
                client.DisposeAsync().AsTask().GetAwaiter().GetResult()
                Assert.Equal(0, secondExit.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())
                Assert.Equal(2, launchCount)

                let closed =
                    client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()
                    |> Test.failure

                Assert.Equal("msbuild.worker_closed", closed.Diagnostic.DiagnosticCode.Value)
                Assert.Equal(2, launchCount)
            finally
                releaseFirstLaunch.Set()
                client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``cancellation kills and reaps the worker``() =
        let directory = Test.temporaryDirectory "cancel"

        try
            let project = Test.simpleProject directory "Cancel" ".csproj"
            use cancellation = new CancellationTokenSource()
            let processIds = ConcurrentBag<int>()

            let client =
                Test.client (
                    Action<Process>(fun child ->
                        processIds.Add child.Id
                        cancellation.Cancel())
                )

            let failure =
                client
                    .EvaluateAsync(Test.path project, Test.path directory, cancellation.Token)
                    .GetAwaiter()
                    .GetResult()
                |> Test.failure

            Assert.True(failure.IsCancelled)
            Assert.All(processIds, fun processId -> Assert.False(Test.processExists processId))
            client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``transport crash retries once disables and refresh recovers``() =
        let directory = Test.temporaryDirectory "crash"

        try
            let project = Test.simpleProject directory "Crash" ".csproj"
            let mutable kill = true
            let launches = ConcurrentBag<int>()

            let client =
                Test.client (
                    Action<Process>(fun child ->
                        launches.Add child.Id

                        if kill then
                            child.Kill(true))
                )

            let crashed =
                client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()
                |> Test.failure

            Assert.Equal("msbuild.worker_crashed", crashed.Diagnostic.DiagnosticCode.Value)
            Assert.Equal(2, launches.Count)

            let disabled =
                client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()
                |> Test.failure

            Assert.Equal("msbuild.worker_disabled", disabled.Diagnostic.DiagnosticCode.Value)
            Assert.Equal(2, launches.Count)
            client.RefreshAsync().GetAwaiter().GetResult()
            kill <- false

            client.EvaluateAsync(Test.path project, Test.path directory).GetAwaiter().GetResult()
            |> Test.success
            |> ignore

            Assert.Equal(3, launches.Count)
            client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``same toolset keeps distinct global json bindings and restarts the shared worker``() =
        let directory = Test.temporaryDirectory "bindings"

        try
            let version = Test.currentSdkVersion directory
            let first = Path.Combine(directory, "first")
            let second = Path.Combine(directory, "second")
            Directory.CreateDirectory first |> ignore
            Directory.CreateDirectory second |> ignore
            Test.writeGlobalJson first version
            Test.writeGlobalJson second version
            let firstProject = Test.simpleProject first "First" ".csproj"
            let secondProject = Test.simpleProject second "Second" ".csproj"
            let launches = ConcurrentBag<int>()
            let client = Test.client (Action<Process>(fun child -> launches.Add child.Id))

            let firstSnapshot =
                client.EvaluateAsync(Test.path firstProject, Test.path first).GetAwaiter().GetResult()
                |> Test.success

            let secondSnapshot =
                client.EvaluateAsync(Test.path secondProject, Test.path second).GetAwaiter().GetResult()
                |> Test.success

            Assert.Contains(firstSnapshot.WatchInputs, fun path -> path.Value = Path.Combine(first, "global.json"))

            Assert.Contains(secondSnapshot.WatchInputs, fun path -> path.Value = Path.Combine(second, "global.json"))

            Assert.Equal(1, launches.Count)

            let kind =
                client.InvalidateAsync([ Test.path (Path.Combine(first, "global.json")) ]).GetAwaiter().GetResult()
                |> Test.success

            Assert.Equal(MsBuildInvalidationKind.ToolsetSelection, kind)

            client.EvaluateAsync(Test.path secondProject, Test.path second).GetAwaiter().GetResult()
            |> Test.success
            |> ignore

            client.EvaluateAsync(Test.path firstProject, Test.path first).GetAwaiter().GetResult()
            |> Test.success
            |> ignore

            Assert.Equal(2, launches.Count)
            client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``installed SDKs use isolated workers and unavailable SDK is typed``() =
        let directory = Test.temporaryDirectory "sdks"

        try
            let available =
                Test.installedSdks ()
                |> Array.filter (fun version -> version = "8.0.422" || version = "9.0.315" || version = "10.0.301")

            let launches = ConcurrentBag<int>()
            let client = Test.client (Action<Process>(fun child -> launches.Add child.Id))

            for version in available do
                let workspace = Path.Combine(directory, version)
                Directory.CreateDirectory workspace |> ignore
                Test.writeGlobalJson workspace version
                let project = Test.simpleProject workspace "Sdk" ".csproj"

                client.EvaluateAsync(Test.path project, Test.path workspace).GetAwaiter().GetResult()
                |> Test.success
                |> ignore

            Assert.Equal(available.Length, launches.Count)
            let missing = Path.Combine(directory, "missing")
            Directory.CreateDirectory missing |> ignore
            Test.writeGlobalJson missing "99.0.100"
            let missingProject = Test.simpleProject missing "Missing" ".csproj"

            let failure =
                client.EvaluateAsync(Test.path missingProject, Test.path missing).GetAwaiter().GetResult()
                |> Test.failure

            Assert.True(failure.IsExternalToolFailed)
            Assert.Equal("msbuild.sdk_selection_failed", failure.Diagnostic.DiagnosticCode.Value)
            client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``malformed missing and incompatible projects return distinct Core failures``() =
        let directory = Test.temporaryDirectory "failures"

        try
            let malformed = Path.Combine(directory, "Malformed.csproj")
            Test.write malformed "<Project><PropertyGroup>"
            let client = Test.client null

            let malformedFailure =
                client.EvaluateAsync(Test.path malformed, Test.path directory).GetAwaiter().GetResult()
                |> Test.failure

            Assert.True(malformedFailure.IsInvalidInput)
            Assert.Equal("msbuild.project_malformed", malformedFailure.Diagnostic.DiagnosticCode.Value)
            let missing = Test.path (Path.Combine(directory, "Missing.csproj"))

            let missingFailure =
                client.EvaluateAsync(missing, Test.path directory).GetAwaiter().GetResult()
                |> Test.failure

            Assert.True(missingFailure.IsNotFound)

            let selection =
                ToolsetSelection(Test.path (Path.Combine(directory, "not-a-toolset")), null)

            let worker = new WorkerClient(Test.settings null, selection)

            let incompatible =
                worker.EvaluateAsync(Test.path malformed, CancellationToken.None).GetAwaiter().GetResult()
                |> Test.failure

            Assert.True(incompatible.IsExternalToolFailed)
            Assert.Equal("msbuild.toolset_incompatible", incompatible.Diagnostic.DiagnosticCode.Value)
            worker.DisposeAsync().AsTask().GetAwaiter().GetResult()
            worker.DisposeAsync().AsTask().GetAwaiter().GetResult()

            let closedWorker =
                worker.EvaluateAsync(Test.path malformed, CancellationToken.None).GetAwaiter().GetResult()
                |> Test.failure

            Assert.Equal("msbuild.worker_closed", closedWorker.Diagnostic.DiagnosticCode.Value)
            client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``bounded stderr flood cannot deadlock worker restart``() =
        let directory = Test.temporaryDirectory "stderr"

        try
            let project = Test.simpleProject directory "Flood" ".csproj"
            Environment.SetEnvironmentVariable("DOTNET_PLUS_FAKE_HOST_MODE", "stderr-flood")
            let settings = WorkerLaunchSettings("dotnet", Test.fakeHostAssembly, "dotnet", null)
            let client = new MsBuildEvaluationClient(settings)

            let failure =
                client
                    .EvaluateAsync(Test.path project, Test.path directory)
                    .WaitAsync(TimeSpan.FromSeconds 5.0)
                    .GetAwaiter()
                    .GetResult()
                |> Test.failure

            Assert.Equal("msbuild.worker_crashed", failure.Diagnostic.DiagnosticCode.Value)
            client.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            Environment.SetEnvironmentVariable("DOTNET_PLUS_FAKE_HOST_MODE", null)
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``normal test process and packaged apphost contain no MSBuild runtime assemblies``() =
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            fun assembly ->
                match assembly.GetName().Name with
                | null -> false
                | name ->
                    name = "Microsoft.Build"
                    || name = "Microsoft.Build.Framework"
                    || name.StartsWith("Microsoft.Build.Utilities", StringComparison.Ordinal)
                    || name.StartsWith("Microsoft.Build.Tasks", StringComparison.Ordinal)
        )

        let output = Path.GetDirectoryName(Test.apphost)

        let forbidden =
            Directory.EnumerateFiles(output, "Microsoft.Build*.dll")
            |> Seq.filter (fun path ->
                not (Path.GetFileName(path).Equals("Microsoft.Build.Locator.dll", StringComparison.OrdinalIgnoreCase)))
            |> Seq.toArray

        Assert.Empty forbidden
