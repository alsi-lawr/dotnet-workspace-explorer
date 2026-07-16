namespace Dotnet.CLI.Plus

open System
open System.Text
open System.Threading
open Dotnet.CLI.Plus.MSBuild

module Program =
    [<EntryPoint>]
    let main arguments =
        Console.OutputEncoding <- Encoding.UTF8
        use cancellation = new CancellationTokenSource()

        Console.CancelKeyPress.Add(fun event ->
            event.Cancel <- true
            cancellation.Cancel())

        match arguments with
        | [| "internal"; "msbuild-host"; "--toolset"; toolsetPath |] ->
            MsBuildHost.RunAsync(toolsetPath, cancellation.Token).GetAwaiter().GetResult()
        | _ ->
            match Pipe.isPipeInvocation arguments with
            | Some target ->
                Pipe.runAsync
                    target
                    (Console.OpenStandardInput())
                    (Console.OpenStandardOutput())
                    Console.Error
                    cancellation.Token
                |> _.GetAwaiter()
                |> _.GetResult()
            | None ->
                let jsonMode = arguments |> Array.tryHead = Some "--json"

                let result =
                    try
                        Broker
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
                        Broker.InternalFailure()

                Broker.Render result jsonMode Console.Out Console.Error
