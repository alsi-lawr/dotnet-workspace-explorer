namespace Dotnet.WorkspaceExplorer.Rpc.Benchmarks

open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open Dotnet.WorkspaceExplorer.Rpc

[<MemoryDiagnoser>]
type WorkspaceRpcSerializationBenchmarks() =
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

        encoded <- MessagePackRpcCodec.encodeFrame frame

        match MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits encoded with
        | Ok(RpcFrameDecodeResult.Frame(Response(2u, None, _))) -> ()
        | value -> invalidOp $"The benchmark payload did not round-trip: {value}"

    [<Benchmark>]
    member _.EncodeWorkspaceRoot() = MessagePackRpcCodec.encodeFrame frame

    [<Benchmark>]
    member _.DecodeWorkspaceRoot() =
        MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits encoded
