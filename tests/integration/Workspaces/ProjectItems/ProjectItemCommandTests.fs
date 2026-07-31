namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open System.Text
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type ProjectItemCommandTests() =
    [<Fact>]
    member _.``should copy or link external project files without local directory operands``() =
        let external = WorkspaceRpcScenario.temporaryDirectory "external-item-scenario"
        let source = Path.Combine(external, "Source.txt")
        let link = Path.Combine(external, "Link.txt")
        File.WriteAllText(source, "copy")
        File.WriteAllText(link, "link")

        let session =
            WorkspaceRpcScenario.openProject
                "external-item-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        try
            let addArguments =
                WorkspaceRpcScenario.map
                    [ "path", RpcValue.String source; "itemType", RpcValue.String "None" ]

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                addArguments
                0L
                true

            (File.ReadAllText(Path.Combine(session.Directory, "Source.txt")))
            |> should equal ("copy")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                5u
                "project.item.add"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String link
                      "itemType", RpcValue.String "Content"
                      "link", RpcValue.Boolean true ])
                1L
                true

            (File.Exists(Path.Combine(session.Directory, "Link.txt"))) |> should equal false

            (File.ReadAllText session.Project)
            |> should haveSubstring ("<Link>Link.txt</Link>")

            WorkspaceRpcScenario.previewFailure
                session
                7u
                "project.item.add"
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String source; "itemType", RpcValue.String "Content" ])
                2L
        finally
            WorkspaceRpcScenario.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should set metadata and build action through public project commands``() =
        let session =
            WorkspaceRpcScenario.openProject
                "metadata-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        try
            let source = Path.Combine(session.Directory, "Source.cs")
            File.WriteAllText(source, "class Source { }")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.item.set-metadata"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String source
                      "name", RpcValue.String "CopyToOutputDirectory"
                      "value", RpcValue.String "Always" ])
                0L
                true

            (File.ReadAllText session.Project)
            |> should haveSubstring ("<Compile Update=\"Source.cs\"")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                5u
                "project.item.set-build-action"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String source; "itemType", RpcValue.String "Content" ])
                1L
                true

            (File.ReadAllText session.Project)
            |> should haveSubstring ("<Content Include=\"Source.cs\"")
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should refuse directory operands for file project commands``() =
        let session =
            WorkspaceRpcScenario.openProject
                "directory-refusal-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        try
            let folder = Path.Combine(session.Directory, "Folder")
            Directory.CreateDirectory folder |> ignore

            [ "project.item.new",
              WorkspaceRpcScenario.map
                  [ "path", RpcValue.String folder; "itemType", RpcValue.String "Compile" ]
              "project.item.copy",
              WorkspaceRpcScenario.map
                  [ "source", RpcValue.String folder
                    "path", RpcValue.String(Path.Combine(session.Directory, "Copy.cs"))
                    "itemType", RpcValue.String "Compile" ]
              "project.item.add",
              WorkspaceRpcScenario.map
                  [ "path", RpcValue.String folder
                    "itemType", RpcValue.String "Compile"
                    "link", RpcValue.Boolean true ]
              "project.item.rename",
              WorkspaceRpcScenario.map
                  [ "path", RpcValue.String folder; "name", RpcValue.String "Renamed" ]
              "project.item.move",
              WorkspaceRpcScenario.map
                  [ "path", RpcValue.String folder; "destination", RpcValue.String folder ]
              "project.item.remove", WorkspaceRpcScenario.map [ "path", RpcValue.String folder ]
              "project.item.delete", WorkspaceRpcScenario.map [ "path", RpcValue.String folder ] ]
            |> List.iteri (fun index (command, arguments) ->
                WorkspaceRpcScenario.previewFailure
                    session
                    (uint32 (3 + index))
                    command
                    arguments
                    0L)
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should write a local curated property``() =
        let session =
            WorkspaceRpcScenario.openProject
                "local-property-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        try
            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.property.set"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "name", RpcValue.String "RootNamespace"
                      "value", RpcValue.String "Demo.Root" ])
                0L
                true

            (File.ReadAllText session.Project)
            |> should haveSubstring ("<RootNamespace>Demo.Root</RootNamespace>")
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should rename move and remove file project items without directory mutation``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "rename-move-scenario"
                (fun directory -> File.WriteAllText(Path.Combine(directory, "Move.txt"), "move"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
                 + "<Content Include=\"Move.txt\" /></ItemGroup></Project>")

        try
            let source = Path.Combine(session.Directory, "Move.txt")
            let renamed = Path.Combine(session.Directory, "Renamed.txt")
            let moved = Path.Combine(session.Directory, "Moved.txt")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.item.rename"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String source; "name", RpcValue.String "Renamed.txt" ])
                0L
                true

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                5u
                "project.item.move"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String renamed; "destination", RpcValue.String moved ])
                1L
                true

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                7u
                "project.item.remove"
                session.ProjectId
                (WorkspaceRpcScenario.map [ "path", RpcValue.String moved ])
                2L
                true

            (File.Exists moved) |> should equal true

            (File.ReadAllText session.Project)
            |> should haveSubstring ("<None Remove=\"Moved.txt\"")

            (File.ReadAllText session.Project)
            |> should not' (haveSubstring ("<None Include=\"Moved.txt\""))
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should preserve external encoded imported property files``() =
        let external = WorkspaceRpcScenario.temporaryDirectory "encoded-property-scenario"
        let props = Path.Combine(external, "Shared.props")
        let encoding = Encoding.GetEncoding 28591

        File.WriteAllBytes(
            props,
            encoding.GetBytes(
                "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\r\n"
                + "<Project>\r\n  <!-- café shared -->\r\n"
                + "  <PropertyGroup Condition=\"'$(MSBuildProjectName)' == 'Demo'\">"
                + "<AssemblyName>Café</AssemblyName></PropertyGroup>\r\n"
                + "</Project>\r\n"
            )
        )

        let session =
            WorkspaceRpcScenario.openProject
                "encoded-property-scenario"
                ($"<Project Sdk=\"Microsoft.NET.Sdk\">"
                 + $"<Import Project=\"{props.Replace('\\', '/')}\" /></Project>")

        try
            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.property.set"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "name", RpcValue.String "AssemblyName"
                      "value", RpcValue.String "After"
                      "scope", RpcValue.String props
                      "condition", RpcValue.String "'$(MSBuildProjectName)' == 'Demo'" ])
                0L
                true

            let contents = File.ReadAllText(props, encoding)
            (contents) |> should haveSubstring ("encoding=\"iso-8859-1\"")
            (contents) |> should haveSubstring ("<!-- café shared -->")
            (contents) |> should haveSubstring ("\r\n")

            (contents)
            |> should haveSubstring ("Condition=\"'$(MSBuildProjectName)' == 'Demo'\"")

            (contents) |> should haveSubstring ("<AssemblyName>After</AssemblyName>")

            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request
                    5u
                    "workspace/children"
                    (WorkspaceRpcScenario.map
                        [ "parentNodeId", RpcValue.String session.ProjectId
                          "pageSize", RpcValue.Integer 100L ]))

            let (childrenError, children), _, _ =
                WorkspaceRpcScenario.responseAfterWorkspaceNotifications session.Child 5u 1L

            (childrenError.IsNone) |> should equal true

            let names = ResizeArray<string>()

            let appendNames page =
                WorkspaceRpcScenario.field "nodes" page
                |> RpcValue.requireArray "nodes"
                |> Seq.iter (fun node ->
                    names.Add(
                        WorkspaceRpcScenario.field "name" node |> RpcValue.requireString "name"
                    ))

            appendNames children

            let mutable continuation =
                match RpcValue.tryField "nextToken" children with
                | Some(RpcValue.String token) -> Some token
                | Some RpcValue.Nil
                | None -> None
                | Some value -> failwithf "Unexpected continuation token: %A" value

            let mutable requestId = 6u

            while continuation.IsSome do
                WorkspaceRpcScenario.send
                    session.Child
                    false
                    (WorkspaceRpcScenario.request
                        requestId
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String session.ProjectId
                              "pageSize", RpcValue.Integer 100L
                              "continuationToken", RpcValue.String continuation.Value ]))

                let (pageError, page), _, _ =
                    WorkspaceRpcScenario.responseAfterWorkspaceNotifications
                        session.Child
                        requestId
                        1L

                (pageError.IsNone) |> should equal true
                appendNames page

                continuation <-
                    match RpcValue.tryField "nextToken" page with
                    | Some(RpcValue.String token) -> Some token
                    | Some RpcValue.Nil
                    | None -> None
                    | Some value -> failwithf "Unexpected continuation token: %A" value

                requestId <- requestId + 1u

            (names)
            |> Seq.exists (fun name ->
                name.StartsWith("Evaluated ", StringComparison.Ordinal)
                || name.StartsWith("Declared ", StringComparison.Ordinal))
            |> should equal false
        finally
            WorkspaceRpcScenario.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should delete project files through the native trash boundary``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "delete-trash-scenario"
        let trashHome = Path.Combine(directory, "data")
        let solution = Path.Combine(directory, "Demo.slnx")
        let project = Path.Combine(directory, "Demo.csproj")
        let deleted = Path.Combine(directory, "Delete.txt")
        let model = SolutionModel()
        model.AddProject("Demo.csproj", "Demo", null) |> ignore
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(deleted, "delete")
        Directory.CreateDirectory trashHome |> ignore
        WorkspaceRpcScenario.save solution model

        use child =
            if OperatingSystem.IsLinux() then
                WorkspaceRpcScenario.startPipeWithDataHome "solution" solution (Some trashHome)
            else
                WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

        try
            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

            WorkspaceRpcScenario.readFrame child
            |> WorkspaceRpcScenario.response 1u
            |> ignore

            WorkspaceRpcScenario.send
                child
                false
                (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

            let _, root =
                WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

            let rootChildren = WorkspaceRpcScenario.rootChildren child 20u root

            let projectId =
                WorkspaceRpcScenario.field "nodes" rootChildren
                |> RpcValue.requireArray "nodes"
                |> Seq.find (fun node ->
                    WorkspaceRpcScenario.field "kind" node = RpcValue.String "project")
                |> WorkspaceRpcScenario.field "id"
                |> RpcValue.requireString "id"

            WorkspaceRpcScenario.previewAndExecute
                child
                3u
                "project.item.delete"
                projectId
                (WorkspaceRpcScenario.map [ "path", RpcValue.String deleted ])
                0L
                true

            (File.Exists deleted) |> should equal false

            (File.ReadAllText project)
            |> should haveSubstring ("<None Remove=\"Delete.txt\"")

            if OperatingSystem.IsLinux() then
                let trashed =
                    Directory.EnumerateFiles(Path.Combine(trashHome, "Trash", "files"))
                    |> Seq.exactlyOne

                (File.ReadAllText trashed) |> should equal ("delete")

            WorkspaceRpcScenario.shutdown child 5u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)
