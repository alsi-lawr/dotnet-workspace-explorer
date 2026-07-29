namespace Dotnet.CLI.Plus.Benchmarks

open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open Dotnet.CLI.Plus.Transport

[<MemoryDiagnoser>]
type ProtocolWorkspaceBenchmarks() =
    let mutable frame = Unchecked.defaultof<RpcFrame>
    let mutable encoded = Array.empty<byte>

    [<Params(10, 100)>]
    member val NodeCount = 0 with get, set

    [<GlobalSetup>]
    member this.CreateWorkspacePayload() =
        let nodes =
            [ for index in 1 .. this.NodeCount do
                  RpcValue.map
                      [ "id", RpcValue.String $"project-{index:D4}"
                        "kind", RpcValue.String "project"
                        "name", RpcValue.String $"Project{index:D4}"
                        "path", RpcValue.String $"src/Project{index:D4}/Project{index:D4}.csproj"
                        "loadState", RpcValue.String "declared"
                        "capabilities",
                        RpcValue.array
                            [ RpcValue.String "project.items"
                              RpcValue.String "project.properties" ] ] ]

        frame <-
            Response(
                2u,
                None,
                RpcValue.map [ "revision", RpcValue.Integer 1L; "nodes", RpcValue.array nodes ]
            )

        encoded <- RpcCodec.encodeFrame frame

        match RpcCodec.decodeFrame RpcCodec.secureLimits encoded with
        | Ok(RpcFrameDecodeResult.Frame(Response(2u, None, _))) -> ()
        | value -> invalidOp $"The benchmark payload did not round-trip: {value}"

    [<Benchmark>]
    member _.EncodeWorkspaceRoot() = RpcCodec.encodeFrame frame

    [<Benchmark>]
    member _.DecodeWorkspaceRoot() =
        RpcCodec.decodeFrame RpcCodec.secureLimits encoded

module Program =
    [<EntryPoint>]
    let main arguments =
        BenchmarkSwitcher.FromAssembly(typeof<ProtocolWorkspaceBenchmarks>.Assembly).Run arguments
        |> ignore

        0
