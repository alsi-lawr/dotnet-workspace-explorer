namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type RecoverableRequestTests() =
    [<Fact>]
    member _.``should preserve recoverable IDs and session continuation for malformed values``() =
        let invalidValues =
            [ "invalid UTF-8", [| 0xa1uy; 0xffuy |]
              "extension", [| 0xd4uy; 0uy; 0uy |]
              "non-string map key", [| 0x81uy; 1uy; 0xc0uy |]
              "duplicate map key", [| 0x82uy; 0xa1uy; byte 'x'; 1uy; 0xa1uy; byte 'x'; 2uy |]
              "excessive depth", Array.append (Array.replicate 65 0x91uy) [| 0xc0uy |] ]

        for name, bytes in invalidValues do
            match MessagePackRpcCodec.tryDecodeValue MessagePackRpcCodec.secureLimits bytes with
            | Error(RpcFrameDecodeError.Invalid _) -> ()
            | result -> failwithf "%s: expected invalid value, got %A" name result

        let invalidFrames =
            [ "empty frame", [| 0x90uy |]
              "short request", [| 0x91uy; 0uy |]
              "incomplete error map",
              MessagePackRpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 1UL
                        RpcValue.Unsigned 1UL
                        Test.map [ "code", RpcValue.String "bad" ]
                        RpcValue.Nil ]
              ) ]

        for name, bytes in invalidFrames do
            match MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits bytes with
            | Error(RpcFrameDecodeError.Invalid _) -> ()
            | result -> failwithf "%s: expected invalid frame, got %A" name result

        match
            MessagePackRpcCodec.tryReadValueLength
                { MessagePackRpcCodec.secureLimits with
                    MaximumValueBytes = 4 }
                [| 0xc4uy; 4uy; 0uy; 0uy; 0uy; 0uy |]
        with
        | Error(RpcFrameDecodeError.TooLarge _) -> ()
        | result -> failwithf "oversized value: expected too large, got %A" result

        let recoverable =
            [ "wrong arity",
              MessagePackRpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 0UL
                        RpcValue.Unsigned 40UL
                        RpcValue.String "workspace/root" ]
              ),
              40u,
              "invalid_request"
              "non-string method",
              MessagePackRpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 0UL
                        RpcValue.Unsigned 41UL
                        RpcValue.Integer 42L
                        Test.empty ]
              ),
              41u,
              "invalid_request"
              "non-map params",
              MessagePackRpcCodec.encodeValue (
                  RpcValue.array
                      [ RpcValue.Unsigned 0UL
                        RpcValue.Unsigned 42UL
                        RpcValue.String "workspace/root"
                        RpcValue.Nil ]
              ),
              42u,
              "invalid_params"
              "invalid UTF-8 method",
              [| 0x94uy; 0uy; 43uy; 0xa1uy; 0xffuy; 0x80uy |],
              43u,
              "invalid_request"
              "invalid params map key",
              [| 0x94uy; 0uy; 44uy; 0xa1uy; byte 'x'; 0x81uy; 1uy; 0xc0uy |],
              44u,
              "invalid_params" ]

        for name, bytes, expectedId, expectedCode in recoverable do
            match MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits bytes with
            | Ok(RpcFrameDecodeResult.RecoverableError(id, error)) ->
                Assert.Equal(expectedId, id)
                Assert.Equal(expectedCode, error.Code)
            | result -> failwithf "%s: expected recoverable error, got %A" name result

        let input =
            [ yield Test.request 1u "initialize" Test.empty
              yield! recoverable |> List.map (fun (_, bytes, _, _) -> bytes)
              yield Test.request 2u "shutdown" Test.empty ]
            |> Array.concat

        let exitCode, stdout, stderr =
            Test.run (Test.defaultConfiguration WorkspaceRpcProfile.current) input

        Assert.Equal(0, exitCode)
        stderr |> should equal String.Empty

        Assert.Equal<(uint32 * string) list>(
            recoverable |> List.map (fun (_, _, id, code) -> id, code),
            Test.responseErrors stdout
        )

        match Test.frames stdout |> List.last with
        | Response(2u, None, _) -> ()
        | frame -> failwithf "Session did not continue to shutdown: %A" frame
