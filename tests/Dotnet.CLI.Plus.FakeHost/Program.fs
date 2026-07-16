namespace Dotnet.CLI.Plus.FakeHost

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Reflection
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
            Thread.Sleep Timeout.Infinite
            0
        | _, Some "capture" ->
            Console.Out.Write(JsonSerializer.Serialize arguments)
            0
        | _, Some "failure" ->
            Console.Error.Write("failure")
            23
        | _, Some "marker" ->
            match setting "DOTNET_PLUS_FAKE_HOST_MARKER" with
            | Some path -> File.WriteAllText(path, "started")
            | None -> ()

            0
        | _, Some "tree" ->
            let startInfo = ProcessStartInfo()

            startInfo.FileName <-
                Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
                |> Option.ofObj
                |> Option.defaultValue "dotnet"

            startInfo.UseShellExecute <- false
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location)
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
