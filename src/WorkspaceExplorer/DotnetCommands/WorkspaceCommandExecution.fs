namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.CommandLine
open Dotnet.WorkspaceExplorer.Workspaces

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading

type private ProcessOutputSanitizer() =
    let pending = StringBuilder()

    member _.Push(value: string) =
        pending.Append value |> ignore
        let source = pending.ToString()
        let output = StringBuilder()
        let mutable index = 0
        let mutable incomplete = -1

        while index < source.Length && incomplete < 0 do
            let character = source[index]

            if character = '\u001b' then
                if index + 1 >= source.Length then
                    incomplete <- index
                elif source[index + 1] = '[' then
                    let mutable endIndex = index + 2

                    while endIndex < source.Length
                          && not (source[endIndex] >= '@' && source[endIndex] <= '~') do
                        endIndex <- endIndex + 1

                    if endIndex = source.Length then
                        incomplete <- index
                    else
                        index <- endIndex + 1
                elif source[index + 1] = ']' then
                    let mutable endIndex = index + 2
                    let mutable found = false

                    while endIndex < source.Length && not found do
                        found <-
                            source[endIndex] = '\u0007'
                            || source[endIndex] = '\u001b'
                               && endIndex + 1 < source.Length
                               && source[endIndex + 1] = '\\'

                        endIndex <- endIndex + 1

                    if not found then
                        incomplete <- index
                    else
                        index <- endIndex + (if source[endIndex - 1] = '\u001b' then 1 else 0)
                else
                    index <- index + 2
            else
                if
                    character = '\t'
                    || character = '\n'
                    || character = '\r'
                    || character >= ' ' && character <> '\u007f'
                then
                    output.Append character |> ignore

                index <- index + 1

        pending.Clear() |> ignore

        if incomplete >= 0 then
            pending.Append(source.Substring incomplete) |> ignore

        output.ToString()

    member _.Complete() =
        pending.Clear() |> ignore
        String.Empty

module internal WorkspaceCommandExecution =
    let private diagnostic code message retryable =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            retryable,
            CorrelationId.New()
        )

    let private failed code message retryable =
        Error(diagnostic code message retryable)

    let private pump (reader: StreamReader) (writer: TextWriter) =
        task {
            let buffer = Array.zeroCreate<char> 1024
            let sanitizer = ProcessOutputSanitizer()
            let mutable reading = true

            while reading do
                let! read = reader.ReadAsync(buffer, 0, buffer.Length)

                if read = 0 then
                    reading <- false
                else
                    writer.Write(sanitizer.Push(String(buffer, 0, read)))
                    writer.Flush()

            writer.Write(sanitizer.Complete())
            writer.Flush()
        }

    let private executeDotnet
        (arguments: string array)
        (output: TextWriter)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            let host =
                Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
                |> Option.ofObj
                |> Option.defaultValue "dotnet"

            let info =
                ProcessStartInfo(
                    FileName = host,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            arguments |> Array.iter info.ArgumentList.Add
            use child = new Process(StartInfo = info)

            try
                if not (child.Start()) then
                    return failed "internal_error" "The dotnet host did not start." false
                else
                    let outputTask = pump child.StandardOutput output
                    let errorTask = pump child.StandardError error

                    let! wasCancelled =
                        task {
                            try
                                do! child.WaitForExitAsync cancellationToken
                                return false
                            with :? OperationCanceledException ->
                                return true
                        }

                    if wasCancelled then
                        let mutable treeTerminationIncomplete = false

                        try
                            if not child.HasExited then
                                child.Kill true
                        with
                        | :? InvalidOperationException
                        | :? ComponentModel.Win32Exception ->
                            treeTerminationIncomplete <- true

                            if not child.HasExited then
                                try
                                    child.Kill()
                                with
                                | :? InvalidOperationException
                                | :? ComponentModel.Win32Exception -> ()

                        do! child.WaitForExitAsync CancellationToken.None
                        do! outputTask
                        do! errorTask

                        return
                            if treeTerminationIncomplete then
                                failed
                                    "partial_recovery_required"
                                    ("The dotnet command exited, but its remaining child processes "
                                     + "could not be confirmed terminated.")
                                    false
                            else
                                failed "cancelled" "The dotnet command was cancelled." true
                    else
                        do! outputTask
                        do! errorTask

                        return
                            if child.ExitCode = 0 then
                                Ok()
                            else
                                failed "external_tool_failed" "The dotnet command failed." true
            with
            | :? OperationCanceledException ->
                return failed "cancelled" "The dotnet command was cancelled." true
            | :? ComponentModel.Win32Exception ->
                return failed "internal_error" "The dotnet host could not be started." false
        }

    let execute
        (arguments: string array)
        (output: TextWriter)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            match arguments with
            | [| ("solution" | "sln"); _; "launch"; "list" |] ->
                let! result = DirectCommandRunner.ExecuteAsync(arguments, cancellationToken)

                return
                    match result with
                    | Error failure -> Error failure.Diagnostic
                    | Ok completion ->
                        completion.Output
                        |> Option.iter (fun value ->
                            let sanitizer = ProcessOutputSanitizer()
                            output.Write(sanitizer.Push value)
                            output.Write(sanitizer.Complete())
                            output.Flush())

                        Ok()
            | _ -> return! executeDotnet arguments output error cancellationToken
        }
