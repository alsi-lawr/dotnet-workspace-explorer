namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Project-folder scenarios")>]
type ProjectFolderDeletionTests() =
    [<Fact>]
    member _.``should delete project folders through the native trash boundary``() =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "folder-delete-trash-scenario"

        let trashHome = Path.Combine(directory, "data")
        let solution = Path.Combine(directory, "Demo.slnx")
        let project = Path.Combine(directory, "Demo.csproj")
        let deleted = Path.Combine(directory, "Delete")
        let model = SolutionModel()
        model.AddProject("Demo.csproj", "Demo", null) |> ignore
        Directory.CreateDirectory deleted |> ignore
        File.WriteAllText(Path.Combine(deleted, "Source.txt"), "delete")

        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Content Include=\"Delete/Source.txt\" /></ItemGroup></Project>"
        )

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
                "project.folder.delete"
                projectId
                (WorkspaceRpcScenario.map [ "path", RpcValue.String deleted ])
                0L
                true

            Directory.Exists deleted |> should equal false

            (File.ReadAllText project)
            |> should haveSubstring ("<Content Remove=\"Delete/Source.txt\"")

            if OperatingSystem.IsLinux() then
                Directory.EnumerateDirectories(Path.Combine(trashHome, "Trash", "files"))
                |> Seq.exactlyOne
                |> Path.GetFileName
                |> should equal "Delete"

            WorkspaceRpcScenario.shutdown child 5u
        finally
            WorkspaceRpcScenario.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)
