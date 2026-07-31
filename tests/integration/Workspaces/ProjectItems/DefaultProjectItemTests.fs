namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type DefaultProjectItemTests() =
    [<Fact>]
    member _.``should honor Worker default Content items without redundant declarations``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "worker-content-default-scenario"
                (fun directory ->
                    File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{}"))
                ("<Project Sdk=\"Microsoft.NET.Sdk.Worker\"><PropertyGroup>"
                 + "<TargetFramework>net10.0</TargetFramework>"
                 + "</PropertyGroup></Project>")

        try
            let settings = Path.Combine(session.Directory, "appsettings.json")
            let before = File.ReadAllBytes session.Project

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String settings; "itemType", RpcValue.String "Content" ])
                0L
                true

            (File.ReadAllBytes session.Project) |> should equal (before)

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                5u
                "project.item.add"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String settings; "itemType", RpcValue.String "None" ])
                1L
                true

            let project = File.ReadAllText session.Project
            (project) |> should haveSubstring ("<Content Remove=\"appsettings.json\"")
            (project) |> should haveSubstring ("<None Include=\"appsettings.json\"")

            (project)
            |> should not' (haveSubstring ("<Content Include=\"appsettings.json\""))

            let names = WorkspaceRpcScenario.readAllProjectChildNames session 7u 2L

            (names |> Array.filter ((=) "appsettings.json") |> Array.length)
            |> should equal (1)
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should honor Web wwwroot Content defaults and changing build action``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "web-content-default-scenario"
                (fun directory ->
                    let wwwroot = Path.Combine(directory, "wwwroot")
                    Directory.CreateDirectory wwwroot |> ignore
                    File.WriteAllText(Path.Combine(wwwroot, "site.css"), "body {}"))
                ("<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup>"
                 + "<TargetFramework>net10.0</TargetFramework>"
                 + "</PropertyGroup></Project>")

        try
            let site = Path.Combine(session.Directory, "wwwroot", "site.css")
            let before = File.ReadAllBytes session.Project

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String site; "itemType", RpcValue.String "Content" ])
                0L
                true

            (File.ReadAllBytes session.Project) |> should equal (before)

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                5u
                "project.item.set-build-action"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String site; "itemType", RpcValue.String "None" ])
                1L
                true

            let project = File.ReadAllText session.Project
            (project) |> should haveSubstring ("<Content Remove=\"wwwroot/site.css\"")
            (project) |> should haveSubstring ("<None Include=\"wwwroot/site.css\"")

            (project)
            |> should not' (haveSubstring ("<Content Include=\"wwwroot/site.css\""))

            let names = WorkspaceRpcScenario.readAllProjectChildNames session 7u 2L

            (names |> Array.filter ((=) "site.css") |> Array.length) |> should equal (1)
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should keep directory item additions explicit only when needed``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "item-glob-scenario"
                (fun directory ->
                    let included = Path.Combine(directory, "Included")
                    Directory.CreateDirectory included |> ignore
                    File.WriteAllText(Path.Combine(included, "Nested.cs"), "class Nested { }"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                 + "<TargetFramework>net10.0</TargetFramework>"
                 + "<DefaultItemExcludes>$(DefaultItemExcludes);Excluded.cs</DefaultItemExcludes>"
                 + "</PropertyGroup></Project>")

        try
            let before = File.ReadAllBytes session.Project
            let included = Path.Combine(session.Directory, "Included")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String included; "itemType", RpcValue.String "Compile" ])
                0L
                true

            (File.ReadAllBytes session.Project) |> should equal (before)
            let excluded = Path.Combine(session.Directory, "Excluded.cs")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                5u
                "project.item.new"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String excluded
                      "itemType", RpcValue.String "Compile"
                      "contents", RpcValue.String "class Excluded { }" ])
                1L
                true

            (File.ReadAllText session.Project)
            |> should haveSubstring ("<Compile Include=\"Excluded.cs\"")
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``should normalize default directory items when adding a different build action``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "directory-build-action-scenario"
                (fun directory ->
                    let assets = Path.Combine(directory, "Assets")
                    Directory.CreateDirectory assets |> ignore
                    File.WriteAllText(Path.Combine(assets, "Readme.txt"), "readme"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                 + "<TargetFramework>net10.0</TargetFramework>"
                 + "</PropertyGroup></Project>")

        try
            let assets = Path.Combine(session.Directory, "Assets")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.item.add"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String assets; "itemType", RpcValue.String "Content" ])
                0L
                true

            let project = File.ReadAllText session.Project
            (project) |> should haveSubstring ("<None Remove=\"Assets/**/*\"")
            (project) |> should haveSubstring ("<Content Include=\"Assets/**/*\"")

            let names = WorkspaceRpcScenario.readAllProjectChildNames session 5u 1L

            (names |> Array.filter ((=) "Readme.txt") |> Array.length) |> should equal (1)
        finally
            WorkspaceRpcScenario.closeProject session
