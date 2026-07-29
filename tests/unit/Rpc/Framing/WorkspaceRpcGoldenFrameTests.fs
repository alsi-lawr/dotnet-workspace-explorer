namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type WorkspaceRpcGoldenFrameTests() =
    [<Fact>]
    member _.``should retain golden wire shapes for shared standard and public protocol frames``() =
        let error =
            { Code = "e"
              Message = "m"
              Data = Some(Test.map [ "d", RpcValue.Integer 1L ]) }

        let cases =
            [ "standard-request.mpack", Request(7u, "x", Test.empty)
              "standard-response.mpack", Response(7u, Some error, Test.empty)
              "standard-notification.mpack",
              Notification("n", Test.map [ "v", RpcValue.Boolean true ])
              "initialize-request.mpack",
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
              "workspace-root-request.mpack", Request(11u, "workspace/root", Test.empty)
              "workspace-children-request.mpack",
              Request(
                  12u,
                  "workspace/children",
                  Test.map
                      [ "parentNodeId", RpcValue.String "project:included"
                        "pageSize", RpcValue.Integer 1L ]
              )
              "workspace-refresh-request.mpack",
              Request(
                  13u,
                  "workspace/refresh",
                  Test.map [ "expectedRevision", RpcValue.Integer 1L ]
              )
              "workspace-delta-notification.mpack",
              Notification(
                  "workspace/delta",
                  Test.map
                      [ "workspaceId", RpcValue.String "fixture"
                        "baseRevision", RpcValue.Integer 1L
                        "newRevision", RpcValue.Integer 2L
                        "changes", RpcValue.array []
                        "diagnostics", RpcValue.array [] ]
              )
              "workspace-reset-notification.mpack",
              Notification(
                  "workspace/reset",
                  Test.map
                      [ "workspaceId", RpcValue.String "fixture"
                        "revision", RpcValue.Integer 3L
                        "diagnostics", RpcValue.array [] ]
              )
              "workspace-operations-cancel-request.mpack",
              Request(
                  14u,
                  "workspace/operations/cancel",
                  Test.map [ "operationId", RpcValue.String "fixture-export" ]
              )
              "shutdown-request.mpack", Request(15u, "shutdown", Test.empty) ]

        for name, frame in cases do
            let golden, decoded = Test.decodeGolden name
            let encoded = MessagePackRpcCodec.encodeFrame frame
            Assert.True((golden = encoded), sprintf "%s did not match its encoded frame." name)
            Assert.Equal<byte>(golden, MessagePackRpcCodec.encodeFrame decoded)

            match decoded with
            | Request(7u, "x", RpcValue.Map fields) -> Assert.Empty fields
            | Response(7u, Some decoded, RpcValue.Map result) ->
                Assert.Equal("e", decoded.Code)
                Assert.Equal("m", decoded.Message)

                Assert.Equal(
                    Some(RpcValue.Unsigned 1UL),
                    decoded.Data |> Option.bind (RpcValue.tryField "d")
                )

                Assert.Empty result
            | Notification("n", parameters) ->
                Assert.Equal(Some(RpcValue.Boolean true), RpcValue.tryField "v" parameters)
            | Request(_, "initialize", _)
            | Request(_, "workspace/root", _)
            | Request(_, "workspace/children", _)
            | Request(_, "workspace/refresh", _)
            | Request(_, "workspace/operations/cancel", _)
            | Request(_, "shutdown", _)
            | Notification("workspace/delta", _)
            | Notification("workspace/reset", _) -> ()
            | result -> failwithf "%s decoded unexpectedly: %A" name result
