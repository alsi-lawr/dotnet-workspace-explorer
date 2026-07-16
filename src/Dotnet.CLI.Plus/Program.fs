namespace Dotnet.CLI.Plus

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

        let jsonMode = arguments |> Array.tryHead = Some "--json"

        let result =
            try
                CliBroker.ExecuteAsync(arguments, cancellation.Token).GetAwaiter().GetResult()
            with _ ->
                CliBroker.InternalFailure()

        CliBroker.Render(result, jsonMode, Console.Out, Console.Error)
