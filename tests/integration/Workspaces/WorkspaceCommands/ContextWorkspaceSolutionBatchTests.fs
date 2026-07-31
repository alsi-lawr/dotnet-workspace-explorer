namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Microsoft.VisualStudio.SolutionPersistence.Model
open Xunit

module private ContextWorkspaceSolutionBatchScenario =
    let workspace path =
        match SolutionWorkspaceReader.OpenAsync(path).Result with
        | Success value -> value
        | Failure failure -> failwithf "Expected the solution workspace to open: %A" failure

    let folderContext (folder: SolutionFolder) : WorkspaceSemanticContext =
        { WorkspaceSemanticContext.Node = folder.Node
          ProjectId = None
          ProjectPath = None
          PhysicalPath = None
          PhysicalDirectory = None
          LogicalFolderId = Some folder.Node.Id
          LogicalFolderPath = Some folder.Path }

    let projectContext directory (project: SolutionProject) : WorkspaceSemanticContext =
        { WorkspaceSemanticContext.Node = project.Node
          ProjectId = Some project.Node.Id
          ProjectPath = Some project.Path.AbsolutePath
          PhysicalPath = Some project.Path.AbsolutePath
          PhysicalDirectory = Some(WorkspaceArtifactPath.Create directory)
          LogicalFolderId = None
          LogicalFolderPath = project.ParentFolderPath }

    let itemContext directory (item: SolutionItem) : WorkspaceSemanticContext =
        let path = Path.GetFullPath(item.RelativePath, directory)

        { WorkspaceSemanticContext.Node = item.Node
          ProjectId = None
          ProjectPath = None
          PhysicalPath = Some(WorkspaceArtifactPath.Create path)
          PhysicalDirectory = Some(WorkspaceArtifactPath.Create directory)
          LogicalFolderId = None
          LogicalFolderPath = item.FolderPath }

    let generatedDocument =
        function
        | [| WorkspaceEditAction.ReplaceGeneratedDocument(path, contents) |] -> path, contents
        | actions -> failwithf "Expected one generated solution document, got %A" actions

    let saveGenerated actions =
        let path, contents = generatedDocument actions
        File.WriteAllBytes(path, contents)

    let applyRename actions =
        for action in actions do
            match action with
            | WorkspaceEditAction.Rename(source, destination) -> File.Move(source, destination)
            | WorkspaceEditAction.ReplaceGeneratedDocument(path, contents) ->
                File.WriteAllBytes(path, contents)
            | action -> failwithf "Unexpected solution rename action: %A" action

