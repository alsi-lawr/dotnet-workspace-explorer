namespace Dotnet.WorkspaceExplorer.WorkspaceExportCapacity

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc

module internal WorkspaceRpcClient =
    let request id methodName parameters =
        MessagePackRpcCodec.encodeFrame (Request(id, methodName, parameters))

    let send (child: Process) frame =
        child.StandardInput.BaseStream.Write(frame, 0, frame.Length)
        child.StandardInput.BaseStream.Flush()

    let readFrame (child: Process) =
        let pending = ResizeArray<byte>()
        let mutable result = None

        while result.IsNone do
            let value = child.StandardOutput.BaseStream.ReadByte()

            Arguments.require
                (value >= 0)
                "The apphost stdout ended before a complete frame arrived."

            pending.Add(byte value)

            match
                MessagePackRpcCodec.tryReadValueLength
                    MessagePackRpcCodec.secureLimits
                    (pending.ToArray())
            with
            | Error RpcFrameDecodeError.Incomplete -> ()
            | Error error -> Arguments.fail $"The apphost emitted invalid MessagePack: {error}"
            | Ok length when length = pending.Count ->
                match
                    MessagePackRpcCodec.decodeFrame
                        MessagePackRpcCodec.secureLimits
                        (pending.ToArray())
                with
                | Ok(RpcFrameDecodeResult.Frame frame) -> result <- Some frame
                | Ok(RpcFrameDecodeResult.RecoverableError _) ->
                    Arguments.fail "The apphost stdout contained a recoverable request error."
                | Error error -> Arguments.fail $"The apphost emitted an invalid frame: {error}"
            | Ok _ -> Arguments.fail "The frame reader consumed an unexpected byte count."

        result.Value

    let response expectedId =
        function
        | Response(id, None, result) when id = expectedId -> result
        | Response(id, Some error, _) when id = expectedId ->
            Arguments.fail $"Request {id} failed: {error.Code}: {error.Message}"
        | frame -> Arguments.fail $"Expected response {expectedId}, got {frame}."

    let field name value =
        value |> RpcValue.requireMap "parameters" |> RpcValue.requireField name

    let initialize =
        RpcValue.map
            [ "protocolVersion",
              RpcValue.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
              "clientInfo", RpcValue.map [ "name", RpcValue.String "system-capacity-benchmark" ]
              "capabilities",
              RpcValue.array
                  [ RpcValue.String "workspace.root"; RpcValue.String "workspace.export.start" ]
              "limits",
              RpcValue.map
                  [ "maxFrameBytes", RpcValue.Integer 65536L
                    "maxPageSize", RpcValue.Integer 100L ] ]
