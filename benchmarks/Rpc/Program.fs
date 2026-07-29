namespace Dotnet.WorkspaceExplorer.Rpc.Benchmarks

open BenchmarkDotNet.Running

module Program =
    [<EntryPoint>]
    let main arguments =
        BenchmarkSwitcher.FromAssembly(typeof<WorkspaceRpcSerializationBenchmarks>.Assembly).Run
            arguments
        |> ignore

        0
