namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Rpc

module internal Test =
    let map values = RpcValue.map values
    let empty = RpcValue.emptyMap

    let golden name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorkspaceRpc", name)
        |> File.ReadAllBytes

    let packageGolden name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "PackageRpc", name)
        |> File.ReadAllBytes

    let request id name parameters =
        MessagePackRpcCodec.encodeFrame (Request(id, name, parameters))

    let decode (bytes: byte array) =
        let rec consume offset decoded =
            if offset = bytes.Length then
                List.rev decoded
            else
                match
                    MessagePackRpcCodec.tryReadValueLength
                        MessagePackRpcCodec.secureLimits
                        bytes[offset..]
                with
                | Ok size ->
                    match
                        MessagePackRpcCodec.decodeFrame
                            MessagePackRpcCodec.secureLimits
                            bytes[offset .. offset + size - 1]
                    with
                    | Ok(RpcFrameDecodeResult.Frame frame) ->
                        consume (offset + size) ((frame, size) :: decoded)
                    | result -> failwithf "Response decode failed: %A" result
                | Error error -> failwithf "Response length decode failed: %A" error

        consume 0 []

    let frames bytes = decode bytes |> List.map fst

    let decodeGolden name =
        let bytes = golden name

        match MessagePackRpcCodec.tryReadValueLength MessagePackRpcCodec.secureLimits bytes with
        | Ok length when length = bytes.Length ->
            match MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits bytes with
            | Ok(RpcFrameDecodeResult.Frame frame) -> bytes, frame
            | result -> failwithf "%s did not decode as a frame: %A" name result
        | Ok length -> failwithf "%s had %d trailing bytes." name (bytes.Length - length)
        | Error error -> failwithf "%s did not have a complete frame: %A" name error

    let profile name methods =
        methods
        |> Seq.map (fun (methodName, classification) ->
            { Name = methodName
              Classification = classification })
        |> RpcProfile.create name 1 0

    let dispatchResult result stop =
        if stop then
            RpcRequestResult.Stop result
        else
            RpcRequestResult.Continue
                { Result = result
                  Notifications = []
                  BackgroundWork = None
                  AfterResponse = None }

    let dispatchResultWithBackground result background =
        RpcRequestResult.Continue
            { Result = result
              Notifications = []
              BackgroundWork = Some background
              AfterResponse = None }

    let configurationWithLimit profile getOutboundLimit initialize dispatch =
        { Profile = profile
          Limits = MessagePackRpcCodec.secureLimits
          GetOutboundFrameLimit = getOutboundLimit
          Initialize = initialize
          Dispatch = dispatch }

    let configuration profile initialize dispatch =
        configurationWithLimit
            profile
            (fun () -> MessagePackRpcCodec.secureLimits.MaximumValueBytes)
            initialize
            dispatch

    let defaultConfiguration profile =
        configuration
            profile
            (fun _ _ -> Task.FromResult(Ok(map [ "ok", RpcValue.Boolean true ])))
            (fun _ methodName _ _ ->
                Task.FromResult(
                    Ok(
                        dispatchResult
                            (map [ "method", RpcValue.String methodName ])
                            (methodName = "shutdown")
                    )
                ))

    let runStream configuration (input: Stream) cancellationToken =
        task {
            use output = new MemoryStream()
            use errors = new StringWriter()
            let! exitCode = RpcSession.runAsync configuration input output errors cancellationToken
            return exitCode, output.ToArray(), errors.ToString()
        }

    let run configuration (input: byte array) =
        use stream = new MemoryStream(input)
        runStream configuration stream CancellationToken.None |> _.Result

    let responseErrors bytes =
        frames bytes
        |> List.choose (function
            | Response(id, Error error) -> Some(id, error.Code)
            | _ -> None)
