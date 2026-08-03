namespace Dotnet.WorkspaceExplorer.PackageExplorer.IntegrationTests

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open FsUnit.Xunit
open Xunit

module private PackageCommandProcessScenario =
    let rec private repositoryRoot directory =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            directory
        else
            match Directory.GetParent directory with
            | null -> failwith "Could not locate the repository root."
            | parent -> repositoryRoot parent.FullName

    let private root = repositoryRoot AppContext.BaseDirectory

    let private configuration =
        let parent = DirectoryInfo(AppContext.BaseDirectory).Parent
        if isNull parent then "Debug" else parent.Name

    let private scripted =
        let fileName =
            if OperatingSystem.IsWindows() then
                "Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet.exe"
            else
                "Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet"

        Path.Combine(
            root,
            "tests/integration/Support/ScriptedDotnet",
            "bin",
            configuration,
            "net10.0",
            fileName
        )

    let temporaryDirectory () =
        let path =
            Path.Combine(root, ".agent-workspace", "mtp", $"package-command-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let copyScriptedDotnet directory =
        let sourceDirectory = Path.GetDirectoryName scripted
        let destination = Path.Combine(directory, "scripted-dotnet")
        Directory.CreateDirectory destination |> ignore

        for source in Directory.EnumerateFiles sourceDirectory do
            let target = Path.Combine(destination, Path.GetFileName source)
            File.Copy(source, target, true)

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(target, File.GetUnixFileMode source)

        Path.Combine(destination, Path.GetFileName scripted)

    type EnvironmentScope(values: (string * string option) list) =
        let previous =
            values
            |> List.map (fun (name, _) ->
                name, Environment.GetEnvironmentVariable name |> Option.ofObj)

        do
            values
            |> List.iter (fun (name, value) ->
                Environment.SetEnvironmentVariable(name, value |> Option.toObj))

        interface IDisposable with
            member _.Dispose() =
                previous
                |> List.iter (fun (name, value) ->
                    Environment.SetEnvironmentVariable(name, value |> Option.toObj))

    let waitForFile path =
        if not (File.Exists path) then
            use watcher =
                new FileSystemWatcher(Path.GetDirectoryName path, Path.GetFileName path)

            watcher.EnableRaisingEvents <- true

            if not (File.Exists path) then
                watcher
                    .WaitForChanged(
                        WatcherChangeTypes.Created ||| WatcherChangeTypes.Renamed,
                        10000
                    )
                    .TimedOut
                |> should equal false

    let capturedArguments path =
        use document = JsonDocument.Parse(File.ReadAllText path)

        document.RootElement.EnumerateArray()
        |> Seq.map (fun element -> element.GetString())
        |> Seq.map (Option.ofObj >> Option.defaultValue "")
        |> Seq.toList

    let fingerprint path =
        use stream = File.OpenRead path
        $"f:{(FileInfo path).Length}:{Security.Cryptography.SHA256.HashData stream |> Convert.ToHexString}"

[<Collection("Package installed scenarios")>]
type PackageCommandProcessTests() =
    [<Theory>]
    [<InlineData("authentication-required", "authentication-required")>]
    [<InlineData("unauthorized", "unauthorized")>]
    member _.``scripted stock command maps source authorization failures to stable redacted outcomes``
        (mode: string, expected: string)
        =
        let directory = PackageCommandProcessScenario.temporaryDirectory ()

        try
            let host = PackageCommandProcessScenario.copyScriptedDotnet directory

            use _environment =
                new PackageCommandProcessScenario.EnvironmentScope(
                    [ "DOTNET_HOST_PATH", Some host
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", Some mode ]
                )

            let result =
                DotnetPackageOperations.run
                    directory
                    [| "package"
                       "update"
                       "Private.Package@2.0.0"
                       "--project"
                       Path.Combine(directory, "Private.csproj") |]
                    CancellationToken.None
                |> Async.RunSynchronously

            let expectedFailure =
                if expected = "authentication-required" then
                    DotnetPackageCommandFailure.AuthenticationRequired
                else
                    DotnetPackageCommandFailure.Unauthorized

            match result with
            | Ok() -> failwith "The scripted authorization failure unexpectedly succeeded."
            | Error actual -> actual |> should equal expectedFailure
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``scripted selected-SDK package add receives the exact framework and project selectors without a shell``
        ()
        =
        let directory = PackageCommandProcessScenario.temporaryDirectory ()

        try
            let host = PackageCommandProcessScenario.copyScriptedDotnet directory
            let capture = Path.Combine(directory, "capture.jsonl")
            let working = Path.Combine(directory, "working.txt")
            let project = Path.Combine(directory, "Example.csproj")

            File.WriteAllText(
                project,
                "<Project><ItemGroup><PackageReference Include=\"Example.Package\" Version=\"1.0.0\" /></ItemGroup></Project>"
            )

            use _environment =
                new PackageCommandProcessScenario.EnvironmentScope(
                    [ "DOTNET_HOST_PATH", Some host
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", Some "workspace-command"
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CAPTURE_PATH", Some capture
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_WORKING_DIRECTORY_PATH",
                      Some working ]
                )

            let arguments =
                [| "package"
                   "add"
                   "Example.Package"
                   "--version"
                   "2.0.0"
                   "--framework"
                   "net10.0"
                   "--project"
                   project |]

            match
                DotnetPackageOperations.run directory arguments CancellationToken.None
                |> Async.RunSynchronously
            with
            | Error failure -> failwithf "The scripted package command failed: %A" failure
            | Ok() -> ()

            PackageCommandProcessScenario.capturedArguments capture
            |> should equal (arguments |> Array.toList)

            File.ReadAllText working |> should equal directory
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``cancelling a scripted stock command terminates its process tree before returning cancelled``
        ()
        =
        let directory = PackageCommandProcessScenario.temporaryDirectory ()

        try
            let host = PackageCommandProcessScenario.copyScriptedDotnet directory
            let childPidPath = Path.Combine(directory, "child.pid")

            use _environment =
                new PackageCommandProcessScenario.EnvironmentScope(
                    [ "DOTNET_HOST_PATH", Some host
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", Some "tree"
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CHILD_PID_PATH", Some childPidPath ]
                )

            use cancellation = new CancellationTokenSource()

            let running =
                DotnetPackageOperations.run
                    directory
                    [| "package"
                       "update"
                       "Example.Package@2.0.0"
                       "--project"
                       Path.Combine(directory, "Example.csproj") |]
                    cancellation.Token
                |> Async.StartAsTask

            PackageCommandProcessScenario.waitForFile childPidPath
            let childPid = File.ReadAllText childPidPath |> Int32.Parse
            cancellation.Cancel()

            match running.GetAwaiter().GetResult() with
            | Ok() -> failwith "The cancelled package command unexpectedly succeeded."
            | Error failure -> failure |> should equal DotnetPackageCommandFailure.Cancelled

            let childExited =
                try
                    use child = Process.GetProcessById childPid
                    child.WaitForExit 5000
                with :? ArgumentException ->
                    true

            childExited |> should equal true
        finally
            Directory.Delete(directory, true)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``confirmed uninstall uses the real selected SDK for direct and central project ownership without a public feed``
        (central: bool)
        =
        let directory = PackageCommandProcessScenario.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "Example.csproj")
            let centralOwner = Path.Combine(directory, "Directory.Packages.props")

            File.WriteAllText(
                project,
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ManagePackageVersionsCentrally>$(Central)</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Example.Package" Version="$(PackageVersion)" />
  </ItemGroup>
</Project>
"""
                    .Replace("$(Central)", if central then "true" else "false")
                    .Replace(
                        " Version=\"$(PackageVersion)\"",
                        if central then "" else " Version=\"1.0.0\""
                    )
            )

            if central then
                File.WriteAllText(
                    centralOwner,
                    """
<Project>
  <ItemGroup>
    <PackageVersion Include="Example.Package" Version="1.0.0" />
  </ItemGroup>
</Project>
"""
                )

            let identity =
                PackageId.create "Example.Package" |> Result.defaultWith (failwithf "%A")

            let version = NuGetVersion.create "1.0.0" |> Result.defaultWith (failwithf "%A")

            let projectId =
                PackageProjectId.create project |> Result.defaultWith (failwithf "%A")

            let target = PackageTargetScope.Project projectId

            let current =
                if central then
                    InstalledPackageState.CentrallyManagedDirect(
                        PackageVersionSelection.Exact version,
                        version,
                        centralOwner
                    )
                else
                    InstalledPackageState.Direct(PackageVersionSelection.Exact version, version)

            let owners =
                if central then
                    NonEmptyList.create project [ centralOwner ]
                else
                    NonEmptyList.singleton project

            let impact =
                { Metadata = PackageMetadataImpact.Unknown
                  SourceMapping = PackageSourceMappingImpact.ApplyAllowed []
                  Restore =
                    PackageRestoreImpact.RequiredWithUnknownOutcome PackageGraphFreshness.Current }

            let targetPreview =
                PackageTargetPreview.create
                    target
                    (PackageTargetChange.Uninstall current)
                    owners
                    PackageGraphFreshness.Current
                    impact
                |> Result.defaultWith (failwithf "%A")

            let fingerprints =
                owners
                |> NonEmptyList.toList
                |> List.map (fun path -> path, PackageCommandProcessScenario.fingerprint path)
                |> Map

            let preview =
                PackagePreview.create
                    StringComparison.Ordinal
                    (RequestedPackageOperation.Uninstall identity)
                    (NonEmptyList.singleton targetPreview)
                    owners
                    "revision-1"
                    fingerprints
                |> Result.defaultWith (failwithf "%A")

            let confirmation =
                PackageConfirmation.create preview (PackagePreview.confirmationToken preview)
                |> Result.defaultWith (failwithf "%A")

            let currentPrecondition =
                { WorkspaceRevision = "revision-1"
                  FileFingerprints = fingerprints }

            let requests = ConcurrentDictionary<PackageRequestId, CancellationTokenSource>()
            let operations = ConcurrentDictionary<PackageOperationId, CancellationTokenSource>()

            let execution =
                PackageOperationExecution.createWith
                    requests
                    operations
                    { ReadPrecondition = fun _ -> async { return Ok currentPrecondition }
                      ReadUpdateBatchPrecondition = fun _ -> async { return Ok currentPrecondition }
                      RefreshInstalled = fun _ -> async { return Ok [] }
                      RunCommand = DotnetPackageOperations.run }

            let result =
                execution.Execute
                    { Id = PackageRequestId.newId ()
                      Target =
                        PackageWorkspaceTarget.directory directory
                        |> Result.defaultWith (failwithf "%A")
                      Value = confirmation }
                    ignore
                |> Async.RunSynchronously

            match result with
            | Error failure ->
                failwithf "%s: %s" (PackageFailure.code failure) (PackageFailure.message failure)
            | Ok applied ->
                applied.Entries
                |> List.map _.State
                |> should equal [ PackageExecutionState.Completed ]

            File.ReadAllText(project).Contains("PackageReference", StringComparison.Ordinal)
            |> should equal false

            if central then
                File.ReadAllText(centralOwner).Contains("PackageVersion", StringComparison.Ordinal)
                |> should equal true
        finally
            Directory.Delete(directory, true)
