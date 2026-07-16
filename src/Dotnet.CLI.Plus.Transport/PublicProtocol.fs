namespace Dotnet.CLI.Plus.Transport

open System
open System.Collections.Generic
open System.Collections.Immutable
open Dotnet.CLI.Plus.Core

type PublicInitializeRequest =
    { ProtocolMinor: int
      ClientName: string
      Capabilities: ImmutableArray<string>
      MaximumFrameBytes: int
      MaximumPageSize: int }

[<RequireQualifiedAccess>]
module PublicProtocol =
    let private map values = RpcValue.map values
    let private text value = RpcValue.String value
    let private integer value = RpcValue.Integer value
    let private boolean value = RpcValue.Boolean value

    let private supportedCapabilities =
        ImmutableHashSet.CreateRange<string>(
            StringComparer.Ordinal,
            [ "workspace.root"
              "workspace.export"
              "workspace.refresh"
              "operation.cancel" ]
        )

    let private format =
        function
        | WorkspaceFormat.Sln -> "sln"
        | WorkspaceFormat.Slnx -> "slnx"
        | WorkspaceFormat.Slnf -> "slnf"
        | _ -> "unknown"

    let private nodeKind =
        function
        | WorkspaceNodeKind.Workspace -> "workspace"
        | WorkspaceNodeKind.SolutionFolder -> "solutionFolder"
        | WorkspaceNodeKind.Project -> "project"
        | WorkspaceNodeKind.ProjectItem -> "projectItem"
        | WorkspaceNodeKind.SolutionItem -> "solutionItem"
        | WorkspaceNodeKind.Configuration -> "configuration"
        | WorkspaceNodeKind.Platform -> "platform"
        | WorkspaceNodeKind.Placeholder -> "placeholder"
        | _ -> "unknown"

    let private loadState =
        function
        | WorkspaceNodeLoadState.Hydrated -> "hydrated"
        | WorkspaceNodeLoadState.Unhydrated -> "unhydrated"
        | WorkspaceNodeLoadState.FilteredOut -> "filteredOut"
        | _ -> "unknown"

    let private severity =
        function
        | WorkspaceDiagnosticSeverity.Information -> "information"
        | WorkspaceDiagnosticSeverity.Warning -> "warning"
        | WorkspaceDiagnosticSeverity.Error -> "error"
        | _ -> "error"

    let node (workspaceId: WorkspaceId) revision (value: WorkspaceNode) =
        map
            [ "workspaceId", text workspaceId.Value
              "revision", integer revision
              "id", text value.NodeId.Value
              "kind", text (nodeKind value.NodeKind)
              "name", text value.Name
              "loadState", text (loadState value.NodeLoadState)
              "capabilities",
              value.AvailableCapabilities
              |> Seq.map (fun capability -> text capability.Value)
              |> RpcValue.array ]

    let workspace (descriptor: WorkspaceDescriptor) revision =
        map
            [ "id", text descriptor.WorkspaceId.Value
              "path", text descriptor.Path.Value
              "format", text (format descriptor.WorkspaceFormat)
              "readOnly", boolean descriptor.IsReadOnly
              "revision", integer revision ]

    let diagnostic (workspaceId: WorkspaceId) revision (value: WorkspaceDiagnostic) =
        let fields =
            ResizeArray<string * RpcValue>(
                [ "workspaceId", text workspaceId.Value
                  "revision", integer revision
                  "severity", text (severity value.DiagnosticSeverity)
                  "code", text value.DiagnosticCode.Value
                  "message", text value.Message
                  "retryable", boolean value.Retryable
                  "correlationId", text (value.DiagnosticCorrelationId.ToString()) ]
            )

        value.DiagnosticArtifactPath
        |> Option.iter (fun path -> fields.Add("path", text path.Value))

        value.DiagnosticLocation
        |> Option.iter (fun location ->
            fields.Add(
                "location",
                map
                    [ "line", integer (int64 location.Line)
                      "column", integer (int64 location.Column) ]
            ))

        map fields

    let simpleDiagnostic (workspaceId: WorkspaceId) revision code message =
        map
            [ "workspaceId", text workspaceId.Value
              "revision", integer revision
              "severity", text "error"
              "code", text code
              "message", text message
              "retryable", boolean false ]

    let failureError (failure: WorkspaceFailure) =
        { Code = failure.Code.Value
          Message = failure.Diagnostic.Message
          Data = None }

    let private invalid message =
        Error
            { Code = "invalid_params"
              Message = message
              Data = None }

    let private positiveInt minimum name value =
        let parsed = RpcValue.requireInteger name value

        if parsed < int64 minimum || parsed > int64 Int32.MaxValue then
            invalidArg name $"Expected an integer between {minimum} and Int32.MaxValue."

        int parsed

    let parseInitialize parameters =
        try
            let fields = RpcValue.requireMap "initialize.params" parameters
            RpcValue.ensureOnly "initialize.params" [ "protocolVersion"; "clientInfo"; "capabilities"; "limits" ] fields

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

            let clientName =
                clientInfo
                |> RpcValue.requireField "name"
                |> RpcValue.requireString "clientInfo.name"

            if String.IsNullOrWhiteSpace clientName then
                invalidArg "clientInfo.name" "Client name must be a non-empty string."

            let capabilities =
                fields
                |> RpcValue.requireField "capabilities"
                |> RpcValue.requireArray "capabilities"

            let capabilityNames = ImmutableArray.CreateBuilder<string>(capabilities.Length)
            let unique = HashSet<string>(StringComparer.Ordinal)

            for capability in capabilities do
                let name = RpcValue.requireString "capabilities" capability

                if String.IsNullOrWhiteSpace name then
                    invalidArg "capabilities" "Capability names must be non-empty strings."

                if not (unique.Add name) then
                    invalidArg "capabilities" "Capability names must be unique."

                capabilityNames.Add name

            let maximumFrameBytes, maximumPageSize =
                match RpcValue.optionalField "limits" fields with
                | None -> RpcCodec.secureLimits.MaximumValueBytes, 1000
                | Some value ->
                    let limits = RpcValue.requireMap "limits" value
                    RpcValue.ensureOnly "limits" [ "maxFrameBytes"; "maxPageSize" ] limits

                    let frame =
                        match RpcValue.optionalField "maxFrameBytes" limits with
                        | Some requested -> positiveInt 1024 "limits.maxFrameBytes" requested
                        | None -> RpcCodec.secureLimits.MaximumValueBytes

                    let page =
                        match RpcValue.optionalField "maxPageSize" limits with
                        | Some requested -> positiveInt 1 "limits.maxPageSize" requested
                        | None -> 1000

                    min frame RpcCodec.secureLimits.MaximumValueBytes, min page 1000

            Ok
                { ProtocolMinor = min (int minor) 0
                  ClientName = clientName
                  Capabilities = capabilityNames.MoveToImmutable()
                  MaximumFrameBytes = maximumFrameBytes
                  MaximumPageSize = maximumPageSize }
        with
        | :? ArgumentException as error -> invalid error.Message
        | _ -> invalid "Initialize parameters are invalid."

    let initializeResult (descriptor: WorkspaceDescriptor) revision request =
        let negotiatedCapabilities =
            request.Capabilities
            |> Seq.filter supportedCapabilities.Contains
            |> Seq.distinct
            |> Seq.sort
            |> Seq.map text
            |> RpcValue.array

        map
            [ "protocolVersion", map [ "major", integer 1L; "minor", integer 0L ]
              "serverInfo", map [ "name", text "dotnet-cli-plus"; "version", text "1" ]
              "workspace", workspace descriptor revision
              "capabilities", negotiatedCapabilities
              "limits",
              map
                  [ "maxFrameBytes", integer (int64 request.MaximumFrameBytes)
                    "maxPageSize", integer (int64 request.MaximumPageSize) ] ]
