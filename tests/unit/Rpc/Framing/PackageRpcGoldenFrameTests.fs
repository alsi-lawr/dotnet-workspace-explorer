namespace Dotnet.WorkspaceExplorer.Rpc.UnitTests

open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("RPC scenarios")>]
type PackageRpcGoldenFrameTests() =
    [<Fact>]
    member _.``package protocol capability families retain stable independent-client frames``() =
        let requestId = "11111111-1111-1111-1111-111111111111"
        let restoreRequestId = "33333333-3333-3333-3333-333333333333"
        let operationId = "22222222-2222-2222-2222-222222222222"
        let empty = RpcValue.emptyMap
        let map = RpcValue.map
        let text value = RpcValue.String value
        let integer value = RpcValue.Integer value

        let target = map [ "project", text "/workspace/App.fsproj" ]

        let recoveryData =
            map [ "retry", text "afterUserAction"; "recovery", RpcValue.array [] ]

        let cancelledData = map [ "retry", text "never"; "recovery", RpcValue.array [] ]

        let cases =
            [ "initialize-request.mpack",
              Request(
                  1u,
                  "initialize",
                  map
                      [ "protocolVersion", map [ "major", integer 2L; "minor", integer 0L ]
                        "clientInfo", map [ "name", text "package-fixture" ]
                        "capabilities",
                        RpcValue.array [ text "packages.search.v2"; text "packages.execute.v1" ]
                        "limits",
                        map [ "maxFrameBytes", integer 65536L; "maxPageSize", integer 25L ] ]
              )
              "search-request.mpack",
              Request(
                  2u,
                  "package/search/start",
                  map
                      [ "requestId", text requestId
                        "term", text "example"
                        "includePrerelease", RpcValue.Boolean false ]
              )
              "sources-request.mpack",
              Request(10u, "package/sources", map [ "requestId", text requestId ])
              "source-mapping-request.mpack",
              Request(
                  11u,
                  "package/sourceMapping",
                  map
                      [ "requestId", text requestId
                        "package", text "Example.Package"
                        "restoredTransitives", RpcValue.array [] ]
              )
              "details-request.mpack",
              Request(
                  3u,
                  "package/details",
                  map
                      [ "requestId", text requestId
                        "package", text "Example.Package"
                        "version", map [ "kind", text "latest" ]
                        "source", text "nuget.org" ]
              )
              "installed-start-request.mpack",
              Request(4u, "package/installed/start", map [ "requestId", text requestId ])
              "installed-restore-start-request.mpack",
              Request(
                  15u,
                  "package/installed/restore/start",
                  map [ "requestId", text restoreRequestId ]
              )
              "updates-start-request.mpack",
              Request(
                  12u,
                  "package/updates/start",
                  map [ "requestId", text requestId; "includePrerelease", RpcValue.Boolean false ]
              )
              "consolidation-start-request.mpack",
              Request(13u, "package/consolidation/start", map [ "requestId", text requestId ])
              "preview-request.mpack",
              Request(
                  5u,
                  "package/preview",
                  map
                      [ "requestId", text requestId
                        "operation",
                        map
                            [ "kind", text "updateVersion"
                              "package", text "Example.Package"
                              "version", text "2.0.0" ]
                        "targets", RpcValue.array [ target ] ]
              )
              "preview-batch-request.mpack",
              Request(
                  6u,
                  "package/previewBatch",
                  map
                      [ "requestId", text requestId
                        "updates",
                        RpcValue.array
                            [ map
                                  [ "package", text "Example.Package"
                                    "version", text "2.0.0"
                                    "target", target ] ] ]
              )
              "execute-request.mpack",
              Request(
                  7u,
                  "package/execute/start",
                  map [ "requestId", text requestId; "confirmationToken", text "PREVIEW-TOKEN" ]
              )
              "execute-batch-request.mpack",
              Request(
                  14u,
                  "package/executeBatch/start",
                  map
                      [ "requestId", text requestId
                        "confirmationToken", text "BATCH-PREVIEW-TOKEN" ]
              )
              "cancel-request.mpack",
              Request(8u, "package/cancel", map [ "operationId", text operationId ])
              "operation-progress.mpack",
              Notification(
                  "package/operations/progress",
                  map
                      [ "requestId", text requestId
                        "progress",
                        map
                            [ "operationId", text operationId
                              "stage", text "applying"
                              "completed", integer 1L
                              "total", integer 2L ] ]
              )
              "operation-error.mpack",
              Notification(
                  "package/operations/completed",
                  map
                      [ "requestId", text requestId
                        "error",
                        map
                            [ "code", text "DWE-PACKAGE-PARTIAL-RECOVERY"
                              "message", text "Package recovery requires user attention."
                              "data", recoveryData ] ]
              )
              "shutdown-request.mpack", Request(9u, "shutdown", empty) ]

        let installedState =
            map
                [ "kind", text "direct"
                  "requested", map [ "kind", text "exact"; "value", text "1.0.0" ]
                  "resolved", text "1.0.0" ]

        let deprecation =
            map
                [ "kind", text "deprecated"
                  "reasons", RpcValue.array [ text "legacy" ]
                  "alternate",
                  map [ "package", text "Example.Replacement"; "versionRange", text "[2.0.0,)" ] ]

        let candidateVersions = RpcValue.array [ text "2.0.0" ]

        let sourceAuthenticationMessage =
            "The configured package source requires authentication."

        let targetPreview =
            map
                [ "target", target
                  "change",
                  map
                      [ "kind", text "update"
                        "current", installedState
                        "proposed", map [ "kind", text "direct"; "version", text "2.0.0" ] ]
                  "ownerFiles", RpcValue.array [ text "/workspace/App.fsproj" ]
                  "graphFreshness", text "current"
                  "impact",
                  map
                      [ "metadata",
                        map
                            [ "kind", text "known"
                              "dependencies",
                              RpcValue.array
                                  [ map
                                        [ "package", text "Dependency"
                                          "versionRange", text "[1.0.0,)" ] ]
                              "deprecation", deprecation
                              "vulnerabilities",
                              RpcValue.array
                                  [ map
                                        [ "severity", text "high"
                                          "advisory", text "https://example.test/advisory" ] ]
                              "license", text "MIT" ]
                        "sourceMapping",
                        map
                            [ "kind", text "browseSourceDoesNotConstrainApply"
                              "browseSource", text "nuget.org"
                              "allowedSources", RpcValue.array [ text "nuget.org" ] ]
                        "restore",
                        map
                            [ "kind", text "requiredWithUnknownOutcome"
                              "graphFreshness", text "current" ] ] ]

        let preview =
            map
                [ "operation",
                  map
                      [ "kind", text "updateVersion"
                        "package", text "Example.Package"
                        "version", text "2.0.0" ]
                  "targets", RpcValue.array [ targetPreview ]
                  "ownerFiles", RpcValue.array [ text "/workspace/App.fsproj" ]
                  "workspaceRevision", text "revision-1"
                  "fileFingerprints",
                  RpcValue.array
                      [ map
                            [ "path", text "/workspace/App.fsproj"
                              "fingerprint", text "fingerprint-1" ] ]
                  "confirmationToken", text "PREVIEW-TOKEN" ]

        let responseCases =
            [ "initialize-response.mpack",
              Response(
                  1u,
                  Ok(
                      map
                          [ "protocolVersion", map [ "major", integer 2L; "minor", integer 0L ]
                            "serverInfo",
                            map [ "name", text "dotnet-workspace-explorer"; "version", text "1" ]
                            "target",
                            map
                                [ "path", text "/workspace/App.fsproj"
                                  "kind", text "project:fsharp" ]
                            "capabilities",
                            RpcValue.array [ text "packages.details.v1"; text "packages.readme.v1" ]
                            "limits",
                            map
                                [ "maxFrameBytes", integer 65536L
                                  "maxPageSize", integer 25L
                                  "maxDepth", integer 64L ] ]
                  )
              )
              "sources-response.mpack",
              Response(
                  10u,
                  Ok(
                      map
                          [ "sources",
                            RpcValue.array
                                [ map
                                      [ "id", text "nuget.org"
                                        "name", text "nuget.org"
                                        "location", text "https://api.nuget.org/v3/index.json"
                                        "availability", text "available" ] ] ]
                  )
              )
              "source-mapping-response.mpack",
              Response(
                  11u,
                  Ok(map [ "kind", text "allowed"; "sources", RpcValue.array [ text "nuget.org" ] ])
              )
              "accepted-response.mpack",
              Response(
                  2u,
                  Ok(map [ "accepted", RpcValue.Boolean true; "requestId", text requestId ])
              )
              "details-response.mpack",
              Response(
                  3u,
                  Ok(
                      map
                          [ "summary",
                            map
                                [ "package", text "Example.Package"
                                  "version", text "2.0.0"
                                  "tags", RpcValue.array [ text "example" ]
                                  "authors", RpcValue.array [ text "ALSI" ]
                                  "owners", RpcValue.array [ text "ALSI" ]
                                  "source", text "nuget.org" ]
                            "versions", RpcValue.array [ text "2.0.0"; text "1.0.0" ]
                            "authors", RpcValue.array [ text "ALSI" ]
                            "dependencyGroups", RpcValue.array []
                            "deprecation", deprecation
                            "vulnerabilities", RpcValue.array []
                            "readmeCommonMark", text "# Example" ]
                  )
              )
              "installed-accepted-response.mpack",
              Response(
                  4u,
                  Ok(map [ "accepted", RpcValue.Boolean true; "requestId", text requestId ])
              )
              "installed-restore-accepted-response.mpack",
              Response(
                  15u,
                  Ok(map [ "accepted", RpcValue.Boolean true; "requestId", text restoreRequestId ])
              )
              "installed-batch.mpack",
              Notification(
                  "package/installed/batch",
                  map
                      [ "requestId", text requestId
                        "sequence", integer 0L
                        "items",
                        RpcValue.array
                            [ map
                                  [ "target", target
                                    "graphState", text "current"
                                    "package",
                                    map
                                        [ "package", text "Example.Package"
                                          "target", target
                                          "state", installedState ] ] ] ]
              )
              "installed-completed.mpack",
              Notification(
                  "package/installed/completed",
                  map
                      [ "requestId", text requestId
                        "state", text "completed"
                        "batchCount", integer 1L
                        "itemCount", integer 1L
                        "lastSequence", integer 0L ]
              )
              "installed-restore-batch.mpack",
              Notification(
                  "package/installed/restore/batch",
                  map
                      [ "requestId", text restoreRequestId
                        "sequence", integer 0L
                        "items",
                        RpcValue.array
                            [ map
                                  [ "target", target
                                    "graphState", text "current"
                                    "package",
                                    map
                                        [ "package", text "Example.Package"
                                          "target", target
                                          "state", installedState ] ] ] ]
              )
              "installed-restore-completed.mpack",
              Notification(
                  "package/installed/restore/completed",
                  map
                      [ "requestId", text restoreRequestId
                        "state", text "completed"
                        "batchCount", integer 1L
                        "itemCount", integer 1L
                        "lastSequence", integer 0L ]
              )
              "preview-response.mpack", Response(5u, Ok preview)
              "preview-batch-response.mpack",
              Response(
                  6u,
                  Ok(
                      map
                          [ "updates",
                            RpcValue.array
                                [ map
                                      [ "package", text "Example.Package"
                                        "targetPreview", targetPreview
                                        "version", text "2.0.0" ] ]
                            "ownerFiles", RpcValue.array [ text "/workspace/App.fsproj" ]
                            "workspaceRevision", text "revision-1"
                            "fileFingerprints",
                            RpcValue.array
                                [ map
                                      [ "path", text "/workspace/App.fsproj"
                                        "fingerprint", text "fingerprint-1" ] ]
                            "confirmationToken", text "BATCH-PREVIEW-TOKEN" ]
                  )
              )
              "cancel-response.mpack", Response(8u, Ok(map [ "accepted", RpcValue.Boolean true ]))
              "shutdown-response.mpack", Response(9u, Ok(map [ "accepted", RpcValue.Boolean true ]))
              "updates-batch.mpack",
              Notification(
                  "package/updates/batch",
                  map
                      [ "requestId", text requestId
                        "sequence", integer 0L
                        "updates",
                        RpcValue.array
                            [ map
                                  [ "package", text "Example.Package"
                                    "target", target
                                    "available", RpcValue.array [ text "2.0.0" ]
                                    "installedVersion", text "1.0.0" ] ] ]
              )
              "updates-completed.mpack",
              Notification(
                  "package/updates/completed",
                  map
                      [ "requestId", text requestId
                        "state", text "completed"
                        "batchCount", integer 1L
                        "itemCount", integer 1L
                        "lastSequence", integer 0L ]
              )
              "consolidation-batch.mpack",
              Notification(
                  "package/consolidation/batch",
                  map
                      [ "requestId", text requestId
                        "sequence", integer 0L
                        "packages",
                        RpcValue.array
                            [ map
                                  [ "package", text "Example.Package"
                                    "currentVersions",
                                    RpcValue.array
                                        [ map
                                              [ "version", text "1.0.0"
                                                "targets", RpcValue.array [ target ] ] ]
                                    "candidateVersions", candidateVersions ] ] ]
              )
              "consolidation-completed.mpack",
              Notification(
                  "package/consolidation/completed",
                  map
                      [ "requestId", text requestId
                        "state", text "completed"
                        "batchCount", integer 1L
                        "itemCount", integer 1L
                        "lastSequence", integer 0L ]
              )
              "search-batch.mpack",
              Notification(
                  "package/search/batch",
                  map
                      [ "requestId", text requestId
                        "sequence", integer 0L
                        "items",
                        RpcValue.array
                            [ map
                                  [ "package", text "Example.Package"
                                    "version", text "2.0.0"
                                    "tags", RpcValue.array [ text "example" ]
                                    "authors", RpcValue.array [ text "ALSI" ]
                                    "owners", RpcValue.array [ text "ALSI" ]
                                    "source", text "nuget.org" ]
                              map
                                  [ "package", text "Example.Package"
                                    "version", text "2.0.0"
                                    "tags", RpcValue.array [ text "example" ]
                                    "authors", RpcValue.array [ text "ALSI" ]
                                    "owners", RpcValue.array [ text "ALSI" ]
                                    "source", text "nuget.org" ] ]
                        "sourceFailures", RpcValue.array [] ]
              )
              "search-completed.mpack",
              Notification(
                  "package/search/completed",
                  map
                      [ "requestId", text requestId
                        "state", text "completed"
                        "batchCount", integer 2L
                        "itemCount", integer 2L
                        "lastSequence", integer 1L
                        "query",
                        map [ "term", text "example"; "includePrerelease", RpcValue.Boolean false ]
                        "continuation", text "opaque-next" ]
              )
              "operation-completed.mpack",
              Notification(
                  "package/operations/completed",
                  map
                      [ "requestId", text requestId
                        "result",
                        map
                            [ "operationId", text operationId
                              "entries", RpcValue.array []
                              "changedFiles", RpcValue.array []
                              "restore", text "notRequired" ] ]
              )
              "transport-error-response.mpack",
              Response(
                  15u,
                  Error
                      { Code = "response_too_large"
                        Message = "The response exceeds the negotiated outbound frame limit."
                        Data = None }
              )
              "search-source-failure-batch.mpack",
              Notification(
                  "package/search/batch",
                  map
                      [ "requestId", text requestId
                        "sequence", integer 1L
                        "items", RpcValue.array []
                        "sourceFailures",
                        RpcValue.array
                            [ map
                                  [ "source", text "private"
                                    "code", text "DWE-PACKAGE-SOURCE-AUTHENTICATION-REQUIRED"
                                    "message", text sourceAuthenticationMessage ] ] ]
              )
              "updates-cancelled.mpack",
              Notification(
                  "package/updates/completed",
                  map
                      [ "requestId", text requestId
                        "state", text "cancelled"
                        "batchCount", integer 1L
                        "itemCount", integer 1L
                        "lastSequence", integer 0L
                        "error",
                        map
                            [ "code", text "DWE-PACKAGE-CANCELLED"
                              "message", text "The package work was cancelled."
                              "data", cancelledData ] ]
              )
              "consolidation-failed.mpack",
              Notification(
                  "package/consolidation/completed",
                  map
                      [ "requestId", text requestId
                        "state", text "failed"
                        "batchCount", integer 0L
                        "itemCount", integer 0L
                        "error",
                        map
                            [ "code", text "response_too_large"
                              "message",
                              text "The response exceeds the negotiated outbound frame limit." ] ]
              )
              "discovery-in-progress-response.mpack",
              Response(
                  16u,
                  Error
                      { Code = "discovery_in_progress"
                        Message = "A package discovery stream of this kind is already active."
                        Data = Some(map [ "retry", text "transient" ]) }
              ) ]

        let recoverableErrorCases =
            [ "invalid-request-arity-response.mpack",
              20u,
              "invalid_request",
              "A request frame must contain exactly four values."
              "invalid-request-method-response.mpack",
              21u,
              "invalid_request",
              "A request method must be a non-empty UTF-8 string."
              "invalid-params-map-response.mpack",
              22u,
              "invalid_params",
              "Request params must be a string-key map."
              "invalid-params-utf8-response.mpack",
              23u,
              "invalid_params",
              "MessagePack strings must contain valid UTF-8."
              "invalid-params-depth-response.mpack",
              24u,
              "invalid_params",
              "MessagePack nesting exceeds the configured limit."
              "invalid-params-array-limit-response.mpack",
              25u,
              "invalid_params",
              "MessagePack arrays exceed the configured item limit. (Parameter 'value')"
              "invalid-params-map-limit-response.mpack",
              26u,
              "invalid_params",
              "MessagePack maps exceed the configured item limit. (Parameter 'value')"
              "invalid-params-map-key-response.mpack",
              27u,
              "invalid_params",
              "MessagePack map keys must be strings. (Parameter 'value')"
              "invalid-params-empty-map-key-response.mpack",
              28u,
              "invalid_params",
              "MessagePack map keys must be non-empty strings. (Parameter 'value')"
              "invalid-params-duplicate-map-key-response.mpack",
              29u,
              "invalid_params",
              "MessagePack maps cannot contain duplicate keys. (Parameter 'value')"
              "invalid-params-extension-response.mpack",
              30u,
              "invalid_params",
              "MessagePack extension values are not allowed. (Parameter 'value')"
              "invalid-request-reinitialize-response.mpack",
              31u,
              "invalid_request",
              "A session cannot be initialized more than once." ]
            |> List.map (fun (name, id, code, message) ->
                name,
                Response(
                    id,
                    Error
                        { Code = code
                          Message = message
                          Data = None }
                ))

        let allCases = cases @ responseCases @ recoverableErrorCases

        for name, frame in allCases do
            let encoded = MessagePackRpcCodec.encodeFrame frame

            let golden = Test.packageGolden name

            encoded |> should equal golden

            match MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits golden with
            | Ok(RpcFrameDecodeResult.Frame decoded) ->
                MessagePackRpcCodec.encodeFrame decoded |> should equal golden
            | outcome -> failwithf "%s did not decode: %A" name outcome