[<Collection("Workspace scenarios")>]
type ContextWorkspaceSolutionBatchTests() =
    [<Fact>]
    member _.``logical project solution-item and folder moves compose into one authoritative solution document``
        ()
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory "logical-batch-composition"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let projectPath = Path.Combine(directory, "Demo.csproj")
            let itemPath = Path.Combine(directory, "Directory.Build.props")
            File.WriteAllText(projectPath, "<Project />")
            File.WriteAllText(itemPath, "<Project />")

            let model = SolutionModel()
            let projects = model.AddFolder "/Projects/"
            let items = model.AddFolder "/Items/"
            model.AddFolder "/Loose/" |> ignore
            let destination = model.AddFolder "/Destination/"
            model.AddProject("Demo.csproj", "Demo", projects) |> ignore
            items.AddFile "Directory.Build.props" |> ignore
            WorkspaceRpcScenario.save solution model

            let workspace = ContextWorkspaceSolutionBatchScenario.workspace solution

            let target =
                workspace.Contents.Folders
                |> Seq.find (fun folder -> folder.Path = "/Destination/")
                |> ContextWorkspaceSolutionBatchScenario.folderContext

            let sourceProject =
                workspace.Contents.Projects
                |> Seq.exactlyOne
                |> ContextWorkspaceSolutionBatchScenario.projectContext directory

            let sourceItem =
                workspace.Contents.Items
                |> Seq.exactlyOne
                |> ContextWorkspaceSolutionBatchScenario.itemContext directory

            let sourceFolder =
                workspace.Contents.Folders
                |> Seq.find (fun folder -> folder.Path = "/Loose/")
                |> ContextWorkspaceSolutionBatchScenario.folderContext

            let actions, effects =
                match
                    ContextWorkspaceSolutionBatch.solutionPlan
                        workspace
                        target
                        [| sourceProject; sourceItem; sourceFolder |]
                        None
                        CancellationToken.None
                    |> _.Result
                with
                | Ok value -> value
                | Error error -> failwithf "The logical batch did not plan: %s" error.Message

            effects.Length |> should equal 3
            ContextWorkspaceSolutionBatchScenario.saveGenerated actions

            let moved = ContextWorkspaceSolutionBatchScenario.workspace solution

            moved.Contents.Projects
            |> Seq.exactlyOne
            |> _.ParentFolderPath
            |> should equal (Some "/Destination/")

            moved.Contents.Items
            |> Seq.exactlyOne
            |> _.FolderPath
            |> should equal (Some "/Destination/")

            moved.Contents.Folders
            |> Seq.exists (fun folder -> folder.Path = "/Destination/Loose/")
            |> should equal true
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``logical renames preserve solution membership while cycles collisions and unsupported sources reject the complete plan``
        ()
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory "logical-batch-validation"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let projectPath = Path.Combine(directory, "Demo.csproj")
            let occupiedProjectPath = Path.Combine(directory, "Occupied.csproj")
            let itemPath = Path.Combine(directory, "Directory.Build.props")
            File.WriteAllText(projectPath, "<Project />")
            File.WriteAllText(occupiedProjectPath, "<Project />")
            File.WriteAllText(itemPath, "<Project />")

            let model = SolutionModel()
            let source = model.AddFolder "/Source/"
            model.AddFolder "/Source/Nested/" |> ignore
            let destination = model.AddFolder "/Destination/"
            model.AddProject("Demo.csproj", "Demo", source) |> ignore
            source.AddFile "Directory.Build.props" |> ignore
            model.AddBuildType "Debug"
            WorkspaceRpcScenario.save solution model

            let workspace = ContextWorkspaceSolutionBatchScenario.workspace solution

            let sourceFolder =
                workspace.Contents.Folders
                |> Seq.find (fun folder -> folder.Path = "/Source/")
                |> ContextWorkspaceSolutionBatchScenario.folderContext

            let nestedFolder =
                workspace.Contents.Folders
                |> Seq.find (fun folder -> folder.Path = "/Source/Nested/")
                |> ContextWorkspaceSolutionBatchScenario.folderContext

            ContextWorkspacePhysicalBatch.normalizeSources
                [| sourceFolder; sourceFolder; nestedFolder |]
            |> Seq.map _.Node.Id
            |> Seq.toArray
            |> should equal [| sourceFolder.Node.Id |]

            let sourceItem =
                workspace.Contents.Items
                |> Seq.exactlyOne
                |> ContextWorkspaceSolutionBatchScenario.itemContext directory

            let itemRenameActions, itemRenameEffects =
                match
                    ContextWorkspaceSolutionBatch.solutionPlan
                        workspace
                        sourceItem
                        [| sourceItem |]
                        (Some "Renamed.props")
                        CancellationToken.None
                    |> _.Result
                with
                | Ok value -> value
                | Error error -> failwithf "The solution-item rename did not plan: %s" error.Message

            itemRenameEffects.Length |> should equal 2
            ContextWorkspaceSolutionBatchScenario.applyRename itemRenameActions
            File.Exists(Path.Combine(directory, "Renamed.props")) |> should equal true

            let afterItemRename = ContextWorkspaceSolutionBatchScenario.workspace solution

            afterItemRename.Contents.Items
            |> Seq.exactlyOne
            |> _.Node.Name
            |> should equal "Renamed.props"

            let currentSourceFolder =
                afterItemRename.Contents.Folders
                |> Seq.find (fun folder -> folder.Path = "/Source/")
                |> ContextWorkspaceSolutionBatchScenario.folderContext

            let renamedActions, _ =
                match
                    ContextWorkspaceSolutionBatch.solutionPlan
                        afterItemRename
                        currentSourceFolder
                        [| currentSourceFolder |]
                        (Some "Renamed")
                        CancellationToken.None
                    |> _.Result
                with
                | Ok value -> value
                | Error error ->
                    failwithf "The solution-folder rename did not plan: %s" error.Message

            ContextWorkspaceSolutionBatchScenario.saveGenerated renamedActions

            let renamed = ContextWorkspaceSolutionBatchScenario.workspace solution

            renamed.Contents.Folders
            |> Seq.exists (fun folder -> folder.Path = "/Renamed/")
            |> should equal true

            let original = ContextWorkspaceSolutionBatchScenario.workspace solution

            let renamedFolder =
                original.Contents.Folders
                |> Seq.find (fun folder -> folder.Path = "/Renamed/")
                |> ContextWorkspaceSolutionBatchScenario.folderContext

            let unsupported: WorkspaceSemanticContext =
                { renamedFolder with
                    Node =
                        original.Contents.Nodes
                        |> Seq.find (fun node -> node.Kind = WorkspaceNodeKind.Configuration) }

            let expectInvalid
                (outcome:
                    Result<
                        WorkspaceEditAction array * WorkspaceCommandEffect array,
                        Dotnet.WorkspaceExplorer.Rpc.RpcError
                     >)
                =
                match outcome with
                | Error error -> error.Code |> should equal "invalid_params"
                | Ok value -> failwithf "An invalid logical batch unexpectedly planned: %A" value

            ContextWorkspaceSolutionBatch.solutionPlan
                original
                renamedFolder
                [| renamedFolder |]
                None
                CancellationToken.None
            |> _.Result
            |> expectInvalid

            ContextWorkspaceSolutionBatch.solutionPlan
                original
                renamedFolder
                [| unsupported |]
                None
                CancellationToken.None
            |> _.Result
            |> expectInvalid

            let project =
                original.Contents.Projects
                |> Seq.exactlyOne
                |> ContextWorkspaceSolutionBatchScenario.projectContext directory

            ContextWorkspaceSolutionBatch.solutionPlan
                original
                project
                [| project |]
                (Some "Occupied")
                CancellationToken.None
            |> _.Result
            |> expectInvalid
        finally
            Directory.Delete(directory, true)
