namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"
#nowarn "3511"

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading

module internal DotnetProcess =
    let private ansi =
        Regex("\u001b(?:[@-_][0-?]*[ -/]*[@-~]|\\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)

    let sanitize value =
        ansi.Replace(value, String.Empty)
        |> Seq.filter (fun character ->
            character = '\t'
            || character = '\n'
            || character = '\r'
            || character >= ' ' && character <> '\u007f')
        |> String.Concat

    let private pump (reader: StreamReader) (writer: TextWriter) tty =
        task {
            let builder = StringBuilder()
            let buffer = Array.zeroCreate<char> 1024
            let sanitizer = if tty then None else Some(TerminalOutputSanitizer())

            let rec copy () =
                task {
                    let! read = reader.ReadAsync(buffer, 0, buffer.Length)

                    if read > 0 then
                        let chunk = String(buffer, 0, read)
                        builder.Append chunk |> ignore

                        writer.Write(
                            match sanitizer with
                            | Some value -> value.Push chunk
                            | None -> chunk
                        )

                        writer.Flush()
                        return! copy ()
                }

            do! copy ()

            sanitizer
            |> Option.iter (fun value ->
                writer.Write(value.Complete())
                writer.Flush())

            return builder.ToString()
        }

    let run
        (host: DotnetHost)
        (childArguments: string list)
        mode
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            let info =
                ProcessStartInfo(
                    FileName = host.FileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            host.Prefix |> List.iter info.ArgumentList.Add
            childArguments |> List.iter info.ArgumentList.Add
            use childProcess = new Process(StartInfo = info)

            try
                if not (childProcess.Start()) then
                    return
                        Error(
                            DirectCommandFailures.internalFailure "The dotnet host did not start."
                        )
                else
                    let outputTask, errorTask =
                        match mode with
                        | Json ->
                            childProcess.StandardOutput.ReadToEndAsync(),
                            childProcess.StandardError.ReadToEndAsync()
                        | Human(output, error, outputIsTty, errorIsTty) ->
                            pump childProcess.StandardOutput output outputIsTty,
                            pump childProcess.StandardError error errorIsTty

                    let! wasCancelled =
                        task {
                            try
                                do! childProcess.WaitForExitAsync cancellationToken
                                return false
                            with :? OperationCanceledException ->
                                return true
                        }

                    if wasCancelled then
                        let mutable treeTerminationIncomplete = false

                        try
                            if not childProcess.HasExited then
                                childProcess.Kill true
                        with
                        | :? InvalidOperationException
                        | :? ComponentModel.Win32Exception ->
                            treeTerminationIncomplete <- true

                            if not childProcess.HasExited then
                                try
                                    childProcess.Kill()
                                with
                                | :? InvalidOperationException
                                | :? ComponentModel.Win32Exception -> ()

                        do! childProcess.WaitForExitAsync CancellationToken.None
                        let! _ = outputTask
                        let! _ = errorTask

                        return
                            Error(
                                if treeTerminationIncomplete then
                                    DirectCommandFailures.terminationIncomplete ()
                                else
                                    DirectCommandFailures.cancelled ()
                            )
                    else
                        let! output = outputTask
                        let! error = errorTask
                        return Ok(childProcess.ExitCode, output, error)
            with
            | :? OperationCanceledException -> return Error(DirectCommandFailures.cancelled ())
            | :? ComponentModel.Win32Exception ->
                return
                    Error(
                        DirectCommandFailures.internalFailure
                            "The dotnet host could not be started."
                    )
        }
