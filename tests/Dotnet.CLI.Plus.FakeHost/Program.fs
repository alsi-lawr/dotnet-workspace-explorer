namespace Dotnet.CLI.Plus.FakeHost

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading

type FakeHostAssemblyMarker = class end

module Program =
    let private setting name =
        Environment.GetEnvironmentVariable name |> Option.ofObj

    [<EntryPoint>]
    let main arguments =
        match arguments |> Array.toList, setting "DOTNET_PLUS_FAKE_HOST_MODE" with
        | [ "--child" ], _ ->
            use blocked = new ManualResetEventSlim(false)
            blocked.Wait()
            0
        | _, Some "capture" ->
            Console.Out.Write(JsonSerializer.Serialize arguments)
            0
        | _, Some "stream" ->
            Console.Out.Write("\u001b[31mfirst\u001b[0m")
            Console.Out.Flush()

            match setting "DOTNET_PLUS_FAKE_HOST_MARKER" with
            | Some path -> File.WriteAllText(path, "first")
            | None -> ()

            match setting "DOTNET_PLUS_FAKE_HOST_RELEASE" with
            | Some path when not (File.Exists path) ->
                use watcher =
                    new FileSystemWatcher(Path.GetDirectoryName path, Path.GetFileName path)

                watcher.EnableRaisingEvents <- true
                watcher.WaitForChanged(WatcherChangeTypes.Created) |> ignore
            | _ -> ()

            Console.Out.Write("second")
            0
        | _, Some "failure" ->
            Console.Error.Write("\u001b[31mfailure\u001b[0m")
            23
        | _, Some "marker" ->
            match setting "DOTNET_PLUS_FAKE_HOST_MARKER" with
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
        | _, Some "tree" ->
            let startInfo = ProcessStartInfo()

            startInfo.FileName <-
                Environment.ProcessPath
                |> Option.ofObj
                |> Option.defaultWith (fun () -> invalidOp "The fake host process path is unavailable.")

            startInfo.UseShellExecute <- false
            startInfo.ArgumentList.Add("--child")
            use child = Process.Start startInfo

            if isNull child then
                invalidOp "The fake child host could not be started."

            match setting "DOTNET_PLUS_FAKE_HOST_CHILD_PID" with
            | Some path -> File.WriteAllText(path, string child.Id)
            | None -> ()

            child.WaitForExit()
            0
        | _ -> 0
