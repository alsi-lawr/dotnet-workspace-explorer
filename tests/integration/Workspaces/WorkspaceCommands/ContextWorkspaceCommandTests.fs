namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open Xunit

[<Collection("Workspace scenarios")>]
type ContextWorkspaceCommandTests() =
    let nodeId kind result =
        WorkspaceRpcScenario.field "nodes" result
        |> RpcValue.requireArray "nodes"
        |> Seq.find (fun node -> WorkspaceRpcScenario.field "kind" node = RpcValue.String kind)
        |> WorkspaceRpcScenario.field "id"
        |> RpcValue.requireString "id"

    let initialize =
        WorkspaceRpcScenario.map
            [ "protocolVersion",
              WorkspaceRpcScenario.map
                  [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
              "clientInfo", WorkspaceRpcScenario.map [ "name", RpcValue.String "context-test" ]
              "capabilities",
              RpcValue.array
                  [ RpcValue.String "workspace.root"
                    RpcValue.String "workspace.children"
                    RpcValue.String "workspace.create.options"
                    RpcValue.String "workspace.commands.list"
                    RpcValue.String "workspace.commands.describe"
                    RpcValue.String "workspace.commands.preview"
                    RpcValue.String "workspace.commands.execute"
                    RpcValue.String "workspace.delta" ]
              "limits",
              WorkspaceRpcScenario.map
                  [ "maxFrameBytes", RpcValue.Integer 65536L
                    "maxPageSize", RpcValue.Integer 100L ] ]

    let operationCompletion child operationId =
        let mutable completion = None
        let output = ResizeArray<string>()

        while completion.IsNone do
            match WorkspaceRpcScenario.readFrame child with
            | Notification(name, parameters) when
                name.StartsWith("workspace/operations/", StringComparison.Ordinal)
                ->
                Assert.Equal(
                    operationId,
                    WorkspaceRpcScenario.field "operationId" parameters
                    |> RpcValue.requireString "operationId"
                )

                if name = "workspace/operations/output" then
                    output.Add(
                        WorkspaceRpcScenario.field "text" parameters
                        |> RpcValue.requireString "text"
                    )
                elif name = "workspace/operations/completed" then
                    let diagnostics =
                        WorkspaceRpcScenario.field "diagnostics" parameters
                        |> RpcValue.requireArray "diagnostics"

                    let diagnostic =
                        diagnostics
                        |> Seq.tryHead
                        |> Option.map (fun value ->
                            let code =
                                WorkspaceRpcScenario.field "code" value
                                |> RpcValue.requireString "code"

                            let message =
                                WorkspaceRpcScenario.field "message" value
                                |> RpcValue.requireString "message"

                            $"{code}: {message}")

                    completion <-
                        Some(
                            WorkspaceRpcScenario.field "outcome" parameters
                            |> RpcValue.requireString "outcome",
                            WorkspaceRpcScenario.field "revision" parameters
                            |> RpcValue.requireInteger "revision",
                            diagnostic,
                            String.concat String.Empty output
                        )
            | Notification("workspace/delta", _)
            | Notification("workspace/reset", _) -> ()
            | frame -> failwithf "Expected operation notification, got %A" frame

        completion.Value

    let restore project =
        let start = ProcessStartInfo("dotnet")
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.ArgumentList.Add "restore"
        start.ArgumentList.Add project
        use child = Process.Start start
        Assert.NotNull child
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        Assert.True(child.WaitForExit 30000, "The project restore did not finish.")
        Assert.True(child.ExitCode = 0, $"{output.Result}\n{error.Result}")

    [<Fact>]
    member _.``should create and delete from a projected file context through generic commands``() =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "context-workspace-command-scenario"

        let solution = Path.Combine(directory, "Demo.slnx")
        let project = Path.Combine(directory, "Demo.csproj")
        let existing = Path.Combine(directory, "Existing.cs")
        let created = Path.Combine(directory, "Created.cs")
        let dataHome = Path.Combine(directory, "data")
        let model = SolutionModel()
        model.AddProject("Demo.csproj", "Demo", null) |> ignore
        WorkspaceRpcScenario.save solution model
        Directory.CreateDirectory dataHome |> ignore

        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework>"
            + "</PropertyGroup></Project>"
        )

        File.WriteAllText(existing, "internal sealed class Existing { }")
        restore project

        use child =
            WorkspaceRpcScenario.startPipeWithDataHome "solution" solution (Some dataHome)

        try
            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 1u "initialize" initialize)

            let initializeError, initialized =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

            Assert.True initializeError.IsNone

            let negotiated =
                WorkspaceRpcScenario.field "capabilities" initialized
                |> RpcValue.requireArray "capabilities"

            Assert.Contains(RpcValue.String "workspace.create.options", negotiated)

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

            let _, root =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

            let rootId = nodeId "workspace" root

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    3u
                    "workspace/children"
                    (WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String rootId
                          "pageSize", RpcValue.Integer 100L ]))

            let _, rootChildren =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 3u

            let projectId = nodeId "project" rootChildren

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    4u
                    "workspace/children"
                    (WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String projectId
                          "pageSize", RpcValue.Integer 100L ]))

            let (childrenError, projectChildren), _, _ =
                WorkspaceRpcScenario.responseAfterWorkspaceNotifications child 4u 0L

            Assert.True childrenError.IsNone

            let revision =
                WorkspaceRpcScenario.field "revision" projectChildren
                |> RpcValue.requireInteger "revision"

            let fileId = nodeId "projectFile" projectChildren

            match WorkspaceRpcScenario.readFrame child with
            | Notification("workspace/delta", _) -> ()
            | frame -> failwithf "Expected hydration delta, got %A" frame

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    5u
                    "workspace/commands/list"
                    (WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String fileId ]))

            let listError, listed =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 5u

            Assert.True listError.IsNone

            let commandIds =
                WorkspaceRpcScenario.field "commands" listed
                |> RpcValue.requireArray "commands"
                |> Seq.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "command.id")
                |> Seq.toArray

            Assert.Contains("workspace.create", commandIds)
            Assert.Contains("workspace.delete", commandIds)

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    6u
                    "workspace/create/options"
                    (WorkspaceRpcScenario.map
                        [ "targetNodeId", RpcValue.String fileId
                          "expectedRevision", RpcValue.Integer revision ]))

            let optionsError, optionsResult =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 6u

            match optionsError with
            | Some error -> failwithf "Create options failed: %s: %s" error.Code error.Message
            | None -> ()

            Assert.Equal(
                revision,
                WorkspaceRpcScenario.field "revision" optionsResult
                |> RpcValue.requireInteger "revision"
            )

            let empty =
                let options =
                    WorkspaceRpcScenario.field "options" optionsResult
                    |> RpcValue.requireArray "options"

                Assert.Contains(
                    options,
                    fun option ->
                        WorkspaceRpcScenario.field "kind" option = RpcValue.String "projectTemplate"
                )

                Assert.DoesNotContain(
                    options,
                    fun option ->
                        WorkspaceRpcScenario.field "kind" option = RpcValue.String "itemTemplate"
                        && (RpcValue.tryField "language" option
                            |> Option.exists ((<>) (RpcValue.String "C#")))
                )

                options
                |> Seq.find (fun option ->
                    WorkspaceRpcScenario.field "kind" option = RpcValue.String "empty")

            Assert.Equal(
                RpcValue.String "transaction",
                WorkspaceRpcScenario.field "execution" empty
            )

            let createArguments =
                WorkspaceRpcScenario.map
                    [ "selectionId", WorkspaceRpcScenario.field "selectionId" empty
                      "name", RpcValue.String "Created.cs" ]

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    7u
                    "workspace/commands/preview"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String fileId
                          "arguments", createArguments
                          "expectedRevision", RpcValue.Integer revision ]))

            let previewError, preview =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 7u

            Assert.True previewError.IsNone
            Assert.NotEqual(RpcValue.Nil, WorkspaceRpcScenario.field "summary" preview)

            let effects =
                WorkspaceRpcScenario.field "effects" preview |> RpcValue.requireArray "effects"

            Assert.Equal(2, effects.Length)

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    8u
                    "workspace/commands/execute"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String fileId
                          "arguments", createArguments
                          "expectedRevision", RpcValue.Integer revision
                          "confirmationToken",
                          WorkspaceRpcScenario.field "confirmationToken" preview ]))

            let executeError, executeResult =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 8u

            Assert.True executeError.IsNone
            Assert.Equal(RpcValue.Boolean true, WorkspaceRpcScenario.field "applied" executeResult)
            Assert.True(File.Exists created)

            let nextRevision =
                WorkspaceRpcScenario.field "revision" executeResult
                |> RpcValue.requireInteger "revision"

            match WorkspaceRpcScenario.readFrame child with
            | Notification("workspace/delta", _) -> ()
            | frame -> failwithf "Expected creation delta, got %A" frame

            let itemTemplate =
                WorkspaceRpcScenario.field "options" optionsResult
                |> RpcValue.requireArray "options"
                |> Seq.find (fun option ->
                    let kind = WorkspaceRpcScenario.field "kind" option
                    let displayName = WorkspaceRpcScenario.field "displayName" option
                    let language = RpcValue.tryField "language" option

                    kind = RpcValue.String "itemTemplate"
                    && displayName = RpcValue.String "Interface"
                    && language = Some(RpcValue.String "C#"))

            let itemArguments =
                WorkspaceRpcScenario.map
                    [ "selectionId", WorkspaceRpcScenario.field "selectionId" itemTemplate
                      "name", RpcValue.String "IGenerated" ]

            let itemRequest =
                WorkspaceRpcScenario.map
                    [ "commandId", RpcValue.String "workspace.create"
                      "targetNodeId", RpcValue.String fileId
                      "arguments", itemArguments
                      "expectedRevision", RpcValue.Integer nextRevision ]

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 9u "workspace/commands/preview" itemRequest)

            let itemPreviewError, itemPreview =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 9u

            match itemPreviewError with
            | Some error -> failwithf "Item preview failed: %s: %s" error.Code error.Message
            | None -> ()

            let itemExecute =
                match itemRequest with
                | RpcValue.Map fields ->
                    fields.Add(
                        "confirmationToken",
                        WorkspaceRpcScenario.field "confirmationToken" itemPreview
                    )
                    |> RpcValue.Map
                | _ -> failwith "The item request must be a map."

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 10u "workspace/commands/execute" itemExecute)

            let itemExecuteError, itemExecuteResult =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 10u

            match itemExecuteError with
            | Some error -> failwithf "Item execute failed: %s: %s" error.Code error.Message
            | None -> ()

            let itemOperationId =
                WorkspaceRpcScenario.field "operationId" itemExecuteResult
                |> RpcValue.requireString "operationId"

            let itemOutcome, itemRevision, itemDiagnostic, itemOutput =
                operationCompletion child itemOperationId

            Assert.True(
                (itemOutcome = "succeeded"),
                itemDiagnostic
                |> Option.defaultValue "Item template operation failed."
                |> fun message -> $"{message}\n{itemOutput}"
            )

            Assert.True(File.Exists(Path.Combine(directory, "IGenerated.cs")))

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    11u
                    "workspace/create/options"
                    (WorkspaceRpcScenario.map
                        [ "targetNodeId", RpcValue.String rootId
                          "expectedRevision", RpcValue.Integer itemRevision ]))

            let rootOptionsError, rootOptionsResult =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 11u

            match rootOptionsError with
            | Some error -> failwithf "Root create options failed: %s: %s" error.Code error.Message
            | None -> ()

            let projectTemplate =
                WorkspaceRpcScenario.field "options" rootOptionsResult
                |> RpcValue.requireArray "options"
                |> Seq.find (fun option ->
                    let kind = WorkspaceRpcScenario.field "kind" option
                    let displayName = WorkspaceRpcScenario.field "displayName" option
                    let language = WorkspaceRpcScenario.field "language" option

                    kind = RpcValue.String "projectTemplate"
                    && displayName = RpcValue.String "Class Library"
                    && language = RpcValue.String "C#")

            let projectArguments =
                WorkspaceRpcScenario.map
                    [ "selectionId", WorkspaceRpcScenario.field "selectionId" projectTemplate
                      "name", RpcValue.String "Generated" ]

            let projectRequest =
                WorkspaceRpcScenario.map
                    [ "commandId", RpcValue.String "workspace.create"
                      "targetNodeId", RpcValue.String rootId
                      "arguments", projectArguments
                      "expectedRevision", RpcValue.Integer itemRevision ]

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 12u "workspace/commands/preview" projectRequest)

            let projectPreviewError, projectPreview =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 12u

            match projectPreviewError with
            | Some error -> failwithf "Project preview failed: %s: %s" error.Code error.Message
            | None -> ()

            let projectExecute =
                match projectRequest with
                | RpcValue.Map fields ->
                    fields.Add(
                        "confirmationToken",
                        WorkspaceRpcScenario.field "confirmationToken" projectPreview
                    )
                    |> RpcValue.Map
                | _ -> failwith "The project request must be a map."

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 13u "workspace/commands/execute" projectExecute)

            let projectExecuteError, projectExecuteResult =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 13u

            match projectExecuteError with
            | Some error -> failwithf "Project execute failed: %s: %s" error.Code error.Message
            | None -> ()

            let operationId =
                WorkspaceRpcScenario.field "operationId" projectExecuteResult
                |> RpcValue.requireString "operationId"

            let outcome, projectRevision, diagnostic, output =
                operationCompletion child operationId

            Assert.True(
                (outcome = "succeeded"),
                diagnostic
                |> Option.defaultValue "Project template operation failed."
                |> fun message -> $"{message}\n{output}"
            )

            Assert.True(File.Exists(Path.Combine(directory, "Generated", "Generated.csproj")))

            let reopened = WorkspaceCommandScenario.openSolution solution
            Assert.Equal(2, reopened.SolutionProjects.Count)

            WorkspaceRpcScenario.previewAndExecute
                child
                20u
                "workspace.delete"
                fileId
                RpcValue.emptyMap
                projectRevision
                true

            Assert.False(File.Exists existing)
            Assert.True(File.Exists created)
            WorkspaceRpcScenario.shutdown child 30u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)
