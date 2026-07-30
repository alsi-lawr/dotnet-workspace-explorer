namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc
open Xunit

[<Collection("RPC scenarios")>]
type WorkspaceRpcInitializationTests() =
    [<Fact>]
    member _.``should gate requests with initialization profiles and notification callability``() =
        let worker =
            Test.profile "worker" [ "project-evaluation/evaluate", Read; "shutdown", Control ]

        let notifications =
            Test.profile
                "notifications"
                [ "workspace/export/chunk", NotificationMethod
                  "workspace/operations/completed", NotificationMethod
                  "shutdown", Control ]

        let cases =
            [ "initialization",
              WorkspaceRpcProfile.current,
              [ Test.request 1u "workspace/root" Test.empty
                Test.request 2u "initialize" Test.empty
                Test.request 3u "initialize" Test.empty
                Test.request 4u "shutdown" Test.empty ],
              [ 1u, "not_initialized"; 3u, "invalid_request" ]
              "worker profile isolation",
              worker,
              [ Test.request 1u "initialize" Test.empty
                Test.request 2u "workspace/root" Test.empty
                Test.request 3u "project-evaluation/evaluate" Test.empty
                Test.request 4u "shutdown" Test.empty ],
              [ 2u, "unknown_method" ]
              "public profile isolation",
              WorkspaceRpcProfile.current,
              [ Test.request 1u "initialize" Test.empty
                Test.request 2u "project-evaluation/evaluate" Test.empty
                Test.request 3u "shutdown" Test.empty ],
              [ 2u, "unknown_method" ]
              "notifications are not callable",
              notifications,
              [ Test.request 1u "initialize" Test.empty
                Test.request 2u "workspace/export/chunk" Test.empty
                Test.request 3u "workspace/operations/completed" Test.empty
                Test.request 4u "shutdown" Test.empty ],
              [ 2u, "unknown_method"; 3u, "unknown_method" ] ]

        for name, profile, requests, expectedErrors in cases do
            let exitCode, stdout, stderr =
                requests |> Array.concat |> Test.run (Test.defaultConfiguration profile)

            Assert.True((exitCode = 0), $"{name}: exit {exitCode}, {stderr}")
            Assert.Equal<(uint32 * string) list>(expectedErrors, Test.responseErrors stdout)

    [<Fact>]
    member _.``should keep public initialization and paging schemas stable``() =
        let initialize major client capabilities limits =
            let fields =
                ResizeArray<string * RpcValue>
                    [ "protocolVersion",
                      Test.map [ "major", RpcValue.Integer major; "minor", RpcValue.Integer 9L ]
                      "clientInfo", Test.map [ "name", RpcValue.String client ]
                      "capabilities", capabilities |> List.map RpcValue.String |> RpcValue.array ]


            limits |> Option.iter (fun value -> fields.Add("limits", value))
            Test.map fields

        let valid =
            initialize
                1L
                "test"
                [ "workspace.root"
                  "workspace.create.options"
                  "unknown.claim"
                  "workspace.operations.cancel" ]
                (Some(
                    Test.map
                        [ "maxFrameBytes", RpcValue.Integer 4096L
                          "maxPageSize", RpcValue.Integer 50L ]
                ))

        let request =
            WorkspaceRpc.parseInitialize valid
            |> Result.defaultWith (fun error -> failwith error.Message)

        Assert.Equal(0, request.ProtocolMinor)
        Assert.Equal(4096, request.MaximumFrameBytes)
        Assert.Equal(50, request.MaximumPageSize)

        let descriptor =
            WorkspaceDescriptor.Create(
                WorkspacePath.Create(Path.GetTempPath()),
                FileSystemCaseSensitivity.Sensitive,
                WorkspaceFormat.Slnf,
                WorkspaceRevision.Create 0L,
                WorkspaceAccess.ReadWrite
            )

        let resultFields =
            WorkspaceRpcResponses.initializeResult descriptor 0L request
            |> RpcValue.requireMap "initialize.result"

        Assert.Equal<string list>(
            [ "capabilities"; "limits"; "protocolVersion"; "serverInfo"; "workspace" ],
            resultFields.Keys |> Seq.sort |> Seq.toList
        )

        let negotiated =
            resultFields["capabilities"]
            |> RpcValue.requireArray "capabilities"
            |> Seq.map (RpcValue.requireString "capability")
            |> Seq.toList

        Assert.Equal<string list>(
            [ "workspace.create.options"; "workspace.operations.cancel"; "workspace.root" ],
            negotiated
        )

        let resultLimits = resultFields["limits"] |> RpcValue.requireMap "limits"
        Assert.Equal(RpcValue.Integer 4096L, resultLimits["maxFrameBytes"])
        Assert.Equal(RpcValue.Integer 50L, resultLimits["maxPageSize"])

        let invalid =
            [ "missing fields", Test.empty
              "unsupported major", initialize 2L "test" [] None
              "blank client", initialize 1L "" [] None
              "duplicate capability", initialize 1L "test" [ "x"; "x" ] None ]

        for name, parameters in invalid do
            match WorkspaceRpc.parseInitialize parameters with
            | Error error -> Assert.Equal("invalid_params", error.Code)
            | Ok value -> failwithf "%s: expected invalid initialize, got %A" name value

        let defaults =
            initialize 1L "test" [] None
            |> WorkspaceRpc.parseInitialize
            |> Result.defaultWith (fun error -> failwith error.Message)

        Assert.Equal(256, defaults.MaximumPageSize)

        let children pageSize =
            WorkspaceRpc.parseRequest
                "workspace/children"
                (Test.map
                    [ "parentNodeId", RpcValue.String "parent"
                      "pageSize", RpcValue.Integer pageSize
                      "continuationToken", RpcValue.String "next" ])

        match children 4096L with
        | Ok(WorkspaceRpcRequest.Children("parent", Some 4096, Some "next")) -> ()
        | result -> failwithf "maximum page and continuation schema changed: %A" result

        match children 4097L with
        | Error error -> Assert.Equal("invalid_params", error.Code)
        | result -> failwithf "oversized page should be rejected: %A" result

        match
            WorkspaceRpc.parseRequest
                "workspace/create/options"
                (Test.map
                    [ "targetNodeId", RpcValue.String "node"
                      "expectedRevision", RpcValue.Integer 12L ])
        with
        | Ok(WorkspaceRpcRequest.CreateOptions("node", 12L)) -> ()
        | result -> failwithf "create options schema changed: %A" result

        [ Test.map [ "targetNodeId", RpcValue.String "node" ]
          Test.map
              [ "targetNodeId", RpcValue.String "node"
                "expectedRevision", RpcValue.Integer 0L
                "extra", RpcValue.Boolean true ] ]
        |> List.iter (fun parameters ->
            match WorkspaceRpc.parseRequest "workspace/create/options" parameters with
            | Error error -> Assert.Equal("invalid_params", error.Code)
            | result -> failwithf "invalid create options request was accepted: %A" result)
