namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc
open Xunit

[<Collection("RPC scenarios")>]
type WorkspaceRpcStreamTests() =
    [<Fact>]
    member _.``should keep fragmented and coalesced streams frame bounded``() =
        let limits =
            { MessagePackRpcCodec.secureLimits with
                MaximumValueBytes = 4096 }

        let profile = Test.profile "streams" [ "large", Read; "shutdown", Control ]
        let fragmented = Test.golden "fragmented-stream.mpack"
        let coalesced = Test.golden "coalesced-stream.mpack"

        let initialize =
            MessagePackRpcCodec.encodeFrame (
                Request(
                    10u,
                    "initialize",
                    Test.map
                        [ "protocolVersion",
                          Test.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                          "clientInfo", Test.map [ "name", RpcValue.String "fixture" ]
                          "capabilities", RpcValue.array [ RpcValue.String "workspace.root" ]
                          "limits",
                          Test.map
                              [ "maxFrameBytes", RpcValue.Integer 4096L
                                "maxPageSize", RpcValue.Integer 1L ] ]
                )
            )

        let shutdown =
            MessagePackRpcCodec.encodeFrame (Request(15u, "shutdown", Test.empty))

        let large =
            Test.request 2u "large" (Test.map [ "payload", RpcValue.Binary(Array.zeroCreate 3900) ])

        Assert.Equal<byte>(Array.concat [ initialize; shutdown ], fragmented)
        Assert.Equal<byte>(Array.concat [ initialize; large; shutdown ], coalesced)
        Assert.InRange(coalesced.Length, 3072, limits.MaximumValueBytes)
        Assert.Equal(2, (Test.frames fragmented).Length)
        Assert.Equal(3, (Test.frames coalesced).Length)

        let configuration =
            { Test.defaultConfiguration profile with
                Limits = limits
                GetOutboundFrameLimit = fun () -> limits.MaximumValueBytes }

        let cases: (string * (unit -> Stream) * int) list =
            [ "one-byte fragments", (fun () -> new ChunkedReadStream(fragmented, 1)), 2
              "coalesced near-limit frames", (fun () -> new MemoryStream(coalesced)), 3 ]

        for name, createStream, expectedFrames in cases do
            use stream = createStream ()

            let exitCode, stdout, stderr =
                Test.runStream configuration stream CancellationToken.None |> _.Result

            Assert.True((exitCode = 0), $"{name}: exit {exitCode}, {stderr}")
            Assert.Equal(expectedFrames, (Test.frames stdout).Length)

    [<Fact>]
    member _.``should distinguish clean and truncated EOF outcomes``() =
        let configuration = Test.defaultConfiguration WorkspaceRpcProfile.current

        let cases =
            [ "clean", Array.empty, 0, None
              "truncated", [| 0x94uy; 0uy; 1uy |], 65, Some "incomplete" ]

        for name, input, expectedExit, diagnostic in cases do
            let exitCode, stdout, stderr = Test.run configuration input
            Assert.True((exitCode = expectedExit), $"{name}: exit {exitCode}")
            Assert.Empty stdout

            match diagnostic with
            | Some text -> Assert.Contains(text, stderr)
            | None -> Assert.Equal(String.Empty, stderr)
