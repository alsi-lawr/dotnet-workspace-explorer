namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

open System
open System.IO
open System.Text.Json
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type PackageRpcContractTests() =
    let requestId = "11111111-1111-1111-1111-111111111111"

    let initialization major capabilities =
        Test.map
            [ "protocolVersion",
              Test.map [ "major", RpcValue.Integer major; "minor", RpcValue.Integer 0L ]
              "clientInfo", Test.map [ "name", RpcValue.String "contract-test" ]
              "capabilities", capabilities |> Seq.map RpcValue.String |> RpcValue.array
              "limits",
              Test.map
                  [ "maxFrameBytes", RpcValue.Integer 4096L; "maxPageSize", RpcValue.Integer 20L ] ]

    [<Fact>]
    member _.``package initialization negotiates only version two bounded capability input``() =
        match
            PackageRpc.parseInitialize (
                initialization 2L [ "packages.search.v2"; "packages.unknown"; "packages.search.v2" ]
            )
        with
        | Error error -> failwithf "Initialization failed: %A" error
        | Ok request ->
            request.ProtocolMinor |> should equal 0
            request.MaximumFrameBytes |> should equal 4096
            request.MaximumPageSize |> should equal 20

            request.Capabilities
            |> Seq.toList
            |> should equal [ "packages.search.v2"; "packages.unknown" ]

        match PackageRpc.parseInitialize (initialization 1L [ "packages.search.v1" ]) with
        | Error error -> error.Code |> should equal "invalid_params"
        | Ok _ -> failwith "An incompatible package protocol major version was accepted."

    [<Fact>]
    member _.``package request parsing retains project framework runtime and batch targets``() =
        let target =
            Test.map
                [ "project", RpcValue.String "/workspace/App.csproj"
                  "framework", RpcValue.String "net10.0"
                  "runtime", RpcValue.String "linux-x64" ]

        let parameters =
            Test.map
                [ "requestId", RpcValue.String requestId
                  "updates",
                  RpcValue.array
                      [ Test.map
                            [ "package", RpcValue.String "Example.Package"
                              "version", RpcValue.String "2.0.0"
                              "target", target ] ] ]

        match PackageRpc.parseRequest "package/previewBatch" parameters with
        | Ok(PackageRpcRequest.PreviewBatch(_, updates, None)) ->
            let update = updates |> NonEmptyList.toList |> List.exactlyOne

            match PackageUpdateSelection.target update with
            | PackageTargetScope.Runtime(project, framework, runtime) ->
                project.Value |> should equal "/workspace/App.csproj"
                framework.Value |> should equal "net10.0"
                runtime.Value |> should equal "linux-x64"
            | scope -> failwithf "Expected runtime target, got %A" scope
        | result -> failwithf "Batch preview parsing failed: %A" result

    [<Fact>]
    member _.``package parsing rejects removed discovery fields and invalid cancellation``() =
        let invalidSearch =
            Test.map
                [ "requestId", RpcValue.String requestId
                  "pageSize", RpcValue.Integer 20L
                  "unexpected", RpcValue.Boolean true ]

        match PackageRpc.parseRequest "package/search/start" invalidSearch with
        | Error error -> error.Code |> should equal "invalid_params"
        | Ok _ -> failwith "Invalid search parameters were accepted."

        let invalidCancellation =
            Test.map
                [ "requestId", RpcValue.String requestId
                  "operationId", RpcValue.String "22222222-2222-2222-2222-222222222222" ]

        match PackageRpc.parseRequest "package/cancel" invalidCancellation with
        | Error error -> error.Code |> should equal "invalid_params"
        | Ok _ -> failwith "Ambiguous cancellation parameters were accepted."

    [<Fact>]
    member _.``discovery terminals report exact empty and nonempty sequence metadata without rows``
        ()
        =
        let parsedRequestId =
            Guid.Parse requestId
            |> PackageRequestId.create
            |> Result.defaultWith (failwithf "%A")

        let parameters =
            function
            | Notification(_, value) -> value
            | _ -> failwith "Expected a notification."

        let empty =
            PackageRpcResponses.discoveryCompleted
                "package/installed/completed"
                parsedRequestId
                0
                0
                []
            |> parameters

        RpcValue.tryField "batchCount" empty |> should equal (Some(RpcValue.Integer 0L))
        RpcValue.tryField "itemCount" empty |> should equal (Some(RpcValue.Integer 0L))
        RpcValue.tryField "lastSequence" empty |> should equal None
        RpcValue.tryField "items" empty |> should equal None

        let nonempty =
            PackageRpcResponses.discoveryCompleted
                "package/updates/completed"
                parsedRequestId
                3
                7
                []
            |> parameters

        RpcValue.tryField "lastSequence" nonempty
        |> should equal (Some(RpcValue.Integer 2L))

        RpcValue.tryField "batchCount" nonempty
        |> should equal (Some(RpcValue.Integer 3L))

        RpcValue.tryField "itemCount" nonempty
        |> should equal (Some(RpcValue.Integer 7L))

        RpcValue.tryField "updates" nonempty |> should equal None

    [<Fact>]
    member _.``package failures redact dependency text while preserving recovery identities``() =
        let package =
            PackageId.create "Private.Package" |> Result.defaultWith (failwithf "%A")

        let project =
            PackageProjectId.create "/workspace/Private.csproj"
            |> Result.defaultWith (failwithf "%A")

        let failure =
            PackageFailure.create
                PackageFailureKind.Unauthorized
                "Bearer top-secret from https://private.example/index.json"
                PackageFailureRetry.AfterUserAction
            |> Result.defaultWith (failwithf "%A")
            |> PackageFailure.withRecovery
                [ { Package = package
                    Target = PackageTargetScope.Project project
                    State = PackageExecutionState.Uncertain } ]

        let projected = PackageRpcResponses.failureError failure
        projected.Code |> should equal "DWE-PACKAGE-UNAUTHORIZED"

        projected.Message
        |> should equal "The configured package source rejected the request."

        projected.Message |> should not' (haveSubstring "top-secret")
        projected.Data.IsSome |> should equal true

    [<Fact>]
    member _.``package schema reconciles every runtime response notification and stable error``() =
        let path =
            Path.Combine(AppContext.BaseDirectory, "protocol", "package-v2.schema.json")

        use document = JsonDocument.Parse(File.ReadAllText path)
        let root = document.RootElement
        let responses = root.GetProperty "responses"

        [ "initialize"
          "package/sources"
          "package/sourceMapping"
          "package/search/start"
          "package/details"
          "package/installed/start"
          "package/installed/restore/start"
          "package/updates/start"
          "package/consolidation/start"
          "package/preview"
          "package/previewBatch"
          "package/execute/start"
          "package/executeBatch/start"
          "package/cancel"
          "shutdown"
          "error" ]
        |> List.iter (fun name ->
            let mutable value = Unchecked.defaultof<JsonElement>
            responses.TryGetProperty(name, &value) |> should equal true)

        let notifications = root.GetProperty "notifications"

        PackageRpcContract.current.Methods.Values
        |> Seq.filter (fun method -> method.Classification = NotificationMethod)
        |> Seq.iter (fun method ->
            let mutable value = Unchecked.defaultof<JsonElement>
            notifications.TryGetProperty(method.Name, &value) |> should equal true)

        let errors =
            root.GetProperty("stableErrors").EnumerateArray()
            |> Seq.map (fun value -> value.GetProperty("code").GetString(), value)
            |> Map.ofSeq

        let message code =
            errors[code].GetProperty("message").GetString()

        let messages code =
            errors[code].GetProperty("messages").EnumerateArray()
            |> Seq.map _.GetString()
            |> Seq.toList

        messages "invalid_request"
        |> should
            equal
            [ "A request frame must contain exactly four values."
              "A request method must be a non-empty UTF-8 string."
              "A session cannot be initialized more than once." ]

        messages "invalid_params"
        |> should
            equal
            [ "Package initialization parameters are invalid."
              "Package request parameters are invalid."
              "The confirmation token does not identify a current preview of this operation kind."
              "Request params must be a string-key map."
              "MessagePack strings must contain valid UTF-8."
              "MessagePack nesting exceeds the configured limit."
              "MessagePack arrays exceed the configured item limit. (Parameter 'value')"
              "MessagePack maps exceed the configured item limit. (Parameter 'value')"
              "MessagePack map keys must be strings. (Parameter 'value')"
              "MessagePack map keys must be non-empty strings. (Parameter 'value')"
              "MessagePack maps cannot contain duplicate keys. (Parameter 'value')"
              "MessagePack extension values are not allowed. (Parameter 'value')" ]

        message RpcErrors.responseTooLarge.Code
        |> should equal RpcErrors.responseTooLarge.Message

        message RpcErrors.internalError.Code
        |> should equal RpcErrors.internalError.Message

        message PackageRpcResponses.discoveryInProgress.Code
        |> should equal PackageRpcResponses.discoveryInProgress.Message

        [ "package/installed"; "package/updates"; "package/consolidation" ]
        |> List.iter (fun name ->
            let mutable value = Unchecked.defaultof<JsonElement>
            responses.TryGetProperty(name, &value) |> should equal false)

        let authentication =
            PackageSourceFailure.create
                (PackageSourceId.create "private" |> Result.defaultWith (failwithf "%A"))
                PackageSourceFailureKind.AuthenticationRequired

        message (PackageSourceFailure.code authentication)
        |> should equal (PackageSourceFailure.message authentication)

        let unauthorized =
            PackageSourceFailure.create
                (PackageSourceId.create "private" |> Result.defaultWith (failwithf "%A"))
                PackageSourceFailureKind.Unauthorized

        message (PackageSourceFailure.code unauthorized)
        |> should equal (PackageSourceFailure.message unauthorized)
