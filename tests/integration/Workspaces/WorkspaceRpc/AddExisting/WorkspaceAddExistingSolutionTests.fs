namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

open System
open System.IO
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit


[<Collection("Workspace scenarios")>]
type WorkspaceAddExistingSolutionTests() =
    [<Fact>]
    member _.``clients that do not negotiate Add Existing receive neither its New option nor selector or command access``
        ()
        =
        WorkspaceAddExistingScenario.withPreparedWorkspaceCapability
            "add-existing-unnegotiated"
            ".slnx"
            (fun _ _ -> ())
            false
            (fun _ _ child ->
                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root
                let revision = WorkspaceAddExistingScenario.revision root

                WorkspaceAddExistingScenario.createOptions child 3u rootId revision
                |> WorkspaceRpcScenario.field "options"
                |> RpcValue.requireArray "options"
                |> Seq.exists (fun option ->
                    WorkspaceRpcScenario.field "kind" option = RpcValue.String "addExisting")
                |> should equal false

                let selectorError, _ =
                    WorkspaceAddExistingScenario.call
                        child
                        4u
                        "workspace/addExisting/start"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String rootId
                              "selectionId", RpcValue.String "forged"
                              "expectedRevision", RpcValue.Integer revision ])

                selectorError
                |> Option.map _.Code
                |> should equal (Some "unsupported_capability")

                for (requestId, methodName, parameters) in
                    [ (5u,
                       "workspace/addExisting/children",
                       WorkspaceRpcScenario.map
                           [ "selectorId", RpcValue.String "forged"
                             "parentEntryId", RpcValue.String "forged-parent" ])
                      (6u,
                       "workspace/addExisting/close",
                       WorkspaceRpcScenario.map [ "selectorId", RpcValue.String "forged" ]) ] do
                    let error, _ =
                        WorkspaceAddExistingScenario.call child requestId methodName parameters

                    error |> Option.map _.Code |> should equal (Some "unsupported_capability")

                let malformedChildren, _ =
                    WorkspaceAddExistingScenario.call
                        child
                        7u
                        "workspace/addExisting/children"
                        (WorkspaceRpcScenario.map
                            [ "selectorId", RpcValue.String "forged"
                              "unexpected", RpcValue.Boolean true ])

                malformedChildren |> Option.map _.Code |> should equal (Some "invalid_params")

                let malformedClose, _ =
                    WorkspaceAddExistingScenario.call
                        child
                        8u
                        "workspace/addExisting/close"
                        (WorkspaceRpcScenario.map
                            [ "selectorId", RpcValue.String "forged"
                              "unexpected", RpcValue.Boolean true ])

                malformedClose |> Option.map _.Code |> should equal (Some "invalid_params")

                let commandError, _ =
                    WorkspaceAddExistingScenario.call
                        child
                        9u
                        "workspace/commands/describe"
                        (WorkspaceRpcScenario.map
                            [ "commandId", RpcValue.String "workspace.addExisting"
                              "targetNodeId", RpcValue.String rootId ])

                commandError |> Option.map _.Code |> should equal (Some "not_found"))

    [<Fact>]
    member _.``creating a logical Solution Folder through New preserves an empty nested solution concern without creating a directory``
        ()
        =
        WorkspaceAddExistingScenario.withWorkspace
            "logical-solution-folder"
            (fun directory solution child ->
                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root
                let revision = WorkspaceAddExistingScenario.revision root

                let options =
                    WorkspaceAddExistingScenario.successful
                        child
                        3u
                        "workspace/create/options"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String rootId
                              "expectedRevision", RpcValue.Integer revision ])

                let selection = WorkspaceAddExistingScenario.option "solutionFolder" options

                WorkspaceRpcScenario.field "execution" selection
                |> should equal (RpcValue.String "transaction")

                let nextRevision =
                    WorkspaceAddExistingScenario.createSolutionFolder
                        child
                        4u
                        rootId
                        revision
                        (WorkspaceRpcScenario.field "selectionId" selection)
                        "Solution Items"

                let rootChildren = WorkspaceAddExistingScenario.children child 6u rootId

                let folderId =
                    rootChildren
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        WorkspaceRpcScenario.field "name" node = RpcValue.String "Solution Items")
                    |> WorkspaceAddExistingScenario.nodeId

                let nestedSelection =
                    WorkspaceAddExistingScenario.createOptions child 7u folderId nextRevision
                    |> WorkspaceAddExistingScenario.option "solutionFolder"
                    |> WorkspaceRpcScenario.field "selectionId"

                WorkspaceAddExistingScenario.createSolutionFolder
                    child
                    8u
                    folderId
                    nextRevision
                    nestedSelection
                    "Generated"
                |> ignore

                let reopened = WorkspaceCommandScenario.openSolution solution

                reopened.SolutionFolders
                |> Seq.map _.Path
                |> Set.ofSeq
                |> should equal (Set.ofList [ "/Solution Items/"; "/Solution Items/Generated/" ])

                Directory.Exists(Path.Combine(directory, "Solution Items"))
                |> should equal false)

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``an opaque paged Add Existing selector atomically adds CSharp FSharp and VisualBasic root projects to editable solution formats and closes after success``
        (extension: string)
        =
        WorkspaceAddExistingScenario.withPreparedWorkspace
            $"add-existing-projects-{extension.TrimStart('.')}"
            extension
            (fun _ _ -> ())
            (fun directory solution child ->
                let projectPaths =
                    [| "Alpha.csproj"; "Beta.fsproj"; "Gamma.vbproj" |]
                    |> Array.map (fun name ->
                        let path = Path.Combine(directory, name)
                        WorkspaceRpcScenario.writeProject path
                        path)

                File.WriteAllText(Path.Combine(directory, "notes.txt"), "not a root project")

                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root
                let revision = WorkspaceAddExistingScenario.revision root

                let options =
                    WorkspaceAddExistingScenario.successful
                        child
                        3u
                        "workspace/create/options"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String rootId
                              "expectedRevision", RpcValue.Integer revision ])

                let selection = WorkspaceAddExistingScenario.option "addExisting" options

                let started =
                    WorkspaceAddExistingScenario.successful
                        child
                        4u
                        "workspace/addExisting/start"
                        (WorkspaceRpcScenario.map
                            [ "targetNodeId", RpcValue.String rootId
                              "selectionId", WorkspaceRpcScenario.field "selectionId" selection
                              "expectedRevision", RpcValue.Integer revision
                              "pageSize", RpcValue.Integer 2L ])

                WorkspaceRpcScenario.field "maxSelectionCount" started
                |> RpcValue.requireInteger "maxSelectionCount"
                |> should equal 256L

                let selectorId =
                    WorkspaceRpcScenario.field "selectorId" started
                    |> RpcValue.requireString "selectorId"

                let rootEntryId =
                    WorkspaceRpcScenario.field "root" started
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                let entries = ResizeArray<RpcValue>()

                WorkspaceRpcScenario.field "entries" started
                |> RpcValue.requireArray "entries"
                |> entries.AddRange

                let mutable token =
                    RpcValue.optionalField "nextToken" (WorkspaceRpcScenario.fields started)
                    |> Option.map (RpcValue.requireString "nextToken")

                let mutable requestId = 5u

                while token.IsSome do
                    let page =
                        WorkspaceAddExistingScenario.successful
                            child
                            requestId
                            "workspace/addExisting/children"
                            (WorkspaceRpcScenario.map
                                [ "selectorId", RpcValue.String selectorId
                                  "parentEntryId", RpcValue.String rootEntryId
                                  "pageSize", RpcValue.Integer 2L
                                  "continuationToken", RpcValue.String token.Value ])

                    WorkspaceRpcScenario.field "entries" page
                    |> RpcValue.requireArray "entries"
                    |> entries.AddRange

                    token <-
                        RpcValue.optionalField "nextToken" (WorkspaceRpcScenario.fields page)
                        |> Option.map (RpcValue.requireString "nextToken")

                    requestId <- requestId + 1u

                let selected =
                    entries
                    |> Seq.filter (fun entry ->
                        WorkspaceRpcScenario.field "selectable" entry = RpcValue.Boolean true)
                    |> Seq.map (fun entry ->
                        WorkspaceRpcScenario.field "entryId" entry
                        |> RpcValue.requireString "entryId")
                    |> Seq.toArray

                selected.Length |> should equal 3

                entries
                |> Seq.find (fun entry ->
                    WorkspaceRpcScenario.field "displayName" entry = RpcValue.String "notes.txt")
                |> WorkspaceRpcScenario.field "selectable"
                |> should equal (RpcValue.Boolean false)

                let arguments =
                    WorkspaceRpcScenario.map
                        [ "selectorId", RpcValue.String selectorId
                          "entryIds", selected |> Seq.map RpcValue.String |> RpcValue.array ]

                let request =
                    WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "workspace.addExisting"
                          "targetNodeId", RpcValue.String rootId
                          "arguments", arguments
                          "expectedRevision", RpcValue.Integer revision ]

                let preview =
                    WorkspaceAddExistingScenario.successful
                        child
                        20u
                        "workspace/commands/preview"
                        request

                let execute =
                    match request with
                    | RpcValue.Map fields ->
                        fields.Add(
                            "confirmationToken",
                            WorkspaceRpcScenario.field "confirmationToken" preview
                        )
                        |> RpcValue.Map
                    | _ -> failwith "The add-existing request must be a map."

                WorkspaceAddExistingScenario.execute child 21u execute |> ignore

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _)
                | Notification("workspace/reset", _) -> ()
                | frame ->
                    failwithf "Expected an Add Existing workspace notification, got %A" frame

                let closeError, _ =
                    WorkspaceAddExistingScenario.call
                        child
                        22u
                        "workspace/addExisting/close"
                        (WorkspaceRpcScenario.map [ "selectorId", RpcValue.String selectorId ])

                closeError |> Option.map _.Code |> should equal (Some "selector_unavailable")

                let reopened = WorkspaceCommandScenario.openSolution solution

                reopened.SolutionProjects
                |> Seq.map (fun project -> Path.GetFullPath(project.FilePath, directory))
                |> Set.ofSeq
                |> should equal (projectPaths |> Seq.map Path.GetFullPath |> Set.ofSeq))

    [<Fact>]
    member _.``registered unsupported and case-colliding sources are ineligible while execute-time revalidation rolls back the whole batch and preserves selected bytes``
        ()
        =
        WorkspaceAddExistingScenario.withPreparedWorkspace
            "add-existing-atomic-revalidation"
            ".slnx"
            (fun directory model ->
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Existing.csproj"))
                model.AddProject("Existing.csproj", null, null) |> ignore

                let caseCandidate = Path.Combine(directory, "casecandidate.csproj")
                WorkspaceRpcScenario.writeProject caseCandidate
                model.AddProject("CASECANDIDATE.csproj", null, null) |> ignore

                for name in [ "Alpha.csproj"; "Beta.csproj" ] do
                    WorkspaceRpcScenario.writeProject (Path.Combine(directory, name))

                File.WriteAllText(Path.Combine(directory, "README.txt"), "unsupported at root"))
            (fun directory solution child ->
                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root
                let revision = WorkspaceAddExistingScenario.revision root
                let started = WorkspaceAddExistingScenario.startSelector child 3u rootId revision

                let selectorId =
                    WorkspaceRpcScenario.field "selectorId" started
                    |> RpcValue.requireString "selectorId"

                let selectorRootId =
                    WorkspaceRpcScenario.field "root" started
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                let entries =
                    WorkspaceAddExistingScenario.allEntries
                        child
                        5u
                        selectorId
                        selectorRootId
                        started

                let byName name =
                    entries
                    |> Seq.find (fun entry ->
                        WorkspaceRpcScenario.field "displayName" entry = RpcValue.String name)

                for name in [ "Existing.csproj"; "README.txt" ] do
                    byName name
                    |> WorkspaceRpcScenario.field "selectable"
                    |> should equal (RpcValue.Boolean false)

                let caseSemantics =
                    FileSystemCaseSensitivityDetector.DetectFromExistingPath directory

                byName "casecandidate.csproj"
                |> WorkspaceRpcScenario.field "selectable"
                |> should
                    equal
                    (RpcValue.Boolean(caseSemantics = FileSystemCaseSensitivity.Sensitive))

                let alpha = byName "Alpha.csproj"
                let beta = byName "Beta.csproj"

                let selected = [ alpha; beta ] |> List.map WorkspaceAddExistingScenario.entryId

                let request =
                    WorkspaceAddExistingScenario.previewRequest rootId revision selectorId selected

                let preview =
                    WorkspaceAddExistingScenario.successful
                        child
                        20u
                        "workspace/commands/preview"
                        request

                let execute =
                    match request with
                    | RpcValue.Map fields ->
                        fields.Add(
                            "confirmationToken",
                            WorkspaceRpcScenario.field "confirmationToken" preview
                        )
                        |> RpcValue.Map
                    | _ -> failwith "The batch request must be a map."

                let alphaPath = Path.Combine(directory, "Alpha.csproj")
                let betaPath = Path.Combine(directory, "Beta.csproj")
                File.AppendAllText(betaPath, Environment.NewLine)
                let alphaBefore = File.ReadAllBytes alphaPath
                let betaBefore = File.ReadAllBytes betaPath

                let executeError, _ =
                    WorkspaceAddExistingScenario.call
                        child
                        21u
                        "workspace/commands/execute"
                        execute

                executeError.IsSome |> should equal true
                File.ReadAllBytes alphaPath |> should equal alphaBefore
                File.ReadAllBytes betaPath |> should equal betaBefore

                let afterFailure = WorkspaceCommandScenario.openSolution solution

                afterFailure.SolutionProjects
                |> Seq.map _.FilePath
                |> Set.ofSeq
                |> should equal (Set.ofList [ "CASECANDIDATE.csproj"; "Existing.csproj" ])

                let replacement =
                    WorkspaceAddExistingScenario.startSelector child 22u rootId revision

                let replacementId =
                    WorkspaceRpcScenario.field "selectorId" replacement
                    |> RpcValue.requireString "selectorId"

                let replacementRootId =
                    WorkspaceRpcScenario.field "root" replacement
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                let alphaId =
                    WorkspaceAddExistingScenario.allEntries
                        child
                        24u
                        replacementId
                        replacementRootId
                        replacement
                    |> Seq.find (fun entry ->
                        WorkspaceRpcScenario.field "displayName" entry = RpcValue.String
                            "Alpha.csproj")
                    |> WorkspaceAddExistingScenario.entryId

                let successfulRequest =
                    WorkspaceAddExistingScenario.previewRequest
                        rootId
                        revision
                        replacementId
                        [ alphaId ]

                let successfulPreview =
                    WorkspaceAddExistingScenario.successful
                        child
                        30u
                        "workspace/commands/preview"
                        successfulRequest

                let successfulExecute =
                    match successfulRequest with
                    | RpcValue.Map fields ->
                        fields.Add(
                            "confirmationToken",
                            WorkspaceRpcScenario.field "confirmationToken" successfulPreview
                        )
                        |> RpcValue.Map
                    | _ -> failwith "The successful request must be a map."

                WorkspaceAddExistingScenario.execute child 31u successfulExecute |> ignore

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _)
                | Notification("workspace/reset", _) -> ()
                | frame -> failwithf "Expected a workspace mutation notification, got %A" frame

                File.ReadAllBytes alphaPath |> should equal alphaBefore)

    [<Fact>]
    member _.``a solution-folder selector adds an ordinary solution item and nests an existing project in one atomic solution edit``
        ()
        =
        WorkspaceAddExistingScenario.withPreparedWorkspace
            "add-existing-solution-items"
            ".slnx"
            (fun directory model ->
                model.AddFolder "/Solution Items/" |> ignore
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Nested.fsproj"))
                File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project />"))
            (fun directory solution child ->
                let root = WorkspaceAddExistingScenario.root child
                let revision = WorkspaceAddExistingScenario.revision root
                let rootId = WorkspaceAddExistingScenario.nodeId root

                let folder =
                    WorkspaceAddExistingScenario.children child 3u rootId
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        WorkspaceRpcScenario.field "kind" node = RpcValue.String "solutionFolder")

                let folderId = WorkspaceAddExistingScenario.nodeId folder

                let started = WorkspaceAddExistingScenario.startSelector child 4u folderId revision

                let selectorId =
                    WorkspaceRpcScenario.field "selectorId" started
                    |> RpcValue.requireString "selectorId"

                let selectorRevision =
                    WorkspaceRpcScenario.field "revision" started
                    |> RpcValue.requireInteger "revision"

                let rootEntryId =
                    WorkspaceRpcScenario.field "root" started
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                let selected =
                    WorkspaceAddExistingScenario.allEntries child 6u selectorId rootEntryId started
                    |> Seq.filter (fun entry ->
                        match
                            WorkspaceRpcScenario.field "displayName" entry
                            |> RpcValue.requireString "displayName"
                        with
                        | "Nested.fsproj"
                        | "Directory.Build.props" -> true
                        | _ -> false)
                    |> Seq.map (fun entry ->
                        WorkspaceRpcScenario.field "entryId" entry
                        |> RpcValue.requireString "entryId")
                    |> Seq.toArray

                selected.Length |> should equal 2

                let request =
                    WorkspaceAddExistingScenario.previewRequest
                        folderId
                        selectorRevision
                        selectorId
                        selected

                let preview =
                    WorkspaceAddExistingScenario.successful
                        child
                        20u
                        "workspace/commands/preview"
                        request

                let execute =
                    match request with
                    | RpcValue.Map fields ->
                        fields.Add(
                            "confirmationToken",
                            WorkspaceRpcScenario.field "confirmationToken" preview
                        )
                        |> RpcValue.Map
                    | _ -> failwith "The solution-folder request must be a map."

                WorkspaceAddExistingScenario.execute child 21u execute |> ignore

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _)
                | Notification("workspace/reset", _) -> ()
                | frame ->
                    failwithf "Expected a solution-folder mutation notification, got %A" frame

                let reopened =
                    match SolutionWorkspaceReader.OpenAsync(solution).Result with
                    | Success workspace -> workspace
                    | Failure failure -> failwithf "The edited solution did not reopen: %A" failure

                reopened.Contents.Projects
                |> Seq.exactlyOne
                |> _.ParentFolderPath
                |> should equal (Some "/Solution Items/")

                reopened.Contents.Items
                |> Seq.exactlyOne
                |> _.FolderPath
                |> should equal (Some "/Solution Items/")

                File.ReadAllText(Path.Combine(directory, "Directory.Build.props"))
                |> should equal "<Project />")
