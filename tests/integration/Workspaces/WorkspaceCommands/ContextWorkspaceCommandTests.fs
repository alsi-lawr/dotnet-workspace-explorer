namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Workspaces
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
    member _.``should preview and execute every contextual delete cascade``() =
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
            Assert.True((operations filePreview = [| "removeFromProject", false; "trash", false |]))

            let folderPreview = preview 11u generatedId revision
            let folderOperations = operations folderPreview

            Assert.Equal(
                2,
                folderOperations
                |> Array.filter (fst >> (=) "removeFromProject")
                |> Array.length
            )

            Assert.True(Array.contains ("trash", true) folderOperations)

            let projectPreview = preview 12u standaloneId revision
            Assert.True((operations projectPreview = [| "removeFromSolution", false |]))

            let itemPreview = preview 13u solutionItemId revision

            let expectedItemOperations = [| "removeFromSolution", false; "trash", false |]

            Assert.True((operations itemPreview = expectedItemOperations))

            let logicalPreview = preview 14u solutionFolderId revision
            let logicalOperations = operations logicalPreview
            Assert.True(Array.contains ("removeFromSolution", true) logicalOperations)
            Assert.True(logicalOperations.Length >= 3)

            let mutable current = execute 20u looseId revision filePreview
            Assert.False(File.Exists loose)

            let nextFolderPreview = preview 22u generatedId current
            current <- execute 23u generatedId current nextFolderPreview
            Assert.False(Directory.Exists generated)

            let nextProjectPreview = preview 25u standaloneId current
            current <- execute 26u standaloneId current nextProjectPreview
            Assert.True(File.Exists standaloneProject)

            let nextItemPreview = preview 28u solutionItemId current
            current <- execute 29u solutionItemId current nextItemPreview
            Assert.False(File.Exists solutionItem)

            let nextLogicalPreview = preview 31u solutionFolderId current
            execute 32u solutionFolderId current nextLogicalPreview |> ignore
            Assert.True(File.Exists nestedProject)
            Assert.True(File.Exists nestedSource)

            let reopened = WorkspaceCommandScenario.openSolution solution
            Assert.Empty reopened.SolutionProjects
            Assert.Empty reopened.SolutionFolders
            WorkspaceRpcScenario.shutdown child 40u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should publish nested custom template output and compensate child failure``() =
        let runCase mode =
            let partialRecoveryExpected = mode = "partial"
            let postactionExpected = mode = "postaction"
            let nestedProjectExpected = mode = "nested-project"
            let projectTemplateExpected = postactionExpected || nestedProjectExpected
            let failureExpected = mode = "failure" || partialRecoveryExpected
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
                  if partialRecoveryExpected then
                      "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_TEMPLATE_BLOCK_CLEANUP", "true"
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

            let mutable stagingToClean = None

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" initialize)

                let initializeError, _ =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

                Assert.True initializeError.IsNone

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

                Assert.True optionsError.IsNone

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

                Assert.True executeError.IsNone

                let operationId =
                    WorkspaceRpcScenario.field "operationId" executeResult
                    |> RpcValue.requireString "operationId"

                if partialRecoveryExpected then
                    stagingToClean <-
                        Some(
                            Path.Combine(
                                Path.GetTempPath(),
                                "dotnet-workspace-explorer",
                                operationId
                            )
                        )

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
                            | Response(70u, error, result) ->
                                Assert.True error.IsNone

                                Assert.Equal(
                                    RpcValue.Boolean true,
                                    WorkspaceRpcScenario.field "accepted" result
                                )

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
                    Assert.Equal("failed", outcome)
                    Assert.Contains("template_output_changed", diagnostic.Value)
                    Assert.False(Directory.Exists(Path.Combine(directory, "Generated")))

                    let reopened = WorkspaceCommandScenario.openSolution solution
                    Assert.Equal(1, reopened.SolutionProjects.Count)
                elif nestedProjectExpected then
                    Assert.Equal("succeeded", outcome)

                    Assert.True(
                        File.Exists(Path.Combine(directory, "Generated", "src", "Nested.csproj"))
                    )

                    let reopened = WorkspaceCommandScenario.openSolution solution
                    Assert.Equal(2, reopened.SolutionProjects.Count)
                elif cancellationExpected then
                    Assert.Equal("cancelled", outcome)
                    Assert.False(File.Exists destination)
                    Assert.False(Directory.Exists(Path.GetDirectoryName destination))
                elif failureExpected then
                    Assert.Equal("failed", outcome)

                    if partialRecoveryExpected then
                        Assert.Contains("partial_recovery_required", diagnostic.Value)
                    else
                        Assert.Contains("external_tool_failed", diagnostic.Value)

                    Assert.False(File.Exists destination)
                    Assert.False(Directory.Exists(Path.GetDirectoryName destination))
                else
                    Assert.Equal("succeeded", outcome)
                    Assert.True(File.Exists destination)

                WorkspaceRpcScenario.shutdown child 8u
            finally
                WorkspaceRpcScenario.disposeProcess child

                if partialRecoveryExpected && not (OperatingSystem.IsWindows()) then
                    stagingToClean
                    |> Option.iter (fun staging ->
                        if Directory.Exists staging then
                            File.SetUnixFileMode(
                                staging,
                                UnixFileMode.UserRead
                                ||| UnixFileMode.UserWrite
                                ||| UnixFileMode.UserExecute
                            )

                            Directory.Delete(staging, true))

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

        if not (OperatingSystem.IsWindows()) then
            runCase "partial"

    [<Fact>]
    member _.``should reject a catalog change after preview before starting the template child``() =
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
            Assert.True initializeError.IsNone

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

            Assert.True optionsError.IsNone

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

            Assert.True previewError.IsNone
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

            Assert.Equal("template_catalog_changed", executeError.Value.Code)
            Assert.False(File.Exists started)
            Assert.False(Directory.Exists(Path.Combine(directory, "Nested")))
            WorkspaceRpcScenario.shutdown child 8u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should filter item templates for FSharp and Visual Basic project contexts``() =
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

                Assert.True((itemLanguages = [| None; Some language |]))
                WorkspaceRpcScenario.shutdown child 6u
            finally
                WorkspaceRpcScenario.disposeProcess child

                if Directory.Exists directory then
                    Directory.Delete(directory, true)

        runCase ".fsproj" "F#"
        runCase ".vbproj" "VB"

    [<Fact>]
    member _.``should create and delete from a projected file context through generic commands``() =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "context-workspace-command-scenario"

        let solution = Path.Combine(directory, "Demo.slnx")
        let project = Path.Combine(directory, "Demo.csproj")
        let existing = Path.Combine(directory, "Existing.cs")
        let nestedDirectory = Path.Combine(directory, "Nested")
        let nestedExisting = Path.Combine(nestedDirectory, "Nested.cs")
        let created = Path.Combine(directory, "Created.cs")
        let dataHome = Path.Combine(directory, "data")
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

        let workspace =
            match SolutionWorkspaceReader.OpenAsync(solution).Result with
            | Success value -> value
            | Failure failure -> failwithf "Could not open context fixture: %A" failure

        let unsupportedTargetIds =
            [ workspace.Contents.BuildTypes |> Seq.exactlyOne |> _.Id.Value
              workspace.Contents.Platforms |> Seq.exactlyOne |> _.Id.Value
              "unknown-context-target" ]

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

            Assert.Equal("workspace_conflict", staleOptionsError.Value.Code)

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

            Assert.True dependencyChildrenError.IsNone
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

                Assert.True dependencyListError.IsNone

                let dependencyCommands =
                    WorkspaceRpcScenario.field "commands" dependencyList
                    |> RpcValue.requireArray "commands"
                    |> Seq.map (
                        WorkspaceRpcScenario.field "id" >> RpcValue.requireString "command.id"
                    )
                    |> Seq.toArray

                Assert.Contains("workspace.create", dependencyCommands)
                Assert.DoesNotContain("workspace.delete", dependencyCommands)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        (requestId + 10u)
                        "workspace/create/options"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String targetId
                              "expectedRevision", RpcValue.Integer revision ]))

                let dependencyOptionsError, dependencyOptions =
                    WorkspaceRpcScenario.readFrame child
                    |> WorkspaceRpcScenario.response (requestId + 10u)

                Assert.True dependencyOptionsError.IsNone

                Assert.Contains(
                    WorkspaceRpcScenario.field "options" dependencyOptions
                    |> RpcValue.requireArray "options",
                    fun option -> WorkspaceRpcScenario.field "kind" option = RpcValue.String "empty"
                )

                let rejected requestOffset methodName parameters =
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

                    Assert.Equal("not_found", error.Value.Code)

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
                    50u
                    "workspace/commands/describe"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.create"
                          "targetNodeId", RpcValue.String fileId ]))

            let describeError, described =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 50u

            Assert.True describeError.IsNone

            let parameters =
                WorkspaceRpcScenario.field "command" described
                |> WorkspaceRpcScenario.field "parameters"
                |> RpcValue.requireArray "parameters"

            Assert.Equal(2, parameters.Length)

            let parameterIds =
                parameters
                |> Seq.map (fun parameter ->
                    WorkspaceRpcScenario.field "id" parameter
                    |> RpcValue.requireString "parameter.id")
                |> Seq.toArray

            Assert.True((parameterIds = [| "selectionId"; "name" |]))

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

            let effectOperations =
                effects |> Seq.map (WorkspaceRpcScenario.field "operation") |> Seq.toArray

            let expectedEffectOperations =
                [| RpcValue.String "create"; RpcValue.String "addToProject" |]

            Assert.True((effectOperations = expectedEffectOperations))

            for effect in effects do
                let fields = WorkspaceRpcScenario.fields effect |> Seq.map _.Key |> Set.ofSeq

                Assert.True((fields = set [ "operation"; "target"; "recursive" ]))
                Assert.Equal(RpcValue.Boolean false, WorkspaceRpcScenario.field "recursive" effect)

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

                    Assert.True(error.IsSome, $"{methodName} accepted an unsupported target.")

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

            Assert.False(Directory.Exists(Path.Combine(directory, "Generated")))

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

                Assert.True listError.IsNone

                Assert.Contains(
                    WorkspaceRpcScenario.field "commands" listResult
                    |> RpcValue.requireArray "commands",
                    fun command ->
                        WorkspaceRpcScenario.field "id" command = RpcValue.String "workspace.create"
                )

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

                Assert.True describeError.IsNone

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

                Assert.True optionsError.IsNone

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

                Assert.True previewError.IsNone

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

                Assert.True executeError.IsNone

                routedRevision <-
                    WorkspaceRpcScenario.field "revision" executeResult
                    |> RpcValue.requireInteger "revision"

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _)
                | Notification("workspace/reset", _) -> ()
                | frame -> failwithf "Expected routed creation notification, got %A" frame

                Assert.True(File.Exists destination)

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

            Assert.False(File.Exists existing)
            Assert.True(File.Exists created)
            WorkspaceRpcScenario.shutdown child 30u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)
