namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type WorkspaceRpcInitializationTests() =
    [<Fact>]
    member _.``contextual rename move and copy require their exact target while unrelated commands retain optional workspace targeting``
        ()
        =
        let arguments = Test.empty

        let requests =
            [ "workspace/commands/describe",
              fun commandId -> Test.map [ "commandId", RpcValue.String commandId ]
              "workspace/commands/preview",
              fun commandId ->
                  Test.map
                      [ "commandId", RpcValue.String commandId
                        "arguments", arguments
                        "expectedRevision", RpcValue.Integer 0L ]
              "workspace/commands/execute",
              fun commandId ->
                  Test.map
                      [ "commandId", RpcValue.String commandId
                        "arguments", arguments
                        "expectedRevision", RpcValue.Integer 0L
                        "confirmationToken", RpcValue.String(String.replicate 64 "a") ] ]

        for methodName, parameters in requests do
            for commandId in [ "workspace.rename"; "workspace.move"; "workspace.copy" ] do
                match WorkspaceRpc.parseRequest methodName (parameters commandId) with
                | Error error -> error.Code |> should equal "invalid_params"
                | accepted ->
                    failwithf
                        "%s accepted %s without targetNodeId: %A"
                        methodName
                        commandId
                        accepted

            match WorkspaceRpc.parseRequest methodName (parameters "solution.validate") with
            | Ok _ -> ()
            | rejected ->
                failwithf
                    "%s stopped accepting an unrelated optional target: %A"
                    methodName
                    rejected

    [<Fact>]
    member _.``profiles gate request methods and reject notification calls while initialized``() =
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

            ((exitCode = 0)) |> should equal true
            (Test.responseErrors stdout) |> should equal (expectedErrors)

    [<Fact>]
    member _.``public initialization preserves capability, paging, and validation schemas``() =
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
                  "workspace.addExisting.selector"
                  "workspace.file.resolve"
                  "workspace.git.status"
                  "workspace.git.status.v2"
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

        (request.ProtocolMinor) |> should equal (0)
        (request.MaximumFrameBytes) |> should equal (4096)
        (request.MaximumPageSize) |> should equal (50)

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

        (resultFields.Keys |> Seq.sort |> Seq.toList)
        |> should equal ([ "capabilities"; "limits"; "protocolVersion"; "serverInfo"; "workspace" ])

        let negotiated =
            resultFields["capabilities"]
            |> RpcValue.requireArray "capabilities"
            |> Seq.map (RpcValue.requireString "capability")
            |> Seq.toList

        negotiated
        |> should
            equal
            [ "workspace.addExisting.selector"
              "workspace.create.options"
              "workspace.file.resolve"
              "workspace.git.status"
              "workspace.git.status.v2"
              "workspace.operations.cancel"
              "workspace.root" ]

        let resultLimits = resultFields["limits"] |> RpcValue.requireMap "limits"
        (resultLimits["maxFrameBytes"]) |> should equal (RpcValue.Integer 4096L)
        (resultLimits["maxPageSize"]) |> should equal (RpcValue.Integer 50L)

        let invalid =
            [ "missing fields", Test.empty
              "unsupported major", initialize 2L "test" [] None
              "blank client", initialize 1L "" [] None
              "duplicate capability", initialize 1L "test" [ "x"; "x" ] None ]

        for name, parameters in invalid do
            match WorkspaceRpc.parseInitialize parameters with
            | Error error -> (error.Code) |> should equal ("invalid_params")
            | Ok value -> failwithf "%s: expected invalid initialize, got %A" name value

        let defaults =
            initialize 1L "test" [] None
            |> WorkspaceRpc.parseInitialize
            |> Result.defaultWith (fun error -> failwith error.Message)

        (defaults.MaximumPageSize) |> should equal (256)

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
        | Error error -> (error.Code) |> should equal ("invalid_params")
        | result -> failwithf "oversized page should be rejected: %A" result

        match
            WorkspaceRpc.parseRequest
                "workspace/file/resolve"
                (Test.map
                    [ "targetNodeId", RpcValue.String "node"
                      "expectedRevision", RpcValue.Integer 12L ])
        with
        | Ok(WorkspaceRpcRequest.ResolveFile("node", 12L)) -> ()
        | result -> failwithf "file resolve schema changed: %A" result

        [ Test.map [ "targetNodeId", RpcValue.String "node" ]
          Test.map
              [ "targetNodeId", RpcValue.String "node"
                "expectedRevision", RpcValue.Integer -1L ]
          Test.map
              [ "targetNodeId", RpcValue.String "node"
                "expectedRevision", RpcValue.Integer 0L
                "extra", RpcValue.Boolean true ] ]
        |> List.iter (fun parameters ->
            match WorkspaceRpc.parseRequest "workspace/file/resolve" parameters with
            | Error error -> (error.Code) |> should equal ("invalid_params")
            | result -> failwithf "invalid file resolve request was accepted: %A" result)

        match
            WorkspaceRpc.parseRequest
                "workspace/git/status"
                (Test.map [ "expectedRevision", RpcValue.Integer 12L ])
        with
        | Ok(WorkspaceRpcRequest.GitStatus 12L) -> ()
        | result -> failwithf "git status schema changed: %A" result

        [ Test.empty
          Test.map [ "expectedRevision", RpcValue.Integer 0L; "extra", RpcValue.Boolean true ] ]
        |> List.iter (fun parameters ->
            match WorkspaceRpc.parseRequest "workspace/git/status" parameters with
            | Error error -> error.Code |> should equal "invalid_params"
            | result -> failwithf "invalid git status request was accepted: %A" result)

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
            | Error error -> (error.Code) |> should equal ("invalid_params")
            | result -> failwithf "invalid create options request was accepted: %A" result)

        match
            WorkspaceRpc.parseRequest
                "workspace/addExisting/start"
                (Test.map
                    [ "targetNodeId", RpcValue.String "node"
                      "selectionId", RpcValue.String "selection"
                      "expectedRevision", RpcValue.Integer 12L
                      "pageSize", RpcValue.Integer 25L ])
        with
        | Ok(WorkspaceRpcRequest.AddExistingStart("node", "selection", 12L, Some 25)) -> ()
        | result -> failwithf "Add Existing start schema changed: %A" result

        match
            WorkspaceRpc.parseRequest
                "workspace/addExisting/children"
                (Test.map
                    [ "selectorId", RpcValue.String "selector"
                      "parentEntryId", RpcValue.String "parent"
                      "continuationToken", RpcValue.String "next" ])
        with
        | Ok(WorkspaceRpcRequest.AddExistingChildren("selector", "parent", None, Some "next")) -> ()
        | result -> failwithf "Add Existing children schema changed: %A" result

        match
            WorkspaceRpc.parseRequest
                "workspace/addExisting/close"
                (Test.map [ "selectorId", RpcValue.String "selector" ])
        with
        | Ok(WorkspaceRpcRequest.AddExistingClose "selector") -> ()
        | result -> failwithf "Add Existing close schema changed: %A" result

        [ "workspace/addExisting/start", Test.empty
          "workspace/addExisting/children",
          Test.map
              [ "selectorId", RpcValue.String "selector"
                "parentEntryId", RpcValue.String "parent"
                "pageSize", RpcValue.Integer 0L ]
          "workspace/addExisting/close",
          Test.map [ "selectorId", RpcValue.String "selector"; "extra", RpcValue.Boolean true ] ]
        |> List.iter (fun (methodName, parameters) ->
            match WorkspaceRpc.parseRequest methodName parameters with
            | Error error -> error.Code |> should equal "invalid_params"
            | result -> failwithf "invalid %s request was accepted: %A" methodName result)
