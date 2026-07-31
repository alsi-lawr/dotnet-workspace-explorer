namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open FsUnit.Xunit
open Xunit

module internal DirectCommandProcess =
    type Result =
        { ExitCode: int
          StandardOutput: string
          StandardError: string }

    let rec repositoryRoot directory =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            directory
        else
            repositoryRoot (Directory.GetParent(directory).FullName)

    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let root = repositoryRoot AppContext.BaseDirectory

    let temporaryDirectory name =
        let path =
            Path.Combine(
                root,
                ".agent-workspace",
                "mtp",
                $"dotnet-workspace-explorer-{name}-{Guid.NewGuid():N}"
            )

        Directory.CreateDirectory path |> ignore
        path

    let executable project =
        let name =
            if OperatingSystem.IsWindows() then
                $"{project}.exe"
            else
                project

        let projectDirectory =
            if project = "Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet" then
                Path.Combine(root, "tests", "integration", "Support", "ScriptedDotnet")
            else
                Path.Combine(root, "tests", "integration", "Workspaces")

        Path.Combine(projectDirectory, "bin", configuration, "net10.0", name)

    let product =
        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.WorkspaceExplorer.exe"
            else
                "Dotnet.WorkspaceExplorer"

        Path.Combine(root, "src", "WorkspaceExplorer", "bin", configuration, "net10.0", name)

    let copyScriptedDotnet directory =
        let sourceDirectory =
            Path.GetDirectoryName(executable "Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet")

        let destination = Path.Combine(directory, "scripted-dotnet")
        Directory.CreateDirectory destination |> ignore

        for source in Directory.EnumerateFiles sourceDirectory do
            let target = Path.Combine(destination, Path.GetFileName source)
            File.Copy(source, target, true)

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(target, File.GetUnixFileMode source)

        Path.Combine(
            destination,
            Path.GetFileName(executable "Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet")
        )

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
        let info = ProcessStartInfo product
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.WorkingDirectory <- directory
        info.Environment["DOTNET_HOST_PATH"] <- copyScriptedDotnet directory
        info.Environment["DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE"] <- mode

        for name, value in environment do
            info.Environment[name] <- value

        for argument in arguments do
            info.ArgumentList.Add argument

        Process.Start info

    let run directory mode arguments environment =
        use child = start directory mode arguments environment
        (child) |> should not' (be Null)
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        (child.WaitForExit 10000) |> should equal true

        { ExitCode = child.ExitCode
          StandardOutput = output.Result
          StandardError = error.Result }

    let json result =
        JsonDocument.Parse result.StandardOutput

    let success result =
        use document = json result
        document.RootElement.GetProperty("success").GetBoolean()

    let diagnosticCode result =
        use document = json result
        let diagnostics = document.RootElement.GetProperty "diagnostics"
        diagnostics[0].GetProperty("code").GetString()

    let childArguments result =
        use document = json result

        document.RootElement.GetProperty("result").GetProperty("childArguments").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toArray

    let waitForFile path =
        if not (File.Exists path) then
            use watcher =
                new FileSystemWatcher(Path.GetDirectoryName path, Path.GetFileName path)

            watcher.EnableRaisingEvents <- true

            if not (File.Exists path) then
                let changes = WatcherChangeTypes.Created ||| WatcherChangeTypes.Renamed
                (watcher.WaitForChanged(changes, 10000).TimedOut) |> should equal false

    let delete path =
        if Directory.Exists path then
            Directory.Delete(path, true)
