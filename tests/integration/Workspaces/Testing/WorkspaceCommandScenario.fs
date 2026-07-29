namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit

module internal WorkspaceCommandScenario =
    type Session =
        { Directory: string
          Solution: string
          Child: Process
          WorkspaceId: string
          ProjectId: string option
          FolderId: string option }

    type Completion =
        { Outcome: string
          Revision: int64
          Notifications: string list
          Output: string list
          WorkspaceNotifications: string list }

    let private initialize maximumFrameBytes =
        WorkspaceRpcScenario.map
            [ "protocolVersion",
              WorkspaceRpcScenario.map
                  [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
              "clientInfo", WorkspaceRpcScenario.map [ "name", RpcValue.String "canonical-test" ]
              "capabilities",
              RpcValue.array
                  [ RpcValue.String "workspace.root"
                    RpcValue.String "workspace.delta"
                    RpcValue.String "workspace.commands.preview"
                    RpcValue.String "workspace.commands.execute" ]
              "limits",
              WorkspaceRpcScenario.map
                  [ "maxFrameBytes", RpcValue.Integer maximumFrameBytes
                    "maxPageSize", RpcValue.Integer 100L ] ]

    let private nodeId kind nodes =
        nodes
        |> Seq.tryFind (fun node -> WorkspaceRpcScenario.field "kind" node = RpcValue.String kind)
        |> Option.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "id")

    let startWithFrameBytes name maximumFrameBytes environment setup =
        let directory = DirectCommandProcess.temporaryDirectory name
        let solution = Path.Combine(directory, "Demo.slnx")
        let model = SolutionModel()
        setup directory model
        WorkspaceRpcScenario.save solution model

        let fakeHost = DirectCommandProcess.copyScriptedDotnet directory

        let child =
            WorkspaceRpcScenario.startPipeWithEnvironment
                "solution"
                solution
                [ "DOTNET_HOST_PATH", fakeHost
                  "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", "workspace-command"
                  yield! environment ]

        WorkspaceRpcScenario.send
            child
            false
            (WorkspaceRpcScenario.request 1u "initialize" (initialize maximumFrameBytes))

        let initializeError, initialized =
            WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

        initializeError |> should equal None

        let workspaceId =
            WorkspaceRpcScenario.field "workspace" initialized
            |> WorkspaceRpcScenario.field "id"
            |> RpcValue.requireString "id"

        WorkspaceRpcScenario.send
            child
            false
            (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

        let rootError, root =
            WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

        rootError |> should equal None
        let nodes = WorkspaceRpcScenario.field "nodes" root |> RpcValue.requireArray "nodes"

        { Directory = directory
          Solution = solution
          Child = child
          WorkspaceId = workspaceId
          ProjectId = nodeId "project" nodes
          FolderId = nodeId "solutionFolder" nodes }

    let startWithEnvironment name environment setup =
        startWithFrameBytes name 4194304L environment setup

    let start name setup = startWithEnvironment name [] setup

    let stop session =
        try
            WorkspaceRpcScenario.shutdown session.Child 99u
        finally
            WorkspaceRpcScenario.disposeProcess session.Child
            DirectCommandProcess.delete session.Directory

    let argumentMap values = WorkspaceRpcScenario.map values

    let private common commandId target arguments expectedRevision =
        let targetField =
            target
            |> Option.map (fun value -> [ "targetNodeId", RpcValue.String value ])
            |> Option.defaultValue []

        [ "commandId", RpcValue.String commandId
          "arguments", arguments
          "expectedRevision", RpcValue.Integer expectedRevision ]
        @ targetField

    let private startOperation session requestId fields =
        WorkspaceRpcScenario.send
            session.Child
            false
            (WorkspaceRpcScenario.request
                requestId
                "workspace/commands/execute"
                (WorkspaceRpcScenario.map fields))

        let executeError, result =
            WorkspaceRpcScenario.readFrame session.Child
            |> WorkspaceRpcScenario.response requestId

        match executeError with
        | None -> ()
        | Some error ->
            failwithf "Workspace command execution failed: %s: %s" error.Code error.Message

        WorkspaceRpcScenario.field "operationId" result
        |> RpcValue.requireString "operationId"

    let beginMutation session requestId commandId target arguments expectedRevision =
        let fields = common commandId target arguments expectedRevision

        WorkspaceRpcScenario.send
            session.Child
            false
            (WorkspaceRpcScenario.request
                requestId
                "workspace/commands/preview"
                (WorkspaceRpcScenario.map fields))

        let previewError, preview =
            WorkspaceRpcScenario.readFrame session.Child
            |> WorkspaceRpcScenario.response requestId

        match previewError with
        | None -> ()
        | Some error ->
            failwithf "Workspace command preview failed: %s: %s" error.Code error.Message

        let confirmationToken = WorkspaceRpcScenario.field "confirmationToken" preview
        startOperation session (requestId + 1u) (("confirmationToken", confirmationToken) :: fields)

    let complete session operationId =
        let mutable completed = None
        let mutable nextSequence = 0L
        let notifications = ResizeArray<string>()
        let output = ResizeArray<string>()
        let workspaceNotifications = ResizeArray<string>()

        while completed.IsNone do
            match WorkspaceRpcScenario.readFrame session.Child with
            | Notification(name, parameters) when
                name.StartsWith("workspace/operations/", StringComparison.Ordinal)
                ->
                WorkspaceRpcScenario.field "operationId" parameters
                |> RpcValue.requireString "operationId"
                |> should equal operationId

                WorkspaceRpcScenario.field "sequence" parameters
                |> RpcValue.requireInteger "sequence"
                |> should equal nextSequence

                nextSequence <- nextSequence + 1L
                notifications.Add name

                match name with
                | "workspace/operations/output" ->
                    output.Add(
                        WorkspaceRpcScenario.field "text" parameters
                        |> RpcValue.requireString "text"
                    )
                | "workspace/operations/completed" ->
                    completed <-
                        Some
                            { Outcome =
                                WorkspaceRpcScenario.field "outcome" parameters
                                |> RpcValue.requireString "outcome"
                              Revision =
                                WorkspaceRpcScenario.field "revision" parameters
                                |> RpcValue.requireInteger "revision"
                              Notifications = notifications |> Seq.toList
                              Output = output |> Seq.toList
                              WorkspaceNotifications = workspaceNotifications |> Seq.toList }
                | _ -> ()
            | Notification(name, _) when name = "workspace/delta" || name = "workspace/reset" ->
                workspaceNotifications.Add name
            | frame -> failwithf "Unexpected workspace command operation frame: %A" frame

        completed.Value

    let execute session requestId commandId target arguments expectedRevision =
        beginMutation session requestId commandId target arguments expectedRevision
        |> complete session

    let executeRead session requestId commandId target arguments expectedRevision =
        common commandId target arguments expectedRevision
        |> startOperation session requestId
        |> complete session

    let captured path =
        File.ReadAllLines path
        |> Array.map (fun line -> JsonSerializer.Deserialize<string array> line)

    let openSolution path =
        SolutionSerializers
            .GetSerializerByMoniker(path)
            .OpenAsync(path, CancellationToken.None)
            .GetAwaiter()
            .GetResult()
