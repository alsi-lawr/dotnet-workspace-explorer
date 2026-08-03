namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.CommandLine

open System
open System.Text
open System.Threading

module Program =
    let private runExistingRoute arguments (cancellation: CancellationTokenSource) =
        match WorkspaceRpcInvocation.parse arguments with
        | WorkspaceRpcInvocation.ValidPipeStartup(target, exportCapacity) ->
            WorkspaceRpcServer.runAsync
                target
                exportCapacity
                (Console.OpenStandardInput())
                (Console.OpenStandardOutput())
                Console.Error
                cancellation.Token
            |> _.GetAwaiter()
            |> _.GetResult()
        | WorkspaceRpcInvocation.InvalidPipeStartup ->
            Console.Error.WriteLine
                "dotnet-workspace-explorer workspace RPC startup failure: invalid invocation."

            64
        | WorkspaceRpcInvocation.NotPipeRelated ->
            let jsonMode = arguments |> Array.tryHead = Some "--json"

            let result =
                DirectCommandRunner
                    .ExecuteAsync(arguments, cancellation.Token)
                    .GetAwaiter()
                    .GetResult()

            DirectCommandRendering.render result jsonMode Console.Out Console.Error

    let private unavailablePackageHost name =
        Console.Error.WriteLine
            $"DWE-PACKAGES-HOST-UNAVAILABLE: The package {name} host is not installed yet."

        69

    [<EntryPoint>]
    let main arguments =
        Console.OutputEncoding <- Encoding.UTF8
        use cancellation = new CancellationTokenSource()

        Console.CancelKeyPress.Add(fun event ->
            event.Cancel <- true
            cancellation.Cancel())

        match ProgramInvocation.parse Environment.CurrentDirectory arguments with
        | ProgramInvocation.ProjectEvaluationHost sdkPath ->
            ProjectEvaluationHost.RunAsync(sdkPath, cancellation.Token).GetAwaiter().GetResult()
        | ProgramInvocation.PackageTerminal _ -> unavailablePackageHost "terminal"
        | ProgramInvocation.PackagePipe _ -> unavailablePackageHost "RPC"
        | ProgramInvocation.InvalidPackageStartup failure ->
            Console.Error.WriteLine $"{failure.Code}: {failure.Message}"
            64
        | ProgramInvocation.ExistingRoute existingArguments ->
            runExistingRoute existingArguments cancellation
