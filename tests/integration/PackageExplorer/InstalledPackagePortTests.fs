namespace Dotnet.WorkspaceExplorer.PackageExplorer.IntegrationTests

#nowarn "3261"
#nowarn "3262"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open FsUnit.Xunit
open Xunit

module private InstalledPortScenario =
    type Workspace =
        { Directory: string
          Project: string
          Solution: string }

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

    let private executable directory name =
        let fileName = if OperatingSystem.IsWindows() then name + ".exe" else name

        Path.Combine(root, directory, "bin", configuration, "net10.0", fileName)

    let product = executable "src/WorkspaceExplorer" "Dotnet.WorkspaceExplorer"

    let private scripted =
        executable
            "tests/integration/Support/ScriptedDotnet"
            "Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet"

    let temporaryDirectory name =
        let path =
            Path.Combine(
                root,
                ".agent-workspace",
                "mtp",
                $"package-installed-{name}-{Guid.NewGuid():N}"
            )

        Directory.CreateDirectory path |> ignore
        path

    let write (path: string) (contents: string) = File.WriteAllText(path, contents)

    let runDotnet directory arguments =
        let startInfo =
            ProcessStartInfo(
                FileName = "dotnet",
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo

        if isNull child then
            failwith "The dotnet process did not start."

        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()

        if child.ExitCode <> 0 then
            failwithf "dotnet failed (%d): %s%s" child.ExitCode output error

    let createWorkspace name =
        let directory = temporaryDirectory name
        let feed = Path.Combine(directory, "feed")
        Directory.CreateDirectory feed |> ignore

        write
            (Path.Combine(directory, "NuGet.Config"))
            $"""
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="{feed}" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"""

        let project = Path.Combine(directory, "Example.csproj")

        write
            project
            """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <BaseIntermediateOutputPath>obj\</BaseIntermediateOutputPath>
  </PropertyGroup>
</Project>
"""

        let solution = Path.Combine(directory, "Example.slnx")
        write solution "<Solution><Project Path=\"Example.csproj\" /></Solution>"
        runDotnet directory [ "restore"; project; "--nologo" ]

        { Directory = directory
          Project = project
          Solution = solution }

    let delete workspace =
        if Directory.Exists workspace.Directory then
            Directory.Delete(workspace.Directory, true)

    let evaluatorFactory () =
        let launch = EvaluationWorkerLaunch(product, null, "dotnet")
        new ProjectEvaluator(launch)

    let request target =
        { Id = PackageRequestId.newId ()
          Target = target
          Value = () }

    let fileTarget path =
        PackageWorkspaceTarget.file path |> Result.defaultWith (failwithf "%A")

    let directoryTarget path =
        PackageWorkspaceTarget.directory path |> Result.defaultWith (failwithf "%A")

    let installed target =
        let catalog =
            NuGetPackageCatalog.createWith evaluatorFactory DotnetInstalledRestore.run

        catalog.Installed(request target) |> Async.RunSynchronously

    let requireGraphs =
        function
        | Error error ->
            failwithf "%s: %s" (PackageFailure.code error) (PackageFailure.message error)
        | Ok graphs -> graphs

    let assertImmediateGraph graphs =
        graphs
        |> List.map _.State
        |> List.distinct
        |> should equal [ InstalledPackageGraphState.UnverifiablyFreshRestoreGraph ]

        graphs |> List.collect _.Packages |> should not' (be Empty)

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
                let change = WatcherChangeTypes.Created ||| WatcherChangeTypes.Renamed
                watcher.WaitForChanged(change, 10000).TimedOut |> should equal false

    let capturedArguments path =
        File.ReadAllLines path
        |> Array.map (fun line ->
            use document = JsonDocument.Parse line

            document.RootElement.EnumerateArray()
            |> Seq.map (fun element -> element.GetString())
            |> Seq.map (Option.ofObj >> Option.defaultValue "")
            |> Seq.toList)
        |> Array.toList

[<CollectionDefinition("Package installed scenarios", DisableParallelization = true)>]
type PackageInstalledCollection() = class end

[<Collection("Package installed scenarios")>]
type InstalledPackagePortTests() =
    [<Fact>]
    member _.``public installed port evaluates a direct project and finds native assets when MSBuild returns obj backslash``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "direct"

        try
            workspace.Project
            |> InstalledPortScenario.fileTarget
            |> InstalledPortScenario.installed
            |> InstalledPortScenario.requireGraphs
            |> InstalledPortScenario.assertImmediateGraph
        finally
            InstalledPortScenario.delete workspace

    [<Fact>]
    member _.``public installed port evaluates every supported project under a directory target``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "directory"

        try
            workspace.Directory
            |> InstalledPortScenario.directoryTarget
            |> InstalledPortScenario.installed
            |> InstalledPortScenario.requireGraphs
            |> InstalledPortScenario.assertImmediateGraph
        finally
            InstalledPortScenario.delete workspace

    [<Fact>]
    member _.``public installed port resolves the supported project set from a solution XML target``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "solution-xml"

        try
            workspace.Solution
            |> InstalledPortScenario.fileTarget
            |> InstalledPortScenario.installed
            |> InstalledPortScenario.requireGraphs
            |> InstalledPortScenario.assertImmediateGraph
        finally
            InstalledPortScenario.delete workspace

    [<Fact>]
    member _.``successful refresh uses one closed stock restore vector in workspace context and returns a verified mapped graph``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "refresh-success"

        try
            let capture = Path.Combine(workspace.Directory, "restore-arguments.jsonl")
            let working = Path.Combine(workspace.Directory, "restore-working-directory.txt")
            let continuePath = Path.Combine(workspace.Directory, "continue")
            InstalledPortScenario.write continuePath "continue"
            let fakeHost = InstalledPortScenario.copyScriptedDotnet workspace.Directory

            use _environment =
                new InstalledPortScenario.EnvironmentScope(
                    [ "DOTNET_HOST_PATH", Some fakeHost
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", Some "workspace-command"
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CAPTURE_PATH", Some capture
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_WORKING_DIRECTORY_PATH",
                      Some working
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CONTINUE_PATH", Some continuePath ]
                )

            let catalog =
                NuGetPackageCatalog.createWith
                    InstalledPortScenario.evaluatorFactory
                    DotnetInstalledRestore.run

            let result =
                catalog.RefreshInstalled(
                    InstalledPortScenario.request (
                        InstalledPortScenario.fileTarget workspace.Project
                    )
                )
                |> Async.RunSynchronously
                |> InstalledPortScenario.requireGraphs

            result
            |> List.map _.State
            |> List.distinct
            |> should equal [ InstalledPackageGraphState.Current ]

            result |> List.collect _.Packages |> should not' (be Empty)

            InstalledPortScenario.capturedArguments capture
            |> should equal [ [ "restore"; workspace.Project; "--nologo" ] ]

            File.ReadAllText working |> should equal workspace.Directory
        finally
            InstalledPortScenario.delete workspace

    [<Fact>]
    member _.``failed refresh returns a stable external-tool failure and leaves the immediate installed read available``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "refresh-failure"

        try
            let fakeHost = InstalledPortScenario.copyScriptedDotnet workspace.Directory

            use _environment =
                new InstalledPortScenario.EnvironmentScope(
                    [ "DOTNET_HOST_PATH", Some fakeHost
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", Some "failure" ]
                )

            let catalog =
                NuGetPackageCatalog.createWith
                    InstalledPortScenario.evaluatorFactory
                    DotnetInstalledRestore.run

            let target = InstalledPortScenario.fileTarget workspace.Project

            match
                catalog.RefreshInstalled(InstalledPortScenario.request target)
                |> Async.RunSynchronously
            with
            | Ok _ -> failwith "The scripted restore failure unexpectedly succeeded."
            | Error error ->
                PackageFailure.kind error |> should equal PackageFailureKind.ExternalToolFailed

                PackageFailure.code error |> should equal "DWE-PACKAGE-EXTERNAL-TOOL-FAILED"

            catalog.Installed(InstalledPortScenario.request target)
            |> Async.RunSynchronously
            |> InstalledPortScenario.requireGraphs
            |> InstalledPortScenario.assertImmediateGraph
        finally
            InstalledPortScenario.delete workspace

    [<Fact>]
    member _.``cancelled refresh terminates the scripted restore and returns the stable cancelled failure``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "refresh-cancel"

        try
            let fakeHost = InstalledPortScenario.copyScriptedDotnet workspace.Directory
            let started = Path.Combine(workspace.Directory, "started")
            let continuePath = Path.Combine(workspace.Directory, "continue")

            use _environment =
                new InstalledPortScenario.EnvironmentScope(
                    [ "DOTNET_HOST_PATH", Some fakeHost
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", Some "workspace-command"
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH", Some started
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CONTINUE_PATH", Some continuePath ]
                )

            let catalog =
                NuGetPackageCatalog.createWith
                    InstalledPortScenario.evaluatorFactory
                    DotnetInstalledRestore.run

            let request =
                InstalledPortScenario.request (InstalledPortScenario.fileTarget workspace.Project)

            let refresh = catalog.RefreshInstalled request |> Async.StartAsTask
            InstalledPortScenario.waitForFile started

            catalog.Cancel(PackageCancellation.Request request.Id) |> Async.RunSynchronously

            match refresh.GetAwaiter().GetResult() with
            | Ok _ -> failwith "The cancelled refresh unexpectedly succeeded."
            | Error error ->
                PackageFailure.kind error |> should equal PackageFailureKind.Cancelled
                PackageFailure.code error |> should equal "DWE-PACKAGE-CANCELLED"
        finally
            InstalledPortScenario.delete workspace

    [<Fact>]
    member _.``production preview composition evaluates and fingerprints without invoking package restore``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "preview-read-only"

        try
            let mutable restoreStarts = 0

            let runRestore _ _ _ =
                async {
                    restoreStarts <- restoreStarts + 1
                    return failwith "Preview must not invoke restore."
                }

            let catalog =
                NuGetPackageCatalog.createWith InstalledPortScenario.evaluatorFactory runRestore

            let target = InstalledPortScenario.fileTarget workspace.Project

            let identity =
                PackageId.create "Preview.Package" |> Result.defaultWith (failwithf "%A")

            let version = NuGetVersion.create "1.0.0" |> Result.defaultWith (failwithf "%A")

            let project =
                PackageProjectId.create workspace.Project |> Result.defaultWith (failwithf "%A")

            let selection =
                { Id = PackageRequestId.newId ()
                  Target = target
                  Value =
                    { Operation = RequestedPackageOperation.InstallVersion(identity, version)
                      Targets = NonEmptyList.singleton (PackageTargetScope.Project project)
                      BrowseSource = None } }

            let precondition =
                catalog.PreviewPrecondition selection
                |> Async.RunSynchronously
                |> Result.defaultWith (fun error -> failwith (PackageFailure.message error))

            let request =
                { Id = selection.Id
                  Target = selection.Target
                  Value =
                    { Operation = selection.Value.Operation
                      Targets = selection.Value.Targets
                      BrowseSource = selection.Value.BrowseSource
                      Precondition = precondition } }

            let preview =
                catalog.Preview request
                |> Async.RunSynchronously
                |> Result.defaultWith (fun error -> failwith (PackageFailure.message error))

            PackagePreview.workspaceRevision preview
            |> should equal precondition.WorkspaceRevision

            restoreStarts |> should equal 0
        finally
            InstalledPortScenario.delete workspace

    [<Fact>]
    member _.``nested NuGet configurations remain project-scoped and invalidate an earlier preview precondition``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "nested-preview-config"

        try
            let nestedDirectory = Path.Combine(workspace.Directory, "nested")
            let nestedFeed = Path.Combine(nestedDirectory, "feed")
            Directory.CreateDirectory nestedFeed |> ignore

            let nestedProject = Path.Combine(nestedDirectory, "Nested.csproj")
            let nestedConfig = Path.Combine(nestedDirectory, "NuGet.Config")

            InstalledPortScenario.write
                nestedProject
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
"""

            InstalledPortScenario.write
                nestedConfig
                $"""
<configuration>
  <packageSources>
    <clear />
    <add key="nested" value="{nestedFeed}" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="nested"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"""

            InstalledPortScenario.runDotnet nestedDirectory [ "restore"; nestedProject; "--nologo" ]

            let catalog =
                NuGetPackageCatalog.createWith
                    InstalledPortScenario.evaluatorFactory
                    DotnetInstalledRestore.run

            let identity =
                PackageId.create "Preview.Package" |> Result.defaultWith (failwithf "%A")

            let version = NuGetVersion.create "1.0.0" |> Result.defaultWith (failwithf "%A")

            let projectScope path =
                PackageProjectId.create path
                |> Result.map PackageTargetScope.Project
                |> Result.defaultWith (failwithf "%A")

            let selection =
                { Id = PackageRequestId.newId ()
                  Target = InstalledPortScenario.directoryTarget workspace.Directory
                  Value =
                    { Operation = RequestedPackageOperation.InstallVersion(identity, version)
                      Targets =
                        NonEmptyList.create
                            (projectScope workspace.Project)
                            [ projectScope nestedProject ]
                      BrowseSource = None } }

            let precondition =
                catalog.PreviewPrecondition selection
                |> Async.RunSynchronously
                |> Result.defaultWith (fun error -> failwith (PackageFailure.message error))

            let fingerprintedPaths = precondition.FileFingerprints |> Map.keys |> Set.ofSeq

            fingerprintedPaths
            |> should contain (Path.Combine(workspace.Directory, "NuGet.Config"))

            fingerprintedPaths |> should contain nestedConfig

            let request =
                { Id = selection.Id
                  Target = selection.Target
                  Value =
                    { Operation = selection.Value.Operation
                      Targets = selection.Value.Targets
                      BrowseSource = selection.Value.BrowseSource
                      Precondition = precondition } }

            let preview =
                catalog.Preview request
                |> Async.RunSynchronously
                |> Result.defaultWith (fun error -> failwith (PackageFailure.message error))

            let mappings =
                PackagePreview.targets preview
                |> NonEmptyList.toList
                |> List.map (fun target ->
                    let project =
                        match PackageTargetPreview.target target with
                        | PackageTargetScope.Project project
                        | PackageTargetScope.Framework(project, _)
                        | PackageTargetScope.Runtime(project, _, _) -> project.Value

                    project, (PackageTargetPreview.impact target).SourceMapping)
                |> Map

            let mappedSources =
                function
                | PackageSourceMappingImpact.ApplyAllowed sources
                | PackageSourceMappingImpact.BrowseSourceDoesNotConstrainApply(_, sources)
                | PackageSourceMappingImpact.UnknownTransitiveConsequences(sources, _) -> sources

            mappings[workspace.Project]
            |> mappedSources
            |> should
                equal
                [ PackageSourceId.create "local" |> Result.defaultWith (failwithf "%A") ]

            mappings[nestedProject]
            |> mappedSources
            |> should
                equal
                [ PackageSourceId.create "nested" |> Result.defaultWith (failwithf "%A") ]

            InstalledPortScenario.write
                nestedConfig
                (File.ReadAllText nestedConfig + Environment.NewLine)

            match catalog.Preview request |> Async.RunSynchronously with
            | Ok _ -> failwith "The changed nested NuGet configuration unexpectedly remained valid."
            | Error failure ->
                PackageFailure.kind failure |> should equal PackageFailureKind.StaleState
        finally
            InstalledPortScenario.delete workspace

    [<Fact>]
    member _.``latest preview metadata and versions remain isolated to each project effective NuGet source``
        ()
        =
        let workspace = InstalledPortScenario.createWorkspace "project-scoped-metadata"

        try
            let rootFeed = Path.Combine(workspace.Directory, "feed")
            let nestedDirectory = Path.Combine(workspace.Directory, "nested")
            let nestedFeed = Path.Combine(nestedDirectory, "feed")
            let packageBuild = Path.Combine(workspace.Directory, "package-build")
            Directory.CreateDirectory nestedFeed |> ignore
            Directory.CreateDirectory packageBuild |> ignore

            let packageProject = Path.Combine(packageBuild, "Scoped.Package.csproj")

            InstalledPortScenario.write
                packageProject
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Scoped.Package</PackageId>
    <Authors>ALSI</Authors>
    <Description>Project-scoped package metadata fixture.</Description>
  </PropertyGroup>
</Project>
"""

            let pack version license output =
                InstalledPortScenario.runDotnet
                    packageBuild
                    [ "pack"
                      packageProject
                      "--nologo"
                      "--output"
                      output
                      $"-p:PackageVersion={version}"
                      $"-p:PackageLicenseExpression={license}" ]

            pack "1.0.0" "MIT" rootFeed

            File.Copy(
                Path.Combine(rootFeed, "Scoped.Package.1.0.0.nupkg"),
                Path.Combine(nestedFeed, "Scoped.Package.1.0.0.nupkg")
            )

            pack "2.0.0" "MIT" rootFeed
            pack "3.0.0" "Apache-2.0" nestedFeed
            Directory.Delete(packageBuild, true)

            InstalledPortScenario.write
                (Path.Combine(workspace.Directory, "Directory.Packages.props"))
                """
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Scoped.Package" Version="1.0.0" />
  </ItemGroup>
</Project>
"""

            InstalledPortScenario.write
                workspace.Project
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Scoped.Package" />
  </ItemGroup>
</Project>
"""

            let nestedProject = Path.Combine(nestedDirectory, "Nested.csproj")
            let nestedConfig = Path.Combine(nestedDirectory, "NuGet.Config")

            InstalledPortScenario.write
                nestedProject
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Scoped.Package" />
  </ItemGroup>
</Project>
"""

            InstalledPortScenario.write
                nestedConfig
                $"""
<configuration>
  <packageSources>
    <clear />
    <add key="nested" value="{nestedFeed}" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="nested"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"""

            InstalledPortScenario.runDotnet
                workspace.Directory
                [ "restore"; workspace.Project; "--nologo" ]

            InstalledPortScenario.runDotnet nestedDirectory [ "restore"; nestedProject; "--nologo" ]

            let catalog =
                NuGetPackageCatalog.createWith
                    InstalledPortScenario.evaluatorFactory
                    DotnetInstalledRestore.run

            let identity =
                PackageId.create "Scoped.Package" |> Result.defaultWith (failwithf "%A")

            let projectScope path =
                PackageProjectId.create path
                |> Result.map PackageTargetScope.Project
                |> Result.defaultWith (failwithf "%A")

            let selection =
                { Id = PackageRequestId.newId ()
                  Target = InstalledPortScenario.directoryTarget workspace.Directory
                  Value =
                    { Operation = RequestedPackageOperation.UpdateLatest identity
                      Targets =
                        NonEmptyList.create
                            (projectScope workspace.Project)
                            [ projectScope nestedProject ]
                      BrowseSource = None } }

            let precondition =
                catalog.PreviewPrecondition selection
                |> Async.RunSynchronously
                |> Result.defaultWith (fun failure -> failwith (PackageFailure.message failure))

            let request =
                { Id = selection.Id
                  Target = selection.Target
                  Value =
                    { Operation = selection.Value.Operation
                      Targets = selection.Value.Targets
                      BrowseSource = selection.Value.BrowseSource
                      Precondition = precondition } }

            let projectEvidence =
                catalog.Preview request
                |> Async.RunSynchronously
                |> Result.defaultWith (fun failure -> failwith (PackageFailure.message failure))
                |> PackagePreview.targets
                |> NonEmptyList.toList
                |> List.map (fun target ->
                    let project =
                        match PackageTargetPreview.target target with
                        | PackageTargetScope.Project project
                        | PackageTargetScope.Framework(project, _)
                        | PackageTargetScope.Runtime(project, _, _) -> project.Value

                    let version =
                        match PackageTargetPreview.change target with
                        | PackageTargetChange.Update(_, ProposedPackageState.Direct value)
                        | PackageTargetChange.Update(_,
                                                     ProposedPackageState.CentrallyManaged(value, _)) ->
                            value.Value
                        | change -> failwithf "Expected an update change, got %A." change

                    let license =
                        match (PackageTargetPreview.impact target).Metadata with
                        | PackageMetadataImpact.Known(_, _, _, value) -> value
                        | PackageMetadataImpact.Unknown -> None

                    let sources =
                        match (PackageTargetPreview.impact target).SourceMapping with
                        | PackageSourceMappingImpact.ApplyAllowed values
                        | PackageSourceMappingImpact.BrowseSourceDoesNotConstrainApply(_, values)
                        | PackageSourceMappingImpact.UnknownTransitiveConsequences(values, _) ->
                            values |> List.map _.Value

                    project, (version, license, sources))
                |> Map

            projectEvidence[workspace.Project]
            |> should equal ("2.0.0", Some "MIT", [ "local" ])

            projectEvidence[nestedProject]
            |> should equal ("3.0.0", Some "Apache-2.0", [ "nested" ])
        finally
            InstalledPortScenario.delete workspace
