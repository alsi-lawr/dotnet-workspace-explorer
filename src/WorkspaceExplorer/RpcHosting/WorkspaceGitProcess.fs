namespace Dotnet.WorkspaceExplorer

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc

[<RequireQualifiedAccess>]
module internal WorkspaceGitProcess =
    let readBoundedAsync (reader: TextReader) maximum (cancellationToken: CancellationToken) =
        task {
            let buffer = Array.zeroCreate<char> 4096
            let builder = StringBuilder()
            let mutable complete = false
            let mutable exceeded = false

            while not complete do
                let! read = reader.ReadAsync(buffer, cancellationToken)

                if read = 0 then
                    complete <- true
                elif exceeded || builder.Length + read > maximum then
                    exceeded <- true
                else
                    builder.Append(buffer, 0, read) |> ignore

            return if exceeded then Error() else Ok(builder.ToString())
        }

    let runAsync
        executable
        workingDirectory
        arguments
        maximum
        (cancellationToken: CancellationToken)
        =
        task {
            try
                let start = ProcessStartInfo executable
                start.WorkingDirectory <- workingDirectory
                start.UseShellExecute <- false
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                start.StandardOutputEncoding <- Encoding.UTF8
                start.StandardErrorEncoding <- Encoding.UTF8

                for argument in arguments do
                    start.ArgumentList.Add argument

                use child = new Process(StartInfo = start)

                if not (child.Start()) then
                    return
                        Error(RpcErrors.create "git_launch_failed" "Git could not be started." None)
                else
                    let output = readBoundedAsync child.StandardOutput maximum cancellationToken

                    let error = readBoundedAsync child.StandardError (64 * 1024) cancellationToken

                    do! child.WaitForExitAsync cancellationToken
                    let! output = output
                    let! error = error

                    match output, error with
                    | Error(), _
                    | _, Error() ->
                        return
                            Error(
                                RpcErrors.create
                                    "git_output_too_large"
                                    "Git status output exceeded the supported bound."
                                    None
                            )
                    | Ok output, Ok error -> return Ok(child.ExitCode, output, error)
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException())
            | :? System.ComponentModel.Win32Exception
            | :? ArgumentException
            | :? UnauthorizedAccessException ->
                return Error(RpcErrors.create "git_launch_failed" "Git could not be started." None)
            | :? IOException ->
                return
                    Error(
                        RpcErrors.create
                            "git_status_failed"
                            "Git status output could not be read."
                            None
                    )
        }
