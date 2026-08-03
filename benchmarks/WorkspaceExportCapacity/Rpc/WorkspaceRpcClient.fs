namespace Dotnet.WorkspaceExplorer.WorkspaceExportCapacity

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Testing

module internal WorkspaceRpcClient =
    let request = WorkspaceRpcTransport.request

    let send (child: Process) frame =
        WorkspaceRpcTransport.send child.StandardInput.BaseStream false frame

    let readFrame (child: Process) =
        match WorkspaceRpcTransport.readFrame child.StandardOutput.BaseStream with
        | Ok frame -> frame
        | Error message -> Arguments.fail message

    let response expectedId frame =
        match WorkspaceRpcTransport.response expectedId frame with
        | Ok(Ok result) -> result
        | Ok(Error error) ->
            Arguments.fail $"Request {expectedId} failed: {error.Code}: {error.Message}"
        | Error message -> Arguments.fail message

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
