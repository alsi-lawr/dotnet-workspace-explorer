namespace Dotnet.WorkspaceExplorer.Rpc

open System
open System.Collections.Generic
open System.Collections.Immutable

[<RequireQualifiedAccess>]
module WorkspaceRpc =
    let private invalid message =
        Error
            { Code = "invalid_params"
              Message = message
              Data = None }

    let private positiveInt minimum maximum name value =
        let parsed = RpcValue.requireInteger name value

        if parsed < int64 minimum || parsed > int64 maximum then
            invalidArg name $"Expected an integer between {minimum} and {maximum}."

        int parsed

    let private requiredString name fields =
        fields |> RpcValue.requireField name |> RpcValue.requireString name

    let private optionalString name fields =
        RpcValue.optionalField name fields |> Option.map (RpcValue.requireString name)

    let private revision name value =
        let parsed = RpcValue.requireInteger name value

        if parsed < 0L then
            invalidArg name "Expected a non-negative revision."

        parsed

    let private confirmationToken fields =
        optionalString "confirmationToken" fields
        |> Option.map (fun value ->
            if value.Length <> 64 || value |> Seq.exists (Char.IsAsciiHexDigit >> not) then
                invalidArg
                    "confirmationToken"
                    "Expected a 64-character hexadecimal confirmation token."

            value)

    let private requireEmpty parameters =
        let fields = RpcValue.requireMap "params" parameters

        if fields.Count <> 0 then
            invalidArg "params" "This method does not accept parameters."

    let parseInitialize parameters =
        try
            let fields = RpcValue.requireMap "initialize.params" parameters

            RpcValue.ensureOnly
                "initialize.params"
                [ "protocolVersion"; "clientInfo"; "capabilities"; "limits" ]
                fields

            let version =
                fields
                |> RpcValue.requireField "protocolVersion"
                |> RpcValue.requireMap "protocolVersion"

            RpcValue.ensureOnly "protocolVersion" [ "major"; "minor" ] version

            let major =
                version
                |> RpcValue.requireField "major"
                |> RpcValue.requireInteger "protocolVersion.major"

            let minor =
                version
                |> RpcValue.requireField "minor"
                |> RpcValue.requireInteger "protocolVersion.minor"

            if major <> 1L then
                invalidArg "protocolVersion.major" "Only protocol major version 1 is supported."

            if minor < 0L || minor > int64 Int32.MaxValue then
                invalidArg "protocolVersion.minor" "The protocol minor version is invalid."

            let clientInfo =
                fields |> RpcValue.requireField "clientInfo" |> RpcValue.requireMap "clientInfo"

            let clientName = requiredString "name" clientInfo

            if String.IsNullOrWhiteSpace clientName then
                invalidArg "clientInfo.name" "Client name must be a non-empty string."

            let capabilities =
                fields
                |> RpcValue.requireField "capabilities"
                |> RpcValue.requireArray "capabilities"

            let capabilityNames = ImmutableArray.CreateBuilder<string> capabilities.Length
            let unique = HashSet<string> StringComparer.Ordinal

            for capability in capabilities do
                let name = RpcValue.requireString "capabilities" capability

                if String.IsNullOrWhiteSpace name then
                    invalidArg "capabilities" "Capability names must be non-empty strings."

                if not (unique.Add name) then
                    invalidArg "capabilities" "Capability names must be unique."

                capabilityNames.Add name

            let maximumFrameBytes, maximumPageSize =
                match RpcValue.optionalField "limits" fields with
                | None -> MessagePackRpcCodec.secureLimits.MaximumValueBytes, 256
                | Some value ->
                    let limits = RpcValue.requireMap "limits" value
                    RpcValue.ensureOnly "limits" [ "maxFrameBytes"; "maxPageSize" ] limits

                    let frame =
                        match RpcValue.optionalField "maxFrameBytes" limits with
                        | Some requested ->
                            positiveInt 1024 Int32.MaxValue "limits.maxFrameBytes" requested
                        | None -> MessagePackRpcCodec.secureLimits.MaximumValueBytes

                    let page =
                        match RpcValue.optionalField "maxPageSize" limits with
                        | Some requested -> positiveInt 1 4096 "limits.maxPageSize" requested
                        | None -> 256

                    min frame MessagePackRpcCodec.secureLimits.MaximumValueBytes, page

            Ok
                { ProtocolMinor = 0
                  ClientName = clientName
                  Capabilities = capabilityNames.MoveToImmutable()
                  MaximumFrameBytes = maximumFrameBytes
                  MaximumPageSize = maximumPageSize }
        with
        | :? ArgumentException as error -> invalid error.Message
        | _ -> invalid "Initialize parameters are invalid."

    let parseRequest methodName parameters =
        try
            let parsed =
                match methodName with
                | "workspace/root" ->
                    requireEmpty parameters
                    WorkspaceRpcRequest.Root
                | "workspace/export/start" ->
                    requireEmpty parameters
                    WorkspaceRpcRequest.Export
                | "workspace/refresh" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "expectedRevision" ] fields

                    WorkspaceRpcRequest.Refresh(
                        RpcValue.optionalField "expectedRevision" fields
                        |> Option.map (RpcValue.requireInteger "expectedRevision")
                    )
                | "workspace/children" ->
                    let fields = RpcValue.requireMap "params" parameters

                    RpcValue.ensureOnly
                        "params"
                        [ "parentNodeId"; "pageSize"; "continuationToken" ]
                        fields

                    WorkspaceRpcRequest.Children(
                        requiredString "parentNodeId" fields,
                        RpcValue.optionalField "pageSize" fields
                        |> Option.map (positiveInt 1 4096 "pageSize"),
                        optionalString "continuationToken" fields
                    )
                | "workspace/file/resolve" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "targetNodeId"; "expectedRevision" ] fields

                    WorkspaceRpcRequest.ResolveFile(
                        requiredString "targetNodeId" fields,
                        fields
                        |> RpcValue.requireField "expectedRevision"
                        |> revision "expectedRevision"
                    )
                | "workspace/git/status" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "expectedRevision" ] fields

                    WorkspaceRpcRequest.GitStatus(
                        fields
                        |> RpcValue.requireField "expectedRevision"
                        |> revision "expectedRevision"
                    )
                | "workspace/create/options" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "targetNodeId"; "expectedRevision" ] fields

                    WorkspaceRpcRequest.CreateOptions(
                        requiredString "targetNodeId" fields,
                        fields
                        |> RpcValue.requireField "expectedRevision"
                        |> revision "expectedRevision"
                    )
                | "workspace/commands/list" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "targetNodeId" ] fields
                    WorkspaceRpcRequest.CommandList(optionalString "targetNodeId" fields)
                | "workspace/commands/describe" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "commandId"; "targetNodeId" ] fields

                    WorkspaceRpcRequest.CommandDescribe(
                        requiredString "commandId" fields,
                        optionalString "targetNodeId" fields
                    )
                | "workspace/commands/preview" ->
                    let fields = RpcValue.requireMap "params" parameters

                    RpcValue.ensureOnly
                        "params"
                        [ "commandId"; "targetNodeId"; "arguments"; "expectedRevision" ]
                        fields

                    let arguments = fields |> RpcValue.requireField "arguments"
                    RpcValue.requireMap "arguments" arguments |> ignore

                    let expectedRevision =
                        fields
                        |> RpcValue.requireField "expectedRevision"
                        |> revision "expectedRevision"

                    WorkspaceRpcRequest.CommandPreview(
                        requiredString "commandId" fields,
                        optionalString "targetNodeId" fields,
                        arguments,
                        expectedRevision
                    )
                | "workspace/commands/execute" ->
                    let fields = RpcValue.requireMap "params" parameters

                    RpcValue.ensureOnly
                        "params"
                        [ "commandId"
                          "targetNodeId"
                          "arguments"
                          "expectedRevision"
                          "confirmationToken" ]
                        fields

                    let arguments = fields |> RpcValue.requireField "arguments"
                    RpcValue.requireMap "arguments" arguments |> ignore

                    let expectedRevision =
                        fields
                        |> RpcValue.requireField "expectedRevision"
                        |> revision "expectedRevision"

                    WorkspaceRpcRequest.CommandExecute(
                        requiredString "commandId" fields,
                        optionalString "targetNodeId" fields,
                        arguments,
                        expectedRevision,
                        confirmationToken fields
                    )
                | "workspace/operations/cancel" ->
                    let fields = RpcValue.requireMap "params" parameters
                    RpcValue.ensureOnly "params" [ "operationId" ] fields
                    WorkspaceRpcRequest.Cancel(requiredString "operationId" fields)
                | "shutdown" ->
                    requireEmpty parameters
                    WorkspaceRpcRequest.Shutdown
                | _ -> invalidArg "methodName" "The method is not part of the public protocol."

            Ok parsed
        with
        | :? ArgumentException as error -> invalid error.Message
        | _ -> invalid "Request parameters are invalid."


[<RequireQualifiedAccess>]
module WorkspaceRpcProfile =
    let current =
        RpcProfile.create
            "dotnet-workspace-explorer/workspace"
            1
            0
            [ for name in
                  [ "initialize"
                    "workspace/root"
                    "workspace/children"
                    "workspace/file/resolve"
                    "workspace/git/status"
                    "workspace/export/start"
                    "workspace/refresh"
                    "workspace/create/options"
                    "workspace/commands/list"
                    "workspace/commands/describe"
                    "workspace/commands/preview"
                    "workspace/commands/execute"
                    "workspace/operations/cancel"
                    "shutdown" ] do
                  { Name = name
                    Classification =
                      if name = "initialize" || name = "shutdown" then Control
                      elif name = "workspace/commands/execute" then Mutation
                      else Read }
              for name in
                  [ "workspace/delta"
                    "workspace/reset"
                    "workspace/export/chunk"
                    "workspace/operations/progress"
                    "workspace/operations/output"
                    "workspace/operations/completed" ] do
                  { Name = name
                    Classification = NotificationMethod } ]
