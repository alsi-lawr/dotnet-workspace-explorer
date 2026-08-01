namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit


[<Collection("Workspace scenarios")>]
type WorkspaceAddExistingProjectTests() =
    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``project and project-folder selectors add ordinary items while project files remain ineligible and never become references``
        (useProjectFolder: bool)
        =
        WorkspaceAddExistingScenario.withPreparedWorkspace
            (if useProjectFolder then
                 "add-existing-project-folder-item"
             else
                 "add-existing-project-item")
            ".slnx"
            (fun directory model ->
                model.AddProject("Demo.csproj", "Demo", null) |> ignore
                let folder = Path.Combine(directory, "Folder")
                Directory.CreateDirectory folder |> ignore
                File.WriteAllText(Path.Combine(folder, "Anchor.txt"), "anchor")
                File.WriteAllText(Path.Combine(folder, "Loose.txt"), "loose")
                File.WriteAllText(Path.Combine(directory, "RootLoose.cs"), "class RootLoose {}")
                WorkspaceRpcScenario.writeProject (Path.Combine(directory, "Blocked.fsproj"))

                File.WriteAllText(
                    Path.Combine(directory, "Demo.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <EnableDefaultItems>false</EnableDefaultItems>
                      </PropertyGroup>
                      <ItemGroup>
                        <None Include="Folder/Anchor.txt" />
                      </ItemGroup>
                    </Project>
                    """
                ))
            (fun directory _ child ->
                let root = WorkspaceAddExistingScenario.root child
                let rootId = WorkspaceAddExistingScenario.nodeId root

                let project =
                    WorkspaceAddExistingScenario.children child 3u rootId
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        WorkspaceRpcScenario.field "kind" node = RpcValue.String "project")

                let projectId = WorkspaceAddExistingScenario.nodeId project

                let projectChildren = WorkspaceAddExistingScenario.children child 4u projectId

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _) -> ()
                | frame -> failwithf "Expected a project hydration notification, got %A" frame

                let revision =
                    WorkspaceRpcScenario.field "revision" projectChildren
                    |> RpcValue.requireInteger "revision"

                let targetId =
                    if useProjectFolder then
                        projectChildren
                        |> WorkspaceRpcScenario.field "nodes"
                        |> RpcValue.requireArray "nodes"
                        |> Seq.find (fun node ->
                            WorkspaceRpcScenario.field "kind" node = RpcValue.String
                                "projectFolder")
                        |> WorkspaceAddExistingScenario.nodeId
                    else
                        projectId

                let started = WorkspaceAddExistingScenario.startSelector child 5u targetId revision

                let selectorId =
                    WorkspaceRpcScenario.field "selectorId" started
                    |> RpcValue.requireString "selectorId"

                let selectorRevision =
                    WorkspaceRpcScenario.field "revision" started
                    |> RpcValue.requireInteger "revision"

                let selectorRootId =
                    WorkspaceRpcScenario.field "root" started
                    |> WorkspaceRpcScenario.field "entryId"
                    |> RpcValue.requireString "entryId"

                let rootEntries =
                    WorkspaceAddExistingScenario.allEntries
                        child
                        7u
                        selectorId
                        selectorRootId
                        started

                rootEntries
                |> Seq.find (fun entry ->
                    WorkspaceRpcScenario.field "displayName" entry = RpcValue.String
                        "Blocked.fsproj")
                |> WorkspaceRpcScenario.field "selectable"
                |> should equal (RpcValue.Boolean false)

                let candidateEntries =
                    if useProjectFolder then
                        let directoryEntry =
                            rootEntries
                            |> Seq.find (fun entry ->
                                WorkspaceRpcScenario.field "displayName" entry = RpcValue.String
                                    "Folder")

                        let directoryId = WorkspaceAddExistingScenario.entryId directoryEntry

                        let page =
                            WorkspaceAddExistingScenario.successful
                                child
                                10u
                                "workspace/addExisting/children"
                                (WorkspaceRpcScenario.map
                                    [ "selectorId", RpcValue.String selectorId
                                      "parentEntryId", RpcValue.String directoryId
                                      "pageSize", RpcValue.Integer 4096L ])

                        WorkspaceAddExistingScenario.allEntries
                            child
                            11u
                            selectorId
                            directoryId
                            page
                    else
                        rootEntries

                let selected =
                    candidateEntries
                    |> Seq.find (fun entry ->
                        WorkspaceRpcScenario.field "displayName" entry = RpcValue.String(
                            if useProjectFolder then "Loose.txt" else "RootLoose.cs"
                        ))

                WorkspaceRpcScenario.field "selectable" selected
                |> should equal (RpcValue.Boolean true)

                let selectedId = WorkspaceAddExistingScenario.entryId selected

                let request =
                    WorkspaceAddExistingScenario.previewRequest
                        targetId
                        selectorRevision
                        selectorId
                        [ selectedId ]

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
                    | _ -> failwith "The project-item request must be a map."

                WorkspaceAddExistingScenario.execute child 21u execute |> ignore

                match WorkspaceRpcScenario.readFrame child with
                | Notification("workspace/delta", _)
                | Notification("workspace/reset", _) -> ()
                | frame -> failwithf "Expected a project-item mutation notification, got %A" frame

                let projectDocument = File.ReadAllText(Path.Combine(directory, "Demo.csproj"))

                projectDocument
                |> should
                    haveSubstring
                    (if useProjectFolder then
                         "Folder/Loose.txt"
                     else
                         "RootLoose.cs")

                projectDocument |> should not' (haveSubstring "ProjectReference"))
