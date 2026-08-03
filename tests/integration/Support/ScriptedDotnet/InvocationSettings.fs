namespace Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet

#nowarn "3261"

open System
open System.IO
open System.Text.Json

module internal InvocationSettings =
    let setting name =
        Environment.GetEnvironmentVariable name |> Option.ofObj

    let argumentValue name (arguments: string array) =
        arguments
        |> Array.pairwise
        |> Array.tryPick (function
            | option, value when option = name -> Some value
            | _ -> None)

    let isEnabled name =
        match setting name with
        | Some value ->
            value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        | None -> false

    let recordInvocation arguments =
        match setting "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_WORKING_DIRECTORY_PATH" with
        | Some path -> File.WriteAllText(path, Directory.GetCurrentDirectory())
        | None -> ()

        match setting "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CAPTURE_PATH" with
        | Some path ->
            let directory = Path.GetDirectoryName path

            if not (String.IsNullOrEmpty directory) then
                Directory.CreateDirectory directory |> ignore

            let line = JsonSerializer.Serialize arguments + Environment.NewLine
            File.AppendAllText(path, line)
        | None -> ()

    let signalAndWait () =
        match setting "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH" with
        | Some path ->
            let temporary =
                Path.Combine(
                    Path.GetDirectoryName path,
                    $".{Path.GetFileName path}.{Guid.NewGuid():N}"
                )

            File.WriteAllText(temporary, string Environment.ProcessId)
            File.Move(temporary, path)
        | None -> ()

        match setting "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CONTINUE_PATH" with
        | Some path when not (File.Exists path) ->
            use watcher =
                new FileSystemWatcher(Path.GetDirectoryName path, Path.GetFileName path)

            watcher.EnableRaisingEvents <- true

            if not (File.Exists path) then
                watcher.WaitForChanged(WatcherChangeTypes.Created ||| WatcherChangeTypes.Renamed)
                |> ignore
        | _ -> ()
