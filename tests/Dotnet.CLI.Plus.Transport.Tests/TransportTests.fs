namespace Dotnet.CLI.Plus.Transport.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Transport
open Xunit

module private Test =
    let map values = RpcValue.map values
    let empty = RpcValue.emptyMap

    let request id name parameters =
        RpcCodec.encodeFrame (Request(id, name, parameters))

    let decodeAll (bytes: byte array) =
        let rec consume offset frames =
            if offset = bytes.Length then
                List.rev frames
            else
                match RpcCodec.tryDecodeValue RpcCodec.secureLimits bytes[offset..] with
                | Ok(_, used) ->
                    match RpcCodec.decodeFrame RpcCodec.secureLimits bytes[offset .. offset + used - 1] with
                    | Ok frame -> consume (offset + used) (frame :: frames)
                    | Error error -> failwithf "decode failed: %A" error
                | Error error -> failwithf "value decode failed: %A" error

        consume 0 []

    let run (profile: RpcProfile) (input: byte array) =
        task {
            use source = new MemoryStream(input)
            use output = new MemoryStream()
            use errors = new StringWriter()

            let configuration =
                { Profile = profile
                  Limits = RpcCodec.secureLimits
                  Initialize = fun _ _ -> Task.FromResult(Ok(map [ "ok", RpcValue.Boolean true ]))
                  Dispatch =
                    fun _ methodName _ _ ->
                        Task.FromResult(
                            Ok
                                { Result = map [ "method", RpcValue.String methodName ]
                                  Notifications = []
                                  StopAfterResponse = methodName = "shutdown" }
                        ) }

            let! exitCode = RpcSession.runAsync configuration source output errors CancellationToken.None
            return exitCode, output.ToArray(), errors.ToString()
        }

type TransportTests() =
    [<Fact>]
    member _.``codec round trips request response notification and nested values``() =
        let nested =
            Test.map [ "items", RpcValue.array [ RpcValue.Integer -4L; Test.map [ "nested", RpcValue.Boolean true ] ] ]

        let frames =
            [ Request(1u, "workspace/root", nested)
              Response(
                  1u,
                  Some
                      { Code = "invalid_params"
                        Message = "bad"
                        Data = Some nested },
                  RpcValue.Nil
              )
              Notification("workspace/reset", nested) ]

        let encoded =
            frames |> List.collect (RpcCodec.encodeFrame >> Array.toList) |> List.toArray

        Assert.Equal(3, (Test.decodeAll encoded).Length)

    [<Fact>]
    member _.``decoder accepts all fragmentation boundaries and coalesced values``() =
        let first = Test.request 1u "initialize" Test.empty
        let second = Test.request 2u "shutdown" Test.empty
        let joined = Array.append first second

        for boundary in 1 .. first.Length - 1 do
            let left = joined[.. boundary - 1]
            Assert.Equal(Error RpcDecodeError.Incomplete, RpcCodec.tryDecodeValue RpcCodec.secureLimits left)

        Assert.Equal(2, (Test.decodeAll joined).Length)

    [<Fact>]
    member _.``codec rejects extensions map keys invalid arity message ids depth and oversize``() =
        let assertInvalid bytes =
            match RpcCodec.decodeFrame RpcCodec.secureLimits bytes with
            | Error(RpcDecodeError.Invalid _) -> ()
            | value -> failwithf "Expected invalid frame, got %A" value

        assertInvalid [| 0xd4uy; 0uy; 0uy |]
        assertInvalid [| 0x91uy; 0uy |]
        assertInvalid [| 0x94uy; 0uy; 0xc0uy; 0xa1uy; byte 'x'; 0x80uy |]

        assertInvalid
            [| 0x94uy
               0uy
               0xcfuy
               0xffuy
               0xffuy
               0xffuy
               0xffuy
               0xffuy
               0xffuy
               0xffuy
               0xffuy
               0xa1uy
               byte 'x'
               0x80uy |]

        assertInvalid [| 0x94uy; 0uy; 1uy; 0xa1uy; byte 'x'; 0x81uy; 1uy; 0xc0uy |]
        let deep = Array.append (Array.replicate 65 0x91uy) [| 0xc0uy |]

        match RpcCodec.tryDecodeValue RpcCodec.secureLimits deep with
        | Error(RpcDecodeError.Invalid _) -> ()
        | value -> failwithf "%A" value

        match
            RpcCodec.tryDecodeValue
                { RpcCodec.secureLimits with
                    MaximumValueBytes = 4 }
                [| 0xc4uy; 4uy; 0uy; 0uy; 0uy; 0uy |]
        with
        | Error(RpcDecodeError.TooLarge _) -> ()
        | value -> failwithf "%A" value

    [<Fact>]
    member _.``session enforces initialization profiles fatal stderr and shutdown flush``() =
        let worker =
            RpcProfile.create
                "worker"
                1
                0
                [ { Name = "msbuild/evaluate"
                    Classification = Read }
                  { Name = "shutdown"
                    Classification = Control } ]

        let input =
            [ Test.request 1u "workspace/root" Test.empty
              Test.request 2u "initialize" Test.empty
              Test.request 3u "workspace/root" Test.empty
              Test.request 4u "msbuild/evaluate" Test.empty
              Test.request 5u "shutdown" Test.empty ]
            |> List.collect Array.toList
            |> List.toArray

        let exitCode, stdout, stderr = Test.run worker input |> _.Result
        Assert.Equal(0, exitCode)
        Assert.Equal(String.Empty, stderr)
        let frames = Test.decodeAll stdout

        let errors =
            frames
            |> List.choose (function
                | Response(_, Some error, _) -> Some error.Code
                | _ -> None)

        Assert.Equal<string list>([ "not_initialized"; "unknown_method" ], errors)
        Assert.Equal(5, frames.Length)

        let malformedExit, malformedOutput, malformedError =
            Test.run RpcProfile.publicProfile [| 0xd4uy; 0uy; 0uy |] |> _.Result

        Assert.Equal(65, malformedExit)
        Assert.Empty(malformedOutput)
        Assert.Contains("protocol failure", malformedError)
