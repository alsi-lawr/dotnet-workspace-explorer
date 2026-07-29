namespace Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading

module Program =
    [<EntryPoint>]
    let main arguments =
        match
            arguments |> Array.toList,
            InvocationSettings.setting "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE"
        with
        | [ "--child" ], _ ->
            use blocked = new ManualResetEventSlim false
            blocked.Wait()
            0
        | _, Some "capture" ->
            Console.Out.Write(JsonSerializer.Serialize arguments)
            0
        | _, Some "stream" ->
            Console.Out.Write "\u001b[31mfirst\u001b[0m"
            Console.Out.Flush()
            InvocationSettings.signalAndWait ()

            Console.Out.Write "second"
            0
        | _, Some "failure" ->
            Console.Error.Write "\u001b[31mfailure\u001b[0m"
            23
        | _, Some "marker" ->
            match
                InvocationSettings.setting "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH"
            with
            | Some path -> File.WriteAllText(path, "started")
            | None -> ()

            0
        | _, Some "create-output" ->
            let output =
                arguments
                |> Array.pairwise
                |> Array.tryPick (function
                    | "--output", value
                    | "-o", value -> Some value
                    | _ -> None)
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            Directory.CreateDirectory output |> ignore
            File.WriteAllText(Path.Combine(output, "created-by-fake.txt"), "created")
            0
        | _, Some "workspace-command" -> ScriptedDotnetCommand.run arguments
        | _, Some "tree" ->
            let startInfo = ProcessStartInfo()

            startInfo.FileName <-
                Environment.ProcessPath
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    invalidOp "The scripted dotnet process path is unavailable.")

            startInfo.UseShellExecute <- false
            startInfo.ArgumentList.Add "--child"
            use child = Process.Start startInfo

            if isNull child then
                invalidOp "The fake child host could not be started."

            match
                InvocationSettings.setting
                    "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CHILD_PID_PATH"
            with
            | Some path ->
                let temporary =
                    Path.Combine(
                        Path.GetDirectoryName path,
                        $".{Path.GetFileName path}.{Guid.NewGuid():N}"
                    )

                File.WriteAllText(temporary, string child.Id)
                File.Move(temporary, path)
            | None -> ()

            child.WaitForExit()
            0
        | _ -> 0
