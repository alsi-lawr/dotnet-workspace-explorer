namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Project-folder scenarios")>]
type ProjectFolderLifecycleTests() =
    [<Fact>]
    member _.``creates an empty project folder and persists one Folder declaration``() =
        let session =
            WorkspaceRpcScenario.openProject
                "folder-new-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            let folder = Path.Combine(session.Directory, "Empty")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.new"
                session.ProjectId
                (WorkspaceRpcScenario.map [ "path", RpcValue.String folder ])
                0L
                true

            Directory.Exists folder |> should equal true

            (File.ReadAllText session.Project)
            |> should haveSubstring ("<Folder Include=\"Empty/\"")
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``copies a complete external folder tree after collision-free preview``() =
        let external = WorkspaceRpcScenario.temporaryDirectory "folder-copy-source"
        let nested = Path.Combine(external, "Nested")
        Directory.CreateDirectory nested |> ignore
        File.WriteAllText(Path.Combine(nested, "Source.txt"), "source")

        let session =
            WorkspaceRpcScenario.openProject
                "folder-copy-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            let destination = Path.Combine(session.Directory, "Copied")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.copy"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "source", RpcValue.String external; "path", RpcValue.String destination ])
                0L
                true

            File.ReadAllText(Path.Combine(destination, "Nested", "Source.txt"))
            |> should equal "source"

            Directory.Exists external |> should equal true
        finally
            WorkspaceRpcScenario.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``links an external project folder using the wildcard convention``() =
        let external = WorkspaceRpcScenario.temporaryDirectory "folder-link-source"
        File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

        let session =
            WorkspaceRpcScenario.openProject
                "folder-link-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.link"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "source", RpcValue.String external
                      "path", RpcValue.String "Linked"
                      "itemType", RpcValue.String "Content" ])
                0L
                true

            let project = File.ReadAllText session.Project

            (project)
            |> should haveSubstring ($"Include=\"{external.Replace('\\', '/')}/**/*\"")

            (project)
            |> should haveSubstring ("<Link>Linked/%(RecursiveDir)%(Filename)%(Extension)</Link>")

            Directory.Exists(Path.Combine(session.Directory, "Linked"))
            |> should equal false
        finally
            WorkspaceRpcScenario.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``renames a project folder while preserving descendant declaration metadata``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "folder-rename-scenario"
                (fun directory ->
                    let folder = Path.Combine(directory, "Old")
                    Directory.CreateDirectory folder |> ignore
                    File.WriteAllText(Path.Combine(folder, "Source.txt"), "source"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                 + "<ItemGroup><Content Include=\"Old\\Source.txt\"><Link>Old/Source.txt</Link></Content></ItemGroup></Project>")

        try
            let source = Path.Combine(session.Directory, "Old")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.rename"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String source; "name", RpcValue.String "New" ])
                0L
                true

            File.Exists(Path.Combine(session.Directory, "New", "Source.txt"))
            |> should equal true

            let project = File.ReadAllText session.Project
            (project) |> should haveSubstring ("Include=\"New/Source.txt\"")
            (project) |> should haveSubstring ("<Link>New/Source.txt</Link>")

            let names = WorkspaceRpcScenario.readAllProjectChildNames session 5u 1L

            names |> Array.exists ((=) "Source.txt") |> should equal true
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``removes a project folder from membership while preserving its directory tree``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "folder-remove-scenario"
                (fun directory ->
                    let folder = Path.Combine(directory, "Assets")
                    Directory.CreateDirectory folder |> ignore
                    File.WriteAllText(Path.Combine(folder, "Source.txt"), "source"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                 + "<ItemGroup><Content Include=\"Assets/Source.txt\" /></ItemGroup></Project>")

        try
            let folder = Path.Combine(session.Directory, "Assets")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.remove"
                session.ProjectId
                (WorkspaceRpcScenario.map [ "path", RpcValue.String folder ])
                0L
                true

            File.Exists(Path.Combine(folder, "Source.txt")) |> should equal true

            (File.ReadAllText session.Project)
            |> should haveSubstring ("<Content Remove=\"Assets/Source.txt\"")

            let names = WorkspaceRpcScenario.readAllProjectChildNames session 5u 1L

            names
            |> Array.exists (fun name ->
                name.Contains(": Assets/Source.txt", StringComparison.Ordinal))
            |> should equal false
        finally
            WorkspaceRpcScenario.closeProject session

    [<Fact>]
    member _.``moves a project folder while preserving conditional metadata``() =
        let session =
            WorkspaceRpcScenario.openProjectWithSetup
                "folder-move-scenario"
                (fun directory ->
                    let folder = Path.Combine(directory, "Old")
                    Directory.CreateDirectory folder |> ignore
                    File.WriteAllText(Path.Combine(folder, "Source.txt"), "source"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                 + "<ItemGroup><Content Include=\"Old/Source.txt\" Condition=\"'$(Configuration)' == 'Debug'\">"
                 + "<Link>Old/Source.txt</Link></Content></ItemGroup></Project>")

        try
            let source = Path.Combine(session.Directory, "Old")
            let destination = Path.Combine(session.Directory, "Moved")

            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.move"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "path", RpcValue.String source; "destination", RpcValue.String destination ])
                0L
                true

            File.Exists(Path.Combine(destination, "Source.txt")) |> should equal true
            let project = File.ReadAllText session.Project
            (project) |> should haveSubstring ("Include=\"Moved/Source.txt\"")

            (project)
            |> should haveSubstring ("Condition=\"'$(Configuration)' == 'Debug'\"")

            (project) |> should haveSubstring ("<Link>Moved/Source.txt</Link>")

            let names = WorkspaceRpcScenario.readAllProjectChildNames session 5u 1L

            names |> Array.exists ((=) "Source.txt") |> should equal true
        finally
            WorkspaceRpcScenario.closeProject session
