namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
type internal DotnetPackageCommandFailure =
    | AuthenticationRequired
    | Unauthorized
    | Cancelled
    | TerminationUncertain
    | Failed
    | HostUnavailable

type internal RunDotnetPackageCommand =
    string -> string array -> CancellationToken -> Async<Result<unit, DotnetPackageCommandFailure>>

[<RequireQualifiedAccess>]
module internal DotnetPackageOperations =
    let private maximumDiagnosticCharacters = 65536

    let private pump (reader: StreamReader) =
        task {
            let buffer = Array.zeroCreate<char> 1024
            let text = StringBuilder()
            let mutable reading = true

            while reading do
                let! read = reader.ReadAsync(buffer, 0, buffer.Length)

                if read = 0 then
                    reading <- false
                elif text.Length < maximumDiagnosticCharacters then
                    let remaining = maximumDiagnosticCharacters - text.Length
                    text.Append(buffer, 0, min remaining read) |> ignore

            return text.ToString()
        }

    let private classifyFailure output error =
        let diagnostic = $"{output}\n{error}".ToLowerInvariant()

        if
            diagnostic.Contains("status 401", StringComparison.Ordinal)
            || diagnostic.Contains(
                "response status code does not indicate success: 401",
                StringComparison.Ordinal
            )
            || diagnostic.Contains("authentication required", StringComparison.Ordinal)
            || diagnostic.Contains("credential provider", StringComparison.Ordinal)
        then
            DotnetPackageCommandFailure.AuthenticationRequired
        elif
            diagnostic.Contains("status 403", StringComparison.Ordinal)
            || diagnostic.Contains(
                "response status code does not indicate success: 403",
                StringComparison.Ordinal
            )
            || diagnostic.Contains("unauthorized", StringComparison.Ordinal)
            || diagnostic.Contains("forbidden", StringComparison.Ordinal)
        then
            DotnetPackageCommandFailure.Unauthorized
        else
            DotnetPackageCommandFailure.Failed

    let run
        (workingDirectory: string)
        (arguments: string array)
        (cancellationToken: CancellationToken)
        =
        async {
            try
                cancellationToken.ThrowIfCancellationRequested()

                let host =
                    Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
                    |> Option.ofObj
                    |> Option.defaultValue "dotnet"

                let startInfo =
                    ProcessStartInfo(
                        FileName = host,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    )

                arguments |> Array.iter startInfo.ArgumentList.Add
                use child = new Process(StartInfo = startInfo)

                if not (child.Start()) then
                    return Error DotnetPackageCommandFailure.HostUnavailable
                else
                    let output = pump child.StandardOutput
                    let error = pump child.StandardError

                    let! cancelled =
                        async {
                            try
                                do! child.WaitForExitAsync cancellationToken |> Async.AwaitTask
                                return false
                            with :? OperationCanceledException ->
                                return true
                        }

                    if cancelled then
                        let mutable terminationUncertain = false

                        try
                            if not child.HasExited then
                                child.Kill true
                        with
                        | :? InvalidOperationException
                        | :? Win32Exception ->
                            terminationUncertain <- true

                            if not child.HasExited then
                                try
                                    child.Kill()
                                with
                                | :? InvalidOperationException
                                | :? Win32Exception -> ()

                        do! child.WaitForExitAsync CancellationToken.None |> Async.AwaitTask
                        let! _ = output |> Async.AwaitTask
                        let! _ = error |> Async.AwaitTask

                        return
                            if terminationUncertain then
                                Error DotnetPackageCommandFailure.TerminationUncertain
                            else
                                Error DotnetPackageCommandFailure.Cancelled
                    else
                        let! capturedOutput = output |> Async.AwaitTask
                        let! capturedError = error |> Async.AwaitTask

                        return
                            if child.ExitCode = 0 then
                                Ok()
                            else
                                Error(classifyFailure capturedOutput capturedError)
            with
            | :? OperationCanceledException -> return Error DotnetPackageCommandFailure.Cancelled
            | :? Win32Exception -> return Error DotnetPackageCommandFailure.HostUnavailable
        }
