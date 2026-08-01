namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open Microsoft.VisualStudio.SolutionPersistence.Model

module private WorkspaceAddExistingScenario =
    let initialize pageSize addExisting =
        WorkspaceRpcScenario.map
            [ "protocolVersion",
              WorkspaceRpcScenario.map
                  [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
              "clientInfo", WorkspaceRpcScenario.map [ "name", RpcValue.String "add-existing-test" ]
              "capabilities",
              seq {
                  yield RpcValue.String "workspace.root"
                  yield RpcValue.String "workspace.children"
                  yield RpcValue.String "workspace.create.options"
                  yield RpcValue.String "workspace.commands.describe"
                  yield RpcValue.String "workspace.commands.preview"
                  yield RpcValue.String "workspace.commands.execute"

                  if addExisting then
                      yield RpcValue.String "workspace.addExisting.selector"
              }
              |> RpcValue.array
              "limits",
              WorkspaceRpcScenario.map
                  [ "maxFrameBytes", RpcValue.Integer 1048576L
                    "maxPageSize", RpcValue.Integer(int64 pageSize) ] ]

    let call child requestId methodName parameters =
        WorkspaceRpcScenario.send
            child
            false
            (WorkspaceRpcScenario.request requestId methodName parameters)

        WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response requestId

    let successful child requestId methodName parameters =
        match call child requestId methodName parameters with
        | None, result -> result
        | Some error, _ -> failwithf "%s failed: %s: %s" methodName error.Code error.Message

    let root child =
        successful child 2u "workspace/root" RpcValue.emptyMap
        |> WorkspaceRpcScenario.field "nodes"
        |> RpcValue.requireArray "nodes"
        |> Seq.exactlyOne

    let revision node =
        WorkspaceRpcScenario.field "revision" node |> RpcValue.requireInteger "revision"

    let nodeId node =
        WorkspaceRpcScenario.field "id" node |> RpcValue.requireString "id"

    let entryId entry =
        WorkspaceRpcScenario.field "entryId" entry |> RpcValue.requireString "entryId"

    let option kind options =
        options
        |> WorkspaceRpcScenario.field "options"
        |> RpcValue.requireArray "options"
        |> Seq.find (fun candidate ->
            WorkspaceRpcScenario.field "kind" candidate = RpcValue.String kind)

    let execute child requestId request =
        let expectedRevision =
            WorkspaceRpcScenario.field "expectedRevision" request
            |> RpcValue.requireInteger "expectedRevision"

        WorkspaceRpcScenario.send
            child
            false
            (WorkspaceRpcScenario.request requestId "workspace/commands/execute" request)

        let (error, result), _, _ =
            WorkspaceRpcScenario.responseAfterWorkspaceNotifications
                child
                requestId
                expectedRevision

        match error with
        | Some value -> failwithf "workspace/commands/execute failed: %s" value.Message
        | None -> result

    let children child requestId parentNodeId =
        successful
            child
            requestId
            "workspace/children"
            (WorkspaceRpcScenario.map
                [ "parentNodeId", RpcValue.String parentNodeId
                  "pageSize", RpcValue.Integer 100L ])

    let allEntries child firstRequestId selectorId parentEntryId firstPage =
        let entries = ResizeArray<RpcValue>()

        WorkspaceRpcScenario.field "entries" firstPage
        |> RpcValue.requireArray "entries"
        |> entries.AddRange

        let mutable token =
            RpcValue.optionalField "nextToken" (WorkspaceRpcScenario.fields firstPage)
            |> Option.map (RpcValue.requireString "nextToken")

        let mutable requestId = firstRequestId

        while token.IsSome do
            let page =
                successful
                    child
                    requestId
                    "workspace/addExisting/children"
                    (WorkspaceRpcScenario.map
                        [ "selectorId", RpcValue.String selectorId
                          "parentEntryId", RpcValue.String parentEntryId
                          "pageSize", RpcValue.Integer 4096L
                          "continuationToken", RpcValue.String token.Value ])

            WorkspaceRpcScenario.field "entries" page
            |> RpcValue.requireArray "entries"
            |> entries.AddRange

            token <-
                RpcValue.optionalField "nextToken" (WorkspaceRpcScenario.fields page)
                |> Option.map (RpcValue.requireString "nextToken")

            requestId <- requestId + 1u

        entries.ToArray()

    let createOptions child requestId targetNodeId revision =
        successful
            child
            requestId
            "workspace/create/options"
            (WorkspaceRpcScenario.map
                [ "targetNodeId", RpcValue.String targetNodeId
                  "expectedRevision", RpcValue.Integer revision ])

    let startSelectorWithPageSize child requestId targetNodeId revision pageSize =
        let selection =
            createOptions child requestId targetNodeId revision |> option "addExisting"

        successful
            child
            (requestId + 1u)
            "workspace/addExisting/start"
            (WorkspaceRpcScenario.map
                [ "targetNodeId", RpcValue.String targetNodeId
                  "selectionId", WorkspaceRpcScenario.field "selectionId" selection
                  "expectedRevision", RpcValue.Integer revision
                  "pageSize", RpcValue.Integer(int64 pageSize) ])

    let startSelector child requestId targetNodeId revision =
        startSelectorWithPageSize child requestId targetNodeId revision 4096

    let previewRequest targetNodeId revision selectorId entryIds =
        let arguments =
            WorkspaceRpcScenario.map
                [ "selectorId", RpcValue.String selectorId
                  "entryIds", entryIds |> Seq.map RpcValue.String |> RpcValue.array ]

        WorkspaceRpcScenario.map
            [ "commandId", RpcValue.String "workspace.addExisting"
              "targetNodeId", RpcValue.String targetNodeId
              "arguments", arguments
              "expectedRevision", RpcValue.Integer revision ]

    let createSolutionFolder child requestId targetNodeId revision selectionId name =
        let request =
            WorkspaceRpcScenario.map
                [ "commandId", RpcValue.String "workspace.create"
                  "targetNodeId", RpcValue.String targetNodeId
                  "arguments",
                  WorkspaceRpcScenario.map
                      [ "selectionId", selectionId; "name", RpcValue.String name ]
                  "expectedRevision", RpcValue.Integer revision ]

        let preview = successful child requestId "workspace/commands/preview" request

        let executeRequest =
            match request with
            | RpcValue.Map fields ->
                fields.Add(
                    "confirmationToken",
                    WorkspaceRpcScenario.field "confirmationToken" preview
                )
                |> RpcValue.Map
            | _ -> failwith "The create request must be a map."

        let result = execute child (requestId + 1u) executeRequest

        match WorkspaceRpcScenario.readFrame child with
        | Notification("workspace/delta", _)
        | Notification("workspace/reset", _) -> ()
        | frame -> failwithf "Expected a Solution Folder mutation notification, got %A" frame

        WorkspaceRpcScenario.field "revision" result
        |> RpcValue.requireInteger "revision"

    let withPreparedWorkspaceCapability
        alias
        extension
        setup
        addExisting
        (action: string -> string -> Process -> unit)
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory alias
        let solution = Path.Combine(directory, "Demo" + extension)
        let cliHome = Path.Combine(directory, "cli-home")

        try
            let model = SolutionModel()
            setup directory model
            WorkspaceRpcScenario.save solution model

            WorkspaceRpcScenario.writeTemplateCatalog solution cliHome """{ "TemplateInfo": [] }"""

            let started =
                WorkspaceRpcScenario.startPipeWithEnvironment
                    alias
                    solution
                    [ "DOTNET_CLI_HOME", cliHome ]

            match started |> Option.ofObj with
            | None -> failwith "The workspace process did not start."
            | Some child ->
                use child = child

                try
                    successful child 1u "initialize" (initialize 2 addExisting) |> ignore
                    action directory solution child
                    WorkspaceRpcScenario.shutdown child 99u
                finally
                    WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    let withPreparedWorkspace alias extension setup action =
        withPreparedWorkspaceCapability alias extension setup true action

    let withWorkspace alias action =
        withPreparedWorkspace alias ".slnx" (fun _ _ -> ()) action
