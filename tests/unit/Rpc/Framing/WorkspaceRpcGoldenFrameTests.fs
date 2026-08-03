namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type WorkspaceRpcGoldenFrameTests() =
    [<Fact>]
    member _.``standard and public protocol frames round trip with stable golden wire bytes``() =
        let error =
            { Code = "e"
              Message = "m"
              Data = Some(Test.map [ "d", RpcValue.Integer 1L ]) }

        let cases =
            [ "standard-request.mpack", Request(7u, "x", Test.empty)
              "standard-response.mpack", Response(7u, Error error)
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
            ((golden = encoded)) |> should equal true
            (MessagePackRpcCodec.encodeFrame decoded) |> should equal (golden)

            match decoded with
            | Request(7u, "x", RpcValue.Map fields) -> (fields) |> should be Empty
            | Response(7u, Error decoded) ->
                (decoded.Code) |> should equal ("e")
                (decoded.Message) |> should equal ("m")

                (decoded.Data |> Option.bind (RpcValue.tryField "d"))
                |> should equal (Some(RpcValue.Unsigned 1UL))
            | Notification("n", parameters) ->
                (RpcValue.tryField "v" parameters) |> should equal (Some(RpcValue.Boolean true))
            | Request(_, "initialize", _)
            | Request(_, "workspace/root", _)
            | Request(_, "workspace/children", _)
            | Request(_, "workspace/refresh", _)
            | Request(_, "workspace/operations/cancel", _)
            | Request(_, "shutdown", _)
            | Notification("workspace/delta", _)
            | Notification("workspace/reset", _) -> ()
            | result -> failwithf "%s decoded unexpectedly: %A" name result
