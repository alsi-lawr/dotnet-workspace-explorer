namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

module private WorkspaceFileResolutionScenario =
    type Context =
        { Child: System.Diagnostics.Process
          RootNodeId: string
          ProjectNodeId: string
          ProjectPath: string
          SolutionItemId: string
          SolutionItemPath: string }

    let private children child requestId parentNodeId =
        let parameters =
            WorkspaceRpcScenario.map
                [ "parentNodeId", RpcValue.String parentNodeId
                  "pageSize", RpcValue.Integer 50L ]

        WorkspaceRpcScenario.send
            child
            false
            (WorkspaceRpcScenario.request requestId "workspace/children" parameters)

        let error, result =
            WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response requestId

        error |> should equal None
        result

    let runWithProject extension projectExtension action =
        let directory = WorkspaceRpcScenario.temporaryDirectory "file-resolution"

        try
            let solution = Path.Combine(directory, "Demo" + extension)
            let solutionItem = Path.Combine(directory, "Directory.Build.props")
            let model = SolutionModel()
            let projectPath = Path.Combine(directory, "Demo" + projectExtension)
            model.AddProject(Path.GetFileName projectPath, "Demo", null) |> ignore
            let solutionFolder = model.AddFolder "/Solution Items/"
            solutionFolder.AddFile(Path.GetFileName solutionItem) |> ignore
            WorkspaceRpcScenario.writeProject projectPath
            File.WriteAllText(solutionItem, "<Project />")
            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "file-resolution" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let rootError, root =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                rootError |> should equal None

                let rootNodeId =
                    WorkspaceRpcScenario.field "nodes" root
                    |> RpcValue.requireArray "nodes"
                    |> Seq.exactlyOne
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                let solutionFolderId =
                    let rootChildren =
                        children child 3u rootNodeId
                        |> WorkspaceRpcScenario.field "nodes"
                        |> RpcValue.requireArray "nodes"

                    rootChildren
                    |> Seq.find (fun node ->
                        let name = WorkspaceRpcScenario.field "name" node
                        name = RpcValue.String "Solution Items")
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                let projectNodeId =
                    children child 5u rootNodeId
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        WorkspaceRpcScenario.field "name" node = RpcValue.String "Demo")
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                let solutionItemId =
                    children child 4u solutionFolderId
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.exactlyOne
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                action
                    { Child = child
                      RootNodeId = rootNodeId
                      ProjectNodeId = projectNodeId
                      ProjectPath = projectPath
                      SolutionItemId = solutionItemId
                      SolutionItemPath = solutionItem }

                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    let run extension action =
        runWithProject extension ".fsproj" action

    let runWithProjectFile action =
        let directory = WorkspaceRpcScenario.temporaryDirectory "project-file-resolution"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let projectPath = Path.Combine(directory, "Demo.csproj")
            let sourceDirectory = Path.Combine(directory, "Source")
            let sourcePath = Path.Combine(sourceDirectory, "ExactCase.cs")
            let model = SolutionModel()
            model.AddProject(Path.GetFileName projectPath, "Demo", null) |> ignore
            Directory.CreateDirectory(sourceDirectory) |> ignore
            WorkspaceRpcScenario.writeProject projectPath
            File.WriteAllText(sourcePath, "class ExactCase {}")
            WorkspaceRpcScenario.save solution model

            use child =
                WorkspaceRpcScenario.startWorkspaceRpc "project-file-resolution" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let rootError, root =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                rootError |> should equal None

                let projectNodeId =
                    children
                        child
                        3u
                        (root
                         |> WorkspaceRpcScenario.field "nodes"
                         |> RpcValue.requireArray "nodes"
                         |> Seq.exactlyOne
                         |> WorkspaceRpcScenario.field "id"
                         |> RpcValue.requireString "id")
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        WorkspaceRpcScenario.field "name" node = RpcValue.String "Demo")
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        4u
                        "workspace/children"
                        (WorkspaceRpcScenario.map
                            [ "parentNodeId", RpcValue.String projectNodeId
                              "pageSize", RpcValue.Integer 50L ]))

                let (projectChildrenError, projectChildren), _, notifications =
                    WorkspaceRpcScenario.responseAfterWorkspaceNotifications child 4u 0L

                projectChildrenError |> should equal None

                if notifications.IsEmpty then
                    match WorkspaceRpcScenario.readFrame child with
                    | Notification("workspace/delta", _) -> ()
                    | frame -> failwithf "Expected the project hydration delta, got %A" frame

                let revision =
                    projectChildren
                    |> WorkspaceRpcScenario.field "revision"
                    |> RpcValue.requireInteger "revision"

                let sourceFolderId =
                    projectChildren
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        WorkspaceRpcScenario.field "name" node = RpcValue.String "Source")
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                let sourceNodeId =
                    children child 5u sourceFolderId
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.exactlyOne
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                action child sourceNodeId sourcePath revision
                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    let resolve child requestId targetNodeId expectedRevision =
        let parameters =
            WorkspaceRpcScenario.map
                [ "targetNodeId", RpcValue.String targetNodeId
                  "expectedRevision", RpcValue.Integer expectedRevision ]

        WorkspaceRpcScenario.send
            child
            false
            (WorkspaceRpcScenario.request requestId "workspace/file/resolve" parameters)

        WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response requestId

