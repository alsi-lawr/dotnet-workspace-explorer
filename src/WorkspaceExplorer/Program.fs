namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.CommandLine

open System
open System.Text
open System.Threading

module Program =
    [<EntryPoint>]
    let main arguments =
        Console.OutputEncoding <- Encoding.UTF8
        use cancellation = new CancellationTokenSource()

        Console.CancelKeyPress.Add(fun event ->
            event.Cancel <- true
            cancellation.Cancel())

        match arguments with
        | [| "internal"; "project-evaluation-host"; "--sdk"; sdkPath |] ->
            ProjectEvaluationHost.RunAsync(sdkPath, cancellation.Token).GetAwaiter().GetResult()
        | _ ->
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
                    try
                        DirectCommandRunner
                            .ExecuteAsync(
                                arguments,
                                (if jsonMode then
                                     Json
                                 else
                                     Human(
                                         Console.Out,
                                         Console.Error,
                                         not Console.IsOutputRedirected,
                                         not Console.IsErrorRedirected
                                     )),
                                cancellation.Token
                            )
                            .GetAwaiter()
                            .GetResult()
                    with _ ->
                        DirectCommandRunner.InternalFailure()

                DirectCommandRendering.render result jsonMode Console.Out Console.Error
