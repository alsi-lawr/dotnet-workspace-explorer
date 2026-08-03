namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
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
                    RpcValue.String "workspace.operations.cancel"
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
                (WorkspaceRpcScenario.field "operationId" parameters
                 |> RpcValue.requireString "operationId")
                |> should equal (operationId)

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
        (child) |> should not' (be Null)
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        let outputText = output.GetAwaiter().GetResult()
        let errorText = error.GetAwaiter().GetResult()

        if child.ExitCode <> 0 then
            failwithf
                "dotnet restore failed with exit code %d.%sstdout:%s%s%sstderr:%s%s"
                child.ExitCode
                Environment.NewLine
                Environment.NewLine
                outputText
                Environment.NewLine
                Environment.NewLine
                errorText

    [<Fact>]
    member _.``contextual delete previews and executions report cascades while preserving surviving project files``
        ()
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory "context-delete-cascades"

        let solution = Path.Combine(directory, "Delete.slnx")
        let standaloneProject = Path.Combine(directory, "Standalone.csproj")
        let nestedProject = Path.Combine(directory, "Nested.csproj")
        let loose = Path.Combine(directory, "Loose.cs")
        let generated = Path.Combine(directory, "Generated")
        let generatedOne = Path.Combine(generated, "One.cs")
        let generatedTwo = Path.Combine(generated, "Two.cs")
        let nestedSource = Path.Combine(directory, "Nested.cs")
        let solutionItem = Path.Combine(directory, "Directory.Build.props")
        let trashHome = Path.Combine(directory, "data")
        let model = SolutionModel()
        let folder = model.AddFolder "/Group/"
        folder.AddFile(Path.GetFileName solutionItem)
        model.AddProject("Standalone.csproj", "Standalone", null) |> ignore
        model.AddProject("Nested.csproj", "Nested", folder) |> ignore
        Directory.CreateDirectory generated |> ignore
        Directory.CreateDirectory trashHome |> ignore

        File.WriteAllText(
            standaloneProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework>"
            + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
            + "</PropertyGroup><ItemGroup><Compile Include=\"Loose.cs\" />"
            + "<Compile Include=\"Generated/One.cs\" />"
            + "<Compile Include=\"Generated/Two.cs\" /></ItemGroup></Project>"
        )

        File.WriteAllText(
            nestedProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework>"
            + "</PropertyGroup></Project>"
        )

        File.WriteAllText(loose, "internal sealed class Loose { }")
        File.WriteAllText(generatedOne, "internal sealed class One { }")
        File.WriteAllText(generatedTwo, "internal sealed class Two { }")
        File.WriteAllText(nestedSource, "internal sealed class Nested { }")
        File.WriteAllText(solutionItem, "<Project />")
        WorkspaceRpcScenario.save solution model
        restore standaloneProject
        restore nestedProject

        use child =
            WorkspaceRpcScenario.startPipeWithEnvironment
                "solution"
                solution
                [ "XDG_DATA_HOME", trashHome ]

        let response id methodName parameters =
            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request id methodName parameters)

            let error, result =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response id

            match error with
            | Some value -> failwithf "%s failed: %s: %s" methodName value.Code value.Message
            | None -> result

        let children id parent =
            response
                id
                "workspace/children"
                (WorkspaceRpcScenario.map
                    [ "parentNodeId", RpcValue.String parent; "pageSize", RpcValue.Integer 100L ])

        let findNode kind name result =
            WorkspaceRpcScenario.field "nodes" result
            |> RpcValue.requireArray "nodes"
            |> Seq.find (fun value ->
                WorkspaceRpcScenario.field "kind" value = RpcValue.String kind
                && WorkspaceRpcScenario.field "name" value = RpcValue.String name)

        let nodeIdentifier node =
            WorkspaceRpcScenario.field "id" node |> RpcValue.requireString "id"

        let preview id target revision =
            response
                id
                "workspace/commands/preview"
                (WorkspaceRpcScenario.map
                    [ "commandId", RpcValue.String "workspace.delete"
                      "targetNodeId", RpcValue.String target
                      "arguments", RpcValue.emptyMap
                      "expectedRevision", RpcValue.Integer revision ])

        let operations previewResult : (string * bool) array =
            WorkspaceRpcScenario.field "effects" previewResult
            |> RpcValue.requireArray "effects"
            |> Seq.map (fun effect ->
                WorkspaceRpcScenario.field "operation" effect
                |> RpcValue.requireString "effect.operation",
                WorkspaceRpcScenario.field "recursive" effect
                |> function
                    | RpcValue.Boolean value -> value
                    | value -> failwithf "Expected recursive boolean, got %A" value)
            |> Seq.toArray

        let execute id target revision previewResult =
            let result =
                response
                    id
                    "workspace/commands/execute"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.delete"
                          "targetNodeId", RpcValue.String target
                          "arguments", RpcValue.emptyMap
                          "expectedRevision", RpcValue.Integer revision
                          "confirmationToken",
                          WorkspaceRpcScenario.field "confirmationToken" previewResult ])

            match WorkspaceRpcScenario.readFrame child with
            | Notification("workspace/delta", _)
            | Notification("workspace/reset", _) -> ()
            | frame -> failwithf "Expected delete workspace notification, got %A" frame

            WorkspaceRpcScenario.field "revision" result
            |> RpcValue.requireInteger "revision"

        try
            response 1u "initialize" initialize |> ignore
            let root = response 2u "workspace/root" RpcValue.emptyMap
            let rootId = nodeId "workspace" root
            let rootChildren = children 3u rootId

            let standaloneId = findNode "project" "Standalone" rootChildren |> nodeIdentifier

            let solutionFolder = findNode "solutionFolder" "Group" rootChildren

            let solutionFolderId = nodeIdentifier solutionFolder
            let folderChildren = children 4u solutionFolderId

            let solutionItemId =
                findNode "solutionItem" "Directory.Build.props" folderChildren |> nodeIdentifier

            let projectChildren =
                response
                    5u
                    "workspace/children"
                    (WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String standaloneId
                          "pageSize", RpcValue.Integer 100L ])

            let revision =
                WorkspaceRpcScenario.field "revision" projectChildren
                |> RpcValue.requireInteger "revision"

            match WorkspaceRpcScenario.readFrame child with
            | Notification("workspace/delta", _) -> ()
            | frame -> failwithf "Expected project hydration delta, got %A" frame

            let looseId = findNode "projectFile" "Loose.cs" projectChildren |> nodeIdentifier

            let generatedId =
                findNode "projectFolder" "Generated" projectChildren |> nodeIdentifier

            let filePreview = preview 10u looseId revision

            ((operations filePreview = [| "removeFromProject", false; "trash", false |]))
            |> should equal true

            let folderPreview = preview 11u generatedId revision
            let folderOperations = operations folderPreview

            (folderOperations
             |> Array.filter (fst >> (=) "removeFromProject")
             |> Array.length)
            |> should equal (2)

            (Array.contains ("trash", true) folderOperations) |> should equal true

            let projectPreview = preview 12u standaloneId revision

            ((operations projectPreview = [| "removeFromSolution", false |]))
            |> should equal true

            let itemPreview = preview 13u solutionItemId revision

            let expectedItemOperations = [| "removeFromSolution", false; "trash", false |]

            ((operations itemPreview = expectedItemOperations)) |> should equal true

            let logicalPreview = preview 14u solutionFolderId revision
            let logicalOperations = operations logicalPreview

            (Array.contains ("removeFromSolution", true) logicalOperations)
            |> should equal true

            (logicalOperations.Length >= 3) |> should equal true

            let mutable current = execute 20u looseId revision filePreview
            (File.Exists loose) |> should equal false

            let nextFolderPreview = preview 22u generatedId current
            current <- execute 23u generatedId current nextFolderPreview
            (Directory.Exists generated) |> should equal false

            let nextProjectPreview = preview 25u standaloneId current
            current <- execute 26u standaloneId current nextProjectPreview
            (File.Exists standaloneProject) |> should equal true

            let nextItemPreview = preview 28u solutionItemId current
            current <- execute 29u solutionItemId current nextItemPreview
            (File.Exists solutionItem) |> should equal false

            let nextLogicalPreview = preview 31u solutionFolderId current
            execute 32u solutionFolderId current nextLogicalPreview |> ignore
            (File.Exists nestedProject) |> should equal true
            (File.Exists nestedSource) |> should equal true

            let reopened = WorkspaceCommandScenario.openSolution solution
            (reopened.SolutionProjects) |> should be Empty
            (reopened.SolutionFolders) |> should be Empty
            WorkspaceRpcScenario.shutdown child 40u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``nested custom template outputs publish and child failures compensate workspace state across modes``
        ()
        =
        let runCase mode =
            let postactionExpected = mode = "postaction"
            let nestedProjectExpected = mode = "nested-project"
            let projectTemplateExpected = postactionExpected || nestedProjectExpected
            let failureExpected = mode = "failure"
            let cancellationExpected = mode = "cancel"

            let directory =
                WorkspaceRpcScenario.temporaryDirectory $"context-custom-item-{mode}"

            let solution = Path.Combine(directory, "Custom.slnx")
            let project = Path.Combine(directory, "Custom.csproj")
            let cliHome = Path.Combine(directory, "home")
            let sdkRoot = Path.Combine(directory, "sdk")
            let sdkPath = Path.Combine(sdkRoot, "test")
            let markerId = Guid.NewGuid().ToString "N"
            let started = Path.Combine(Path.GetTempPath(), $"{markerId}.started")
            let continuePath = Path.Combine(Path.GetTempPath(), $"{markerId}.continue")

            let cache =
                Path.Combine(cliHome, ".templateengine", "dotnetcli", "test", "templatecache.json")

            let model = SolutionModel()
            model.AddProject("Custom.csproj", "Custom", null) |> ignore
            WorkspaceRpcScenario.save solution model
            WorkspaceRpcScenario.writeProject project
            Directory.CreateDirectory sdkPath |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName cache) |> ignore

            File.WriteAllText(
                cache,
                """
                {
                  "TemplateInfo": [
                    {
                      "Identity": "custom.item",
                      "Name": "Custom contract",
                      "ShortNameList": ["collision"],
                      "Precedence": 200,
                      "Description": "Custom item",
                      "TagsCollection": { "language": "C#", "type": "item" }
                    },
                    {
                      "Identity": "custom.project",
                      "Name": "Custom project",
                      "ShortNameList": ["collision"],
                      "Precedence": 200,
                      "Description": "Custom project",
                      "TagsCollection": { "language": "C#", "type": "project" }
                    }
                  ]
                }
                """
            )

            let fakeHost = DirectCommandProcess.copyScriptedDotnet directory

            if cancellationExpected then
                File.WriteAllText(continuePath, "preflight")

            let environment =
                [ "DOTNET_HOST_PATH", fakeHost
                  "DOTNET_CLI_HOME", cliHome
                  "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", "workspace-command"
                  "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_SDK_ROOT", sdkRoot
                  if failureExpected then
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_FAIL_AFTER_EDIT", "true"
                  if postactionExpected then
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_TEMPLATE_POSTACTION", "true"
                  if nestedProjectExpected then
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_TEMPLATE_OUTPUTS",
                      Path.Combine("src", "Nested.csproj")
                  if cancellationExpected then
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH", started
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_CONTINUE_PATH", continuePath ]

            use child =
                WorkspaceRpcScenario.startPipeWithEnvironment "solution" solution environment

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" initialize)

                let initializeError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

                (initializeError.IsNone) |> should equal true

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

                let _, projectChildren =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 4u

                let revision =
                    WorkspaceRpcScenario.field "revision" projectChildren
                    |> RpcValue.requireInteger "revision"

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _) -> ()
                | frame -> failwithf "Expected hydration delta, got %A" frame

                let targetId = if projectTemplateExpected then rootId else projectId

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        5u
                        "workspace/create/options"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String targetId
                              "expectedRevision", RpcValue.Integer revision ]))

                let optionsError, options =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 5u

                (optionsError.IsNone) |> should equal true

                let selectionId =
                    WorkspaceRpcScenario.field "options" options
                    |> RpcValue.requireArray "options"
                    |> Seq.find (fun option ->
                        let kind = WorkspaceRpcScenario.field "kind" option
                        let name = WorkspaceRpcScenario.field "displayName" option

                        if projectTemplateExpected then
                            kind = RpcValue.String "projectTemplate"
                            && name = RpcValue.String "Custom project"
                        else
                            kind = RpcValue.String "itemTemplate"
                            && name = RpcValue.String "Custom contract")
                    |> WorkspaceRpcScenario.field "selectionId"

                let arguments =
                    WorkspaceRpcScenario.map
                        [ "selectionId", selectionId
                          "name",
                          RpcValue.String(
                              if projectTemplateExpected then "Generated" else "IContract"
                          ) ]

                let request =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String targetId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer revision ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 6u "workspace/commands/preview" request)

                let previewError, preview =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 6u

                match previewError with
                | Some error -> failwithf "Custom preview failed: %s: %s" error.Code error.Message
                | None -> ()

                if cancellationExpected then
                    File.Delete continuePath
                    File.Delete started

                let execute =
                    match request with
                    | RpcValue.Map fields ->
                        fields.Add(
                            "confirmationToken",
                            WorkspaceRpcScenario.field "confirmationToken" preview
                        )
                        |> RpcValue.Map
                    | _ -> failwith "The item request must be a map."

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 7u "workspace/commands/execute" execute)

                let executeError, executeResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 7u

                (executeError.IsNone) |> should equal true

                let operationId =
                    WorkspaceRpcScenario.field "operationId" executeResult
                    |> RpcValue.requireString "operationId"

                let outcome, diagnostic =
                    if cancellationExpected then
                        DirectCommandProcess.waitForFile started

                        WorkspaceRpcScenario.send
                            child
                            false
                            (WorkspaceRpcScenario.request
                                70u
                                "workspace/operations/cancel"
                                (WorkspaceRpcScenario.map
                                    [ "operationId", RpcValue.String operationId ]))

                        let mutable accepted = false
                        let mutable completed = None

                        while not accepted || completed.IsNone do
                            match WorkspaceRpcScenario.readFrame child with
                            | Response(70u, Ok result) ->
                                (WorkspaceRpcScenario.field "accepted" result)
                                |> should equal (RpcValue.Boolean true)

                                accepted <- true
                            | Notification("workspace/operations/progress", _) -> ()
                            | Notification("workspace/operations/completed", parameters) ->
                                completed <-
                                    Some(
                                        WorkspaceRpcScenario.field "outcome" parameters
                                        |> RpcValue.requireString "outcome"
                                    )
                            | frame ->
                                failwithf "Unexpected contextual cancellation frame: %A" frame

                        completed.Value, None
                    else
                        let outcome, _, diagnostic, _ = operationCompletion child operationId

                        outcome, diagnostic

                let destination = Path.Combine(directory, "Nested", "IContract.cs")

                if postactionExpected then
                    (outcome) |> should equal ("failed")
                    (diagnostic.Value) |> should haveSubstring ("template_output_changed")
                    (Directory.Exists(Path.Combine(directory, "Generated"))) |> should equal false

                    let reopened = WorkspaceCommandScenario.openSolution solution
                    (reopened.SolutionProjects.Count) |> should equal (1)
                elif nestedProjectExpected then
                    (outcome) |> should equal ("succeeded")

                    (File.Exists(Path.Combine(directory, "Generated", "src", "Nested.csproj")))
                    |> should equal true

                    let reopened = WorkspaceCommandScenario.openSolution solution
                    (reopened.SolutionProjects.Count) |> should equal (2)
                elif cancellationExpected then
                    (outcome) |> should equal ("cancelled")
                    (File.Exists destination) |> should equal false
                    (Directory.Exists(Path.GetDirectoryName destination)) |> should equal false
                elif failureExpected then
                    (outcome) |> should equal ("failed")
                    (diagnostic.Value) |> should haveSubstring ("external_tool_failed")
                    (File.Exists destination) |> should equal false
                    (Directory.Exists(Path.GetDirectoryName destination)) |> should equal false
                else
                    (outcome) |> should equal ("succeeded")
                    (File.Exists destination) |> should equal true

                WorkspaceRpcScenario.shutdown child 8u
            finally
                WorkspaceRpcScenario.disposeProcess child

                for path in [ started; continuePath ] do
                    if File.Exists path then
                        File.Delete path

                if Directory.Exists directory then
                    Directory.Delete(directory, true)

        runCase "success"
        runCase "failure"
        runCase "cancel"
        runCase "postaction"
        runCase "nested-project"

    [<Fact>]
    member _.``a template catalog change after preview rejects execution before the child starts``
        ()
        =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "context-catalog-change-before-child"

        let solution = Path.Combine(directory, "Catalog.slnx")
        let project = Path.Combine(directory, "Catalog.csproj")
        let cliHome = Path.Combine(directory, "home")
        let sdkRoot = Path.Combine(directory, "sdk")
        let sdkPath = Path.Combine(sdkRoot, "test")
        let started = Path.Combine(directory, "template-child.started")

        let cache =
            Path.Combine(cliHome, ".templateengine", "dotnetcli", "test", "templatecache.json")

        let model = SolutionModel()
        model.AddProject("Catalog.csproj", "Catalog", null) |> ignore
        WorkspaceRpcScenario.save solution model
        WorkspaceRpcScenario.writeProject project
        Directory.CreateDirectory sdkPath |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName cache) |> ignore

        File.WriteAllText(
            cache,
            """
            {
              "TemplateInfo": [
                {
                  "Identity": "custom.item",
                  "Name": "Custom contract",
                  "ShortNameList": ["contract"],
                  "Precedence": 200,
                  "Description": "Custom item",
                  "TagsCollection": { "language": "C#", "type": "item" }
                }
              ]
            }
            """
        )

        let fakeHost = DirectCommandProcess.copyScriptedDotnet directory

        use child =
            WorkspaceRpcScenario.startPipeWithEnvironment
                "solution"
                solution
                [ "DOTNET_HOST_PATH", fakeHost
                  "DOTNET_CLI_HOME", cliHome
                  "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", "workspace-command"
                  "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_SDK_ROOT", sdkRoot
                  "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH", started ]

        let response id methodName parameters =
            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request id methodName parameters)

            let error, result =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response id

            error, result

        try
            let initializeError, _ = response 1u "initialize" initialize
            (initializeError.IsNone) |> should equal true

            let _, root = response 2u "workspace/root" RpcValue.emptyMap
            let rootId = nodeId "workspace" root

            let _, rootChildren =
                response
                    3u
                    "workspace/children"
                    (WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String rootId
                          "pageSize", RpcValue.Integer 100L ])

            let projectId = nodeId "project" rootChildren

            let _, projectChildren =
                response
                    4u
                    "workspace/children"
                    (WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String projectId
                          "pageSize", RpcValue.Integer 100L ])

            let revision =
                WorkspaceRpcScenario.field "revision" projectChildren
                |> RpcValue.requireInteger "revision"

            match WorkspaceRpcScenario.readFrame child with
            | Notification("workspace/delta", _) -> ()
            | frame -> failwithf "Expected hydration delta, got %A" frame

            let optionsError, options =
                response
                    5u
                    "workspace/create/options"
                    (WorkspaceRpcScenario.map
                        [ "targetNodeId", RpcValue.String projectId
                          "expectedRevision", RpcValue.Integer revision ])

            (optionsError.IsNone) |> should equal true

            let selectionId =
                WorkspaceRpcScenario.field "options" options
                |> RpcValue.requireArray "options"
                |> Seq.find (fun option ->
                    WorkspaceRpcScenario.field "kind" option = RpcValue.String "itemTemplate")
                |> WorkspaceRpcScenario.field "selectionId"

            let arguments =
                WorkspaceRpcScenario.map
                    [ "selectionId", selectionId; "name", RpcValue.String "IContract" ]

            let request =
                WorkspaceRpcScenario.map
                    [ "commandId", RpcValue.String "workspace.create"
                      "targetNodeId", RpcValue.String projectId
                      "arguments", arguments
                      "expectedRevision", RpcValue.Integer revision ]

            let previewError, preview = response 6u "workspace/commands/preview" request

            (previewError.IsNone) |> should equal true
            File.AppendAllText(cache, Environment.NewLine)

            let executeRequest =
                match request with
                | RpcValue.Map fields ->
                    fields.Add(
                        "confirmationToken",
                        WorkspaceRpcScenario.field "confirmationToken" preview
                    )
                    |> RpcValue.Map
                | _ -> failwith "The create request must be a map."

            let executeError, _ = response 7u "workspace/commands/execute" executeRequest

            (executeError.Value.Code) |> should equal ("template_catalog_changed")
            (File.Exists started) |> should equal false
            (Directory.Exists(Path.Combine(directory, "Nested"))) |> should equal false
            WorkspaceRpcScenario.shutdown child 8u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``project language context filters item templates to the matching language and neutral options``
        ()
        =
        let runCase (extension: string) language =
            let directory =
                WorkspaceRpcScenario.temporaryDirectory
                    $"context-template-language-{extension.TrimStart('.')}"

            let solution = Path.Combine(directory, "Language.slnx")
            let projectName = $"Language{extension}"
            let project = Path.Combine(directory, projectName)
            let cliHome = Path.Combine(directory, "home")
            let sdkRoot = Path.Combine(directory, "sdk")
            let sdkPath = Path.Combine(sdkRoot, "test")

            let cache =
                Path.Combine(cliHome, ".templateengine", "dotnetcli", "test", "templatecache.json")

            let model = SolutionModel()
            model.AddProject(projectName, "Language", null) |> ignore
            WorkspaceRpcScenario.save solution model
            WorkspaceRpcScenario.writeProject project
            Directory.CreateDirectory sdkPath |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName cache) |> ignore

            File.WriteAllText(
                cache,
                """
                {
                  "TemplateInfo": [
                    {
                      "Identity": "item.csharp",
                      "Name": "C# item",
                      "ShortNameList": ["item"],
                      "Precedence": 100,
                      "TagsCollection": { "language": "C#", "type": "item" }
                    },
                    {
                      "Identity": "item.fsharp",
                      "Name": "F# item",
                      "ShortNameList": ["item"],
                      "Precedence": 100,
                      "TagsCollection": { "language": "F#", "type": "item" }
                    },
                    {
                      "Identity": "item.vb",
                      "Name": "VB item",
                      "ShortNameList": ["item"],
                      "Precedence": 100,
                      "TagsCollection": { "language": "VB", "type": "item" }
                    },
                    {
                      "Identity": "item.neutral",
                      "Name": "Neutral item",
                      "ShortNameList": ["neutral"],
                      "Precedence": 100,
                      "TagsCollection": { "type": "item" }
                    }
                  ]
                }
                """
            )

            let fakeHost = DirectCommandProcess.copyScriptedDotnet directory

            use child =
                WorkspaceRpcScenario.startPipeWithEnvironment
                    "solution"
                    solution
                    [ "DOTNET_HOST_PATH", fakeHost
                      "DOTNET_CLI_HOME", cliHome
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_MODE", "workspace-command"
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_SDK_ROOT", sdkRoot ]

            let response id methodName parameters =
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request id methodName parameters)

                let error, result =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response id

                match error with
                | Some value -> failwithf "%s failed: %s: %s" methodName value.Code value.Message
                | None -> result

            try
                response 1u "initialize" initialize |> ignore
                let root = response 2u "workspace/root" RpcValue.emptyMap
                let rootId = nodeId "workspace" root

                let rootChildren =
                    response
                        3u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String rootId
                              "pageSize", RpcValue.Integer 100L ])

                let projectId = nodeId "project" rootChildren

                let projectChildren =
                    response
                        4u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String projectId
                              "pageSize", RpcValue.Integer 100L ])

                let revision =
                    WorkspaceRpcScenario.field "revision" projectChildren
                    |> RpcValue.requireInteger "revision"

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _) -> ()
                | frame -> failwithf "Expected project hydration delta, got %A" frame

                let itemLanguages =
                    response
                        5u
                        "workspace/create/options"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String projectId
                              "expectedRevision", RpcValue.Integer revision ])
                    |> WorkspaceRpcScenario.field "options"
                    |> RpcValue.requireArray "options"
                    |> Seq.filter (fun option ->
                        WorkspaceRpcScenario.field "kind" option = RpcValue.String "itemTemplate")
                    |> Seq.map (fun option ->
                        RpcValue.tryField "language" option
                        |> Option.map (RpcValue.requireString "language"))
                    |> Seq.sort
                    |> Seq.toArray

                ((itemLanguages = [| None; Some language |])) |> should equal true
                WorkspaceRpcScenario.shutdown child 6u
            finally
                WorkspaceRpcScenario.disposeProcess child

                if Directory.Exists directory then
                    Directory.Delete(directory, true)

        runCase ".fsproj" "F#"
        runCase ".vbproj" "VB"

    [<Fact>]
    member _.``a projected file context creates and deletes files through generic workspace commands``
        ()
        =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "context-workspace-command-scenario"

        let solution = Path.Combine(directory, "Demo.slnx")
        let project = Path.Combine(directory, "Demo.csproj")
        let existing = Path.Combine(directory, "Existing.cs")
        let nestedDirectory = Path.Combine(directory, "Nested")
        let nestedExisting = Path.Combine(nestedDirectory, "Nested.cs")
        let created = Path.Combine(directory, "Created.cs")

        let dataHome =
            WorkspaceRpcScenario.temporaryDirectory "context-workspace-command-data-home"

        let cliHome =
            WorkspaceRpcScenario.temporaryDirectory "context-workspace-command-cli-home"

        let model = SolutionModel()
        model.AddProject("Demo.csproj", "Demo", null) |> ignore
        model.AddBuildType "Debug"
        model.AddPlatform "Any CPU"
        WorkspaceRpcScenario.save solution model
        Directory.CreateDirectory dataHome |> ignore

        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework>"
            + "</PropertyGroup></Project>"
        )

        File.WriteAllText(existing, "internal sealed class Existing { }")
        Directory.CreateDirectory nestedDirectory |> ignore
        File.WriteAllText(nestedExisting, "internal sealed class Nested { }")
        restore project

        WorkspaceRpcScenario.initializeCreationTemplateCatalog solution cliHome

        let workspace =
            match SolutionWorkspaceReader.OpenAsync(solution).Result with
            | Success value -> value
            | Failure failure -> failwithf "Could not open context fixture: %A" failure

        let unsupportedTargetIds =
            [ workspace.Contents.BuildTypes |> Seq.exactlyOne |> _.Id.Value
              workspace.Contents.Platforms |> Seq.exactlyOne |> _.Id.Value
              "unknown-context-target" ]

        use child =
            WorkspaceRpcScenario.startPipeWithEnvironment
                "solution"
                solution
                [ "DOTNET_CLI_HOME", cliHome; "XDG_DATA_HOME", dataHome ]

        try
            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 1u "initialize" initialize)

            let initializeError, initialized =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

            (initializeError.IsNone) |> should equal true

            let negotiated =
                WorkspaceRpcScenario.field "capabilities" initialized
                |> RpcValue.requireArray "capabilities"

            (negotiated) |> should contain (RpcValue.String "workspace.create.options")

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

            (childrenError.IsNone) |> should equal true

            let mutable revision =
                WorkspaceRpcScenario.field "revision" projectChildren
                |> RpcValue.requireInteger "revision"

            let fileId = nodeId "projectFile" projectChildren
            let folderId = nodeId "projectFolder" projectChildren
            let dependencyContainerId = nodeId "dependencyContainer" projectChildren

            match WorkspaceRpcScenario.readFrame child with
            | Notification("workspace/delta", _) -> ()
            | frame -> failwithf "Expected hydration delta, got %A" frame

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    55u
                    "workspace/create/options"
                    (WorkspaceRpcScenario.map
                        [ "targetNodeId", RpcValue.String fileId
                          "expectedRevision", RpcValue.Integer(revision - 1L) ]))

            let staleOptionsError, _ =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 55u

            (staleOptionsError.Value.Code) |> should equal ("workspace_conflict")

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    56u
                    "workspace/children"
                    (WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String dependencyContainerId
                          "pageSize", RpcValue.Integer 100L ]))

            let dependencyChildrenError, dependencyChildren =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 56u

            (dependencyChildrenError.IsNone) |> should equal true
            let dependencyId = nodeId "dependency" dependencyChildren

            for requestId, targetId in [ 57u, dependencyContainerId; 58u, dependencyId ] do
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        requestId
                        "workspace/commands/list"
                        (WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String targetId ]))

                let dependencyListError, dependencyList =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response requestId

                (dependencyListError.IsNone) |> should equal true

                let dependencyCommands =
                    WorkspaceRpcScenario.field "commands" dependencyList
                    |> RpcValue.requireArray "commands"
                    |> Seq.map (
                        WorkspaceRpcScenario.field "id" >> RpcValue.requireString "command.id"
                    )
                    |> Seq.toArray

                (dependencyCommands) |> should contain ("workspace.create")
                (dependencyCommands) |> should not' (contain ("workspace.delete"))

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        (requestId + 10u)
                        "workspace/create/options"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String targetId
                              "expectedRevision", RpcValue.Integer revision ]))

                let (dependencyOptionsError, dependencyOptions), observedRevision, _ =
                    WorkspaceRpcScenario.responseAfterWorkspaceNotifications
                        child
                        (requestId + 10u)
                        revision

                revision <- observedRevision

                (dependencyOptionsError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "options" dependencyOptions
                 |> RpcValue.requireArray "options")
                |> Seq.exists (fun option ->
                    WorkspaceRpcScenario.field "kind" option = RpcValue.String "empty")
                |> should equal true

                let rejected requestOffset methodName parameters =
                    WorkspaceRpcScenario.send
                        child
                        false
                        (WorkspaceRpcScenario.request
                            (requestId + requestOffset)
                            methodName
                            parameters)

                    let (error, _), observedRevision, _ =
                        WorkspaceRpcScenario.responseAfterWorkspaceNotifications
                            child
                            (requestId + requestOffset)
                            revision

                    revision <- observedRevision

                    (error.Value.Code) |> should equal ("not_found")

                rejected
                    20u
                    "workspace/commands/describe"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.delete"
                          "targetNodeId", RpcValue.String targetId ])

                let deleteRequest =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.delete"
                          "targetNodeId", RpcValue.String targetId
                          "arguments", RpcValue.emptyMap
                          "expectedRevision", RpcValue.Integer revision ]

                rejected 30u "workspace/commands/preview" deleteRequest

                let deleteExecute =
                    match deleteRequest with
                    | RpcValue.Map fields ->
                        fields.Add("confirmationToken", RpcValue.String(String('0', 64)))
                        |> RpcValue.Map
                    | _ -> failwith "The delete request must be a map."

                rejected 40u "workspace/commands/execute" deleteExecute

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    5u
                    "workspace/commands/list"
                    (WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String fileId ]))

            let listError, listed =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 5u

            (listError.IsNone) |> should equal true

            let commandIds =
                WorkspaceRpcScenario.field "commands" listed
                |> RpcValue.requireArray "commands"
                |> Seq.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "command.id")
                |> Seq.toArray

            (commandIds) |> should contain ("workspace.create")
            (commandIds) |> should contain ("workspace.delete")

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request
                    50u
                    "workspace/commands/describe"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String fileId ]))

            let describeError, described =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 50u

            (describeError.IsNone) |> should equal true

            let parameters =
                WorkspaceRpcScenario.field "command" described
                |> WorkspaceRpcScenario.field "parameters"
                |> RpcValue.requireArray "parameters"

            (parameters.Length) |> should equal (2)

            let parameterIds =
                parameters
                |> Seq.map (fun parameter ->
                    WorkspaceRpcScenario.field "id" parameter
                    |> RpcValue.requireString "parameter.id")
                |> Seq.toArray

            ((parameterIds = [| "selectionId"; "name" |])) |> should equal true

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

            (WorkspaceRpcScenario.field "revision" optionsResult
             |> RpcValue.requireInteger "revision")
            |> should equal (revision)

            let empty =
                let options =
                    WorkspaceRpcScenario.field "options" optionsResult
                    |> RpcValue.requireArray "options"

                (options)
                |> Seq.exists (fun option ->
                    WorkspaceRpcScenario.field "kind" option = RpcValue.String "projectTemplate")
                |> should equal true

                (options)
                |> Seq.exists (fun option ->
                    WorkspaceRpcScenario.field "kind" option = RpcValue.String "itemTemplate"
                    && (RpcValue.tryField "language" option
                        |> Option.exists ((<>) (RpcValue.String "C#"))))
                |> should equal false

                options
                |> Seq.find (fun option ->
                    WorkspaceRpcScenario.field "kind" option = RpcValue.String "empty")

            (WorkspaceRpcScenario.field "execution" empty)
            |> should equal (RpcValue.String "transaction")

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

            (previewError.IsNone) |> should equal true

            (WorkspaceRpcScenario.field "summary" preview)
            |> should not' (equal (RpcValue.Nil))

            let effects =
                WorkspaceRpcScenario.field "effects" preview |> RpcValue.requireArray "effects"

            (effects.Length) |> should equal (2)

            let effectOperations =
                effects |> Seq.map (WorkspaceRpcScenario.field "operation") |> Seq.toArray

            let expectedEffectOperations =
                [| RpcValue.String "create"; RpcValue.String "addToProject" |]

            ((effectOperations = expectedEffectOperations)) |> should equal true

            for effect in effects do
                let fields = WorkspaceRpcScenario.fields effect |> Seq.map _.Key |> Set.ofSeq

                ((fields = set [ "operation"; "target"; "recursive" ])) |> should equal true

                (WorkspaceRpcScenario.field "recursive" effect)
                |> should equal (RpcValue.Boolean false)

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

            (executeError.IsNone) |> should equal true

            (WorkspaceRpcScenario.field "applied" executeResult)
            |> should equal (RpcValue.Boolean true)

            (File.Exists created) |> should equal true

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

            ((itemOutcome = "succeeded")) |> should equal true

            (File.Exists(Path.Combine(directory, "IGenerated.cs"))) |> should equal true

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

                    let language =
                        RpcValue.optionalField "language" (WorkspaceRpcScenario.fields option)

                    kind = RpcValue.String "projectTemplate"
                    && displayName = RpcValue.String "Class Library"
                    && language = Some(RpcValue.String "C#"))

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

            for index, unsupportedTargetId in unsupportedTargetIds |> List.indexed do
                let requestId = 140u + uint32 (index * 10)

                let rejected methodName requestOffset parameters =
                    WorkspaceRpcScenario.send
                        child
                        false
                        (WorkspaceRpcScenario.request
                            (requestId + requestOffset)
                            methodName
                            parameters)

                    let error, _ =
                        WorkspaceRpcScenario.readFrame child
                        |> WorkspaceRpcScenario.response (requestId + requestOffset)

                    (error.IsSome) |> should equal true

                let target =
                    WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String unsupportedTargetId ]

                rejected "workspace/commands/list" 0u target

                rejected
                    "workspace/commands/describe"
                    1u
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String unsupportedTargetId ])

                rejected
                    "workspace/create/options"
                    2u
                    (WorkspaceRpcScenario.map
                        [ "targetNodeId", RpcValue.String unsupportedTargetId
                          "expectedRevision", RpcValue.Integer itemRevision ])

                let unsupportedRequest =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String unsupportedTargetId
                          "arguments", projectArguments
                          "expectedRevision", RpcValue.Integer itemRevision ]

                rejected "workspace/commands/preview" 3u unsupportedRequest

                let unsupportedExecute =
                    match unsupportedRequest with
                    | RpcValue.Map fields ->
                        fields.Add("confirmationToken", RpcValue.String(String('0', 64)))
                        |> RpcValue.Map
                    | _ -> failwith "The unsupported request must be a map."

                rejected "workspace/commands/execute" 4u unsupportedExecute

            (Directory.Exists(Path.Combine(directory, "Generated"))) |> should equal false

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

            ((outcome = "succeeded")) |> should equal true

            (File.Exists(Path.Combine(directory, "Generated", "Generated.csproj")))
            |> should equal true

            let reopened = WorkspaceCommandScenario.openSolution solution
            (reopened.SolutionProjects.Count) |> should equal (2)

            let mutable routedRevision = projectRevision

            let routeCreate requestId targetId name destination =
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        requestId
                        "workspace/commands/list"
                        (WorkspaceRpcScenario.map [ "targetNodeId", RpcValue.String targetId ]))

                let listError, listResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response requestId

                (listError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "commands" listResult
                 |> RpcValue.requireArray "commands")
                |> Seq.exists (fun command ->
                    WorkspaceRpcScenario.field "id" command = RpcValue.String "workspace.create")
                |> should equal true

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        (requestId + 1u)
                        "workspace/commands/describe"
                        (WorkspaceRpcScenario.map
                            [ "commandId", RpcValue.String "workspace.create"
                              "targetNodeId", RpcValue.String targetId ]))

                let describeError, _ =
                    WorkspaceRpcScenario.readFrame child
                    |> WorkspaceRpcScenario.response (requestId + 1u)

                (describeError.IsNone) |> should equal true

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        (requestId + 2u)
                        "workspace/create/options"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String targetId
                              "expectedRevision", RpcValue.Integer routedRevision ]))

                let optionsError, options =
                    WorkspaceRpcScenario.readFrame child
                    |> WorkspaceRpcScenario.response (requestId + 2u)

                (optionsError.IsNone) |> should equal true

                let emptySelection =
                    WorkspaceRpcScenario.field "options" options
                    |> RpcValue.requireArray "options"
                    |> Seq.find (fun option ->
                        WorkspaceRpcScenario.field "kind" option = RpcValue.String "empty")
                    |> WorkspaceRpcScenario.field "selectionId"

                let arguments =
                    WorkspaceRpcScenario.map
                        [ "selectionId", emptySelection; "name", RpcValue.String name ]

                let previewRequest =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String targetId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer routedRevision ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        (requestId + 3u)
                        "workspace/commands/preview"
                        previewRequest)

                let previewError, preview =
                    WorkspaceRpcScenario.readFrame child
                    |> WorkspaceRpcScenario.response (requestId + 3u)

                (previewError.IsNone) |> should equal true

                let executeRequest =
                    match previewRequest with
                    | RpcValue.Map fields ->
                        fields.Add(
                            "confirmationToken",
                            WorkspaceRpcScenario.field "confirmationToken" preview
                        )
                        |> RpcValue.Map
                    | _ -> failwith "The create request must be a map."

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        (requestId + 4u)
                        "workspace/commands/execute"
                        executeRequest)

                let executeError, executeResult =
                    WorkspaceRpcScenario.readFrame child
                    |> WorkspaceRpcScenario.response (requestId + 4u)

                (executeError.IsNone) |> should equal true

                routedRevision <-
                    WorkspaceRpcScenario.field "revision" executeResult
                    |> RpcValue.requireInteger "revision"

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _)
                | Notification("workspace/reset", _) -> ()
                | frame -> failwithf "Expected routed creation notification, got %A" frame

                (File.Exists destination) |> should equal true

            routeCreate
                100u
                dependencyContainerId
                "FromDependencyContainer.cs"
                (Path.Combine(directory, "FromDependencyContainer.cs"))

            routeCreate
                110u
                dependencyId
                "FromDependency.cs"
                (Path.Combine(directory, "FromDependency.cs"))

            routeCreate
                120u
                folderId
                "FromFolder.cs"
                (Path.Combine(nestedDirectory, "FromFolder.cs"))

            WorkspaceRpcScenario.previewAndExecute
                child
                20u
                "workspace.delete"
                fileId
                RpcValue.emptyMap
                routedRevision
                true

            (File.Exists existing) |> should equal false
            (File.Exists created) |> should equal true
            WorkspaceRpcScenario.shutdown child 30u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)

            if Directory.Exists cliHome then
                Directory.Delete(cliHome, true)

            if Directory.Exists dataHome then
                Directory.Delete(dataHome, true)