[<Collection("Workspace scenarios")>]
type WorkspaceFileResolutionTests() =
    [<Theory>]
    [<InlineData(".csproj")>]
    [<InlineData(".fsproj")>]
    [<InlineData(".vbproj")>]
    member _.``resolving a CSharp FSharp or VisualBasic project returns its exact project file``
        (projectExtension: string)
        =
        WorkspaceFileResolutionScenario.runWithProject ".slnx" projectExtension (fun context ->
            let error, result =
                WorkspaceFileResolutionScenario.resolve context.Child 10u context.ProjectNodeId 0L

            error |> should equal None
            let fields = RpcValue.requireMap "file.resolve.result" result
            fields["targetNodeId"] |> should equal (RpcValue.String context.ProjectNodeId)
            fields["path"] |> should equal (RpcValue.String context.ProjectPath)
            fields["revision"] |> RpcValue.requireInteger "revision" |> should equal 0L)

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``resolving an existing solution item returns its core-owned absolute path and current revision``
        (extension: string)
        =
        WorkspaceFileResolutionScenario.run extension (fun context ->
            let error, result =
                WorkspaceFileResolutionScenario.resolve context.Child 10u context.SolutionItemId 0L

            error |> should equal None
            let fields = RpcValue.requireMap "file.resolve.result" result

            fields.Keys
            |> Seq.sort
            |> Seq.toList
            |> should equal [ "path"; "revision"; "targetNodeId" ]

            fields["targetNodeId"] |> should equal (RpcValue.String context.SolutionItemId)
            fields["path"] |> should equal (RpcValue.String context.SolutionItemPath)
            fields["revision"] |> RpcValue.requireInteger "revision" |> should equal 0L)

    [<Fact>]
    member _.``resolving a project file preserves its evaluated physical path casing``() =
        WorkspaceFileResolutionScenario.runWithProjectFile (fun child nodeId sourcePath revision ->
            let error, result =
                WorkspaceFileResolutionScenario.resolve child 10u nodeId revision

            error |> should equal None
            let fields = RpcValue.requireMap "file.resolve.result" result
            fields["targetNodeId"] |> should equal (RpcValue.String nodeId)
            fields["path"] |> should equal (RpcValue.String sourcePath)

            fields["revision"]
            |> RpcValue.requireInteger "revision"
            |> should equal revision)

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``resolving a non-file workspace node returns an invalid-params error``
        (extension: string)
        =
        WorkspaceFileResolutionScenario.run extension (fun context ->
            let error, _ =
                WorkspaceFileResolutionScenario.resolve context.Child 10u context.RootNodeId 0L

            error |> Option.map _.Code |> should equal (Some "invalid_params"))

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``resolving a file with a stale workspace revision returns a workspace-conflict error``
        (extension: string)
        =
        WorkspaceFileResolutionScenario.run extension (fun context ->
            let error, _ =
                WorkspaceFileResolutionScenario.resolve context.Child 10u context.SolutionItemId 1L

            error |> Option.map _.Code |> should equal (Some "workspace_conflict"))
