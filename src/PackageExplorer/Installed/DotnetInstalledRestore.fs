namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.ComponentModel
open System.Diagnostics
open System.Threading
open Dotnet.WorkspaceExplorer.Packages

type internal RunInstalledRestore =
    string -> string -> CancellationToken -> Async<Result<unit, PackageFailure>>

[<RequireQualifiedAccess>]
module internal DotnetInstalledRestore =
    let private failure kind message retry =
        PackageFailure.create kind message retry |> Result.defaultWith (failwithf "%A")

    let run
        (workingDirectory: string)
        (projectPath: string)
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

                [ "restore"; projectPath; "--nologo" ] |> List.iter startInfo.ArgumentList.Add

                use child = new Process(StartInfo = startInfo)

                if not (child.Start()) then
                    return
                        Error(
                            failure
                                PackageFailureKind.Internal
                                "The dotnet restore process did not start."
                                PackageFailureRetry.Transient
                        )
                else
                    let output = child.StandardOutput.ReadToEndAsync()
                    let error = child.StandardError.ReadToEndAsync()

                    let! cancelled =
                        async {
                            try
                                do! child.WaitForExitAsync cancellationToken |> Async.AwaitTask
                                return false
                            with :? OperationCanceledException ->
                                return true
                        }

                    if cancelled then
                        if not child.HasExited then
                            try
                                child.Kill true
                            with
                            | :? InvalidOperationException
                            | :? Win32Exception -> ()

                        do! child.WaitForExitAsync CancellationToken.None |> Async.AwaitTask
                        let! _ = output |> Async.AwaitTask
                        let! _ = error |> Async.AwaitTask

                        return
                            Error(
                                failure
                                    PackageFailureKind.Cancelled
                                    "The installed package refresh was cancelled."
                                    PackageFailureRetry.Never
                            )
                    else
                        let! _ = output |> Async.AwaitTask
                        let! _ = error |> Async.AwaitTask

                        if child.ExitCode = 0 then
                            return Ok()
                        else
                            return
                                Error(
                                    failure
                                        PackageFailureKind.ExternalToolFailed
                                        "The dotnet restore command failed."
                                        PackageFailureRetry.Transient
                                )
            with
            | :? OperationCanceledException ->
                return
                    Error(
                        failure
                            PackageFailureKind.Cancelled
                            "The installed package refresh was cancelled."
                            PackageFailureRetry.Never
                    )
            | :? Win32Exception ->
                return
                    Error(
                        failure
                            PackageFailureKind.ExternalToolFailed
                            "The dotnet restore host could not be started."
                            PackageFailureRetry.Transient
                    )
        }
