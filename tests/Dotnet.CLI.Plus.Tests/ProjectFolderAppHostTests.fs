namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open FsUnit.Xunit
open Xunit
open Dotnet.CLI.Plus.Transport

type ProjectFolderAppHostTests() =
    [<Fact>]
    member _.``should create an empty project folder with one Folder declaration``() =
        let session =
            PipeTest.openProject
                "folder-new-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            let folder = Path.Combine(session.Directory, "Empty")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.folder.new"
                session.ProjectId
                (PipeTest.map [ "path", RpcValue.String folder ])
                0L
                true

            Directory.Exists folder |> should equal true
            Assert.Contains("<Folder Include=\"Empty/\"", File.ReadAllText(session.Project))
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should copy a complete external folder tree after collision-free preview``() =
        let external = PipeTest.temporaryDirectory "folder-copy-source"
        let nested = Path.Combine(external, "Nested")
        Directory.CreateDirectory nested |> ignore
        File.WriteAllText(Path.Combine(nested, "Source.txt"), "source")

        let session =
            PipeTest.openProject
                "folder-copy-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            let destination = Path.Combine(session.Directory, "Copied")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.folder.copy"
                session.ProjectId
                (PipeTest.map
                    [ "source", RpcValue.String external; "path", RpcValue.String destination ])
                0L
                true

            File.ReadAllText(Path.Combine(destination, "Nested", "Source.txt"))
            |> should equal "source"

            Directory.Exists external |> should equal true
        finally
            PipeTest.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should link an external project folder with the wildcard convention``() =
        let external = PipeTest.temporaryDirectory "folder-link-source"
        File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

        let session =
            PipeTest.openProject
                "folder-link-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            PipeTest.previewAndExecute
                session.Child
                3u
                "project.folder.link"
                session.ProjectId
                (PipeTest.map
                    [ "source", RpcValue.String external
                      "path", RpcValue.String "Linked"
                      "itemType", RpcValue.String "Content" ])
                0L
                true

            let project = File.ReadAllText session.Project
            Assert.Contains($"Include=\"{external.Replace('\\', '/')}/**/*\"", project)
            Assert.Contains("<Link>Linked/%(RecursiveDir)%(Filename)%(Extension)</Link>", project)

            Directory.Exists(Path.Combine(session.Directory, "Linked"))
            |> should equal false
        finally
            PipeTest.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should rename a project folder and preserve descendant declaration metadata``() =
        let session =
            PipeTest.openProjectWithSetup
                "folder-rename-scenario"
                (fun directory ->
                    let folder = Path.Combine(directory, "Old")
                    Directory.CreateDirectory folder |> ignore
                    File.WriteAllText(Path.Combine(folder, "Source.txt"), "source"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                 + "<ItemGroup><Content Include=\"Old\\Source.txt\"><Link>Old/Source.txt</Link></Content></ItemGroup></Project>")

        try
            let source = Path.Combine(session.Directory, "Old")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.folder.rename"
                session.ProjectId
                (PipeTest.map [ "path", RpcValue.String source; "name", RpcValue.String "New" ])
                0L
                true

            File.Exists(Path.Combine(session.Directory, "New", "Source.txt"))
            |> should equal true

            let project = File.ReadAllText session.Project
            Assert.Contains("Include=\"New/Source.txt\"", project)
            Assert.Contains("<Link>New/Source.txt</Link>", project)

            let names = PipeTest.readAllProjectChildNames session 5u 1L

            names
            |> Array.exists (fun name ->
                name.StartsWith("Content: New/Source.txt", StringComparison.Ordinal))
            |> should equal true

            names
            |> Array.exists (fun name ->
                name.Contains(": Old/Source.txt", StringComparison.Ordinal))
            |> should equal false
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should remove a project folder from membership without deleting its tree``() =
        let session =
            PipeTest.openProjectWithSetup
                "folder-remove-scenario"
                (fun directory ->
                    let folder = Path.Combine(directory, "Assets")
                    Directory.CreateDirectory folder |> ignore
                    File.WriteAllText(Path.Combine(folder, "Source.txt"), "source"))
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                 + "<ItemGroup><Content Include=\"Assets/Source.txt\" /></ItemGroup></Project>")

        try
            let folder = Path.Combine(session.Directory, "Assets")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.folder.remove"
                session.ProjectId
                (PipeTest.map [ "path", RpcValue.String folder ])
                0L
                true

            File.Exists(Path.Combine(folder, "Source.txt")) |> should equal true

            Assert.Contains(
                "<Content Remove=\"Assets/Source.txt\"",
                File.ReadAllText session.Project
            )

            let names = PipeTest.readAllProjectChildNames session 5u 1L

            names
            |> Array.exists (fun name ->
                name.Contains(": Assets/Source.txt", StringComparison.Ordinal))
            |> should equal false
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should move a project folder and preserve conditional metadata``() =
        let session =
            PipeTest.openProjectWithSetup
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

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.folder.move"
                session.ProjectId
                (PipeTest.map
                    [ "path", RpcValue.String source; "destination", RpcValue.String destination ])
                0L
                true

            File.Exists(Path.Combine(destination, "Source.txt")) |> should equal true
            let project = File.ReadAllText session.Project
            Assert.Contains("Include=\"Moved/Source.txt\"", project)
            Assert.Contains("Condition=\"'$(Configuration)' == 'Debug'\"", project)
            Assert.Contains("<Link>Moved/Source.txt</Link>", project)

            let names = PipeTest.readAllProjectChildNames session 5u 1L

            names
            |> Array.exists (fun name ->
                name.StartsWith("Content: Moved/Source.txt", StringComparison.Ordinal))
            |> should equal true

            names
            |> Array.exists (fun name ->
                name.Contains(": Old/Source.txt", StringComparison.Ordinal))
            |> should equal false
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should refuse project folder copy collisions and generated destinations``() =
        let external = PipeTest.temporaryDirectory "folder-copy-refusal-source"
        File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

        let session =
            PipeTest.openProject
                "folder-copy-refusal-scenario"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            let collision = Path.Combine(session.Directory, "Collision")
            Directory.CreateDirectory collision |> ignore

            PipeTest.previewFailure
                session
                3u
                "project.folder.copy"
                (PipeTest.map
                    [ "source", RpcValue.String external; "path", RpcValue.String collision ])
                0L

            PipeTest.previewFailure
                session
                5u
                "project.folder.new"
                (PipeTest.map
                    [ "path", RpcValue.String(Path.Combine(session.Directory, ".generated")) ])
                0L

            File.Exists(Path.Combine(external, "Source.txt")) |> should equal true
            Directory.Exists collision |> should equal true
        finally
            PipeTest.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should advertise folder commands only for writable project targets``() =
        let writable =
            PipeTest.openProject
                "folder-command-discovery-full"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        let unknown = PipeTest.openProject "folder-command-discovery-unknown" "<Project />"

        let commands (session: PipeTest.ProjectSession) requestId =
            PipeTest.send
                session.Child
                false
                (PipeTest.request
                    requestId
                    "command/list"
                    (PipeTest.map [ "targetId", RpcValue.String session.ProjectId ]))

            let error, result = PipeTest.readFrame session.Child |> PipeTest.response requestId
            Assert.True error.IsNone

            PipeTest.field "commands" result
            |> RpcValue.requireArray "commands"
            |> Seq.map (PipeTest.field "id")
            |> Seq.toArray

        try
            PipeTest.readAllProjectChildNames writable 3u 0L |> ignore

            commands writable 5u
            |> Array.contains (RpcValue.String "project.folder.new")
            |> should equal true

            commands unknown 3u
            |> Array.contains (RpcValue.String "project.folder.new")
            |> should equal false
        finally
            PipeTest.closeProject writable
            PipeTest.closeProject unknown

    [<Fact>]
    member _.``should refuse terminal and intermediate symbolic folder operands``() =
        if not (OperatingSystem.IsWindows()) then
            let external = PipeTest.temporaryDirectory "folder-symbolic-target"
            File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

            let session =
                PipeTest.openProject
                    "folder-symbolic-scenario"
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

            try
                let terminal = Path.Combine(session.Directory, "Terminal")
                let intermediate = Path.Combine(session.Directory, "Intermediate")
                Directory.CreateSymbolicLink(terminal, external) |> ignore
                Directory.CreateSymbolicLink(intermediate, external) |> ignore

                PipeTest.previewFailure
                    session
                    3u
                    "project.folder.remove"
                    (PipeTest.map [ "path", RpcValue.String terminal ])
                    0L

                PipeTest.previewFailure
                    session
                    5u
                    "project.folder.new"
                    (PipeTest.map [ "path", RpcValue.String(Path.Combine(intermediate, "Child")) ])
                    0L
            finally
                PipeTest.closeProject session
                Directory.Delete(external, true)

    [<Fact>]
    member _.``should delete project folders through the native trash boundary``() =
        let directory = PipeTest.temporaryDirectory "folder-delete-trash-scenario"
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
        PipeTest.save solution model

        use child =
            if OperatingSystem.IsLinux() then
                PipeTest.startPipeWithDataHome "solution" solution (Some trashHome)
            else
                PipeTest.startPipe "solution" solution

        try
            PipeTest.send child false (PipeTest.request 1u "initialize" PipeTest.initialize)
            PipeTest.readFrame child |> PipeTest.response 1u |> ignore
            PipeTest.send child false (PipeTest.request 2u "workspace/root" RpcValue.emptyMap)
            let _, root = PipeTest.readFrame child |> PipeTest.response 2u

            let projectId =
                PipeTest.field "nodes" root
                |> RpcValue.requireArray "nodes"
                |> Seq.find (fun node -> PipeTest.field "kind" node = RpcValue.String "project")
                |> PipeTest.field "id"
                |> RpcValue.requireString "id"

            PipeTest.previewAndExecute
                child
                3u
                "project.folder.delete"
                projectId
                (PipeTest.map [ "path", RpcValue.String deleted ])
                0L
                true

            Directory.Exists deleted |> should equal false
            Assert.Contains("<Content Remove=\"Delete/Source.txt\"", File.ReadAllText project)

            if OperatingSystem.IsLinux() then
                Directory.EnumerateDirectories(Path.Combine(trashHome, "Trash", "files"))
                |> Seq.exactlyOne
                |> Path.GetFileName
                |> should equal "Delete"

            PipeTest.shutdown child 5u
        finally
            PipeTest.disposeProcess child

            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should link an external folder at a nested virtual path without creating directories``
        ()
        =
        let external = PipeTest.temporaryDirectory "nested-virtual-link-source"
        File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

        let session =
            PipeTest.openProject
                "nested-virtual-link"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            PipeTest.previewAndExecute
                session.Child
                3u
                "project.folder.link"
                session.ProjectId
                (PipeTest.map
                    [ "source", RpcValue.String external
                      "path", RpcValue.String "Virtual/Linked"
                      "itemType", RpcValue.String "Content" ])
                0L
                true

            let project = File.ReadAllText session.Project

            Assert.Contains(
                "<Link>Virtual/Linked/%(RecursiveDir)%(Filename)%(Extension)</Link>",
                project
            )

            Directory.Exists(Path.Combine(session.Directory, "Virtual"))
            |> should equal false
        finally
            PipeTest.closeProject session
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should refuse an affected direct macro folder declaration``() =
        let contents =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Content Include=\"$(MSBuildThisFileDirectory)Old/Source.cs\" /></ItemGroup></Project>"

        let session =
            PipeTest.openProjectWithSetup
                "direct-macro-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    Directory.CreateDirectory old |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.cs"), "source"))
                contents

        try
            let old = Path.Combine(session.Directory, "Old")

            PipeTest.previewFailure
                session
                3u
                "project.folder.rename"
                (PipeTest.map [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            Directory.Exists old |> should equal true
            File.ReadAllText session.Project |> should equal contents
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should refuse an affected imported macro folder declaration``() =
        let session =
            PipeTest.openProjectWithSetup
                "imported-macro-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    Directory.CreateDirectory old |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.cs"), "source")

                    File.WriteAllText(
                        Path.Combine(directory, "Shared.props"),
                        "<Project><ItemGroup><Content Include=\"$(MSBuildThisFileDirectory)Old/Source.cs\" /></ItemGroup></Project>"
                    ))
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Shared.props\" /></Project>"

        try
            let old = Path.Combine(session.Directory, "Old")

            PipeTest.previewFailure
                session
                3u
                "project.folder.rename"
                (PipeTest.map [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            Directory.Exists old |> should equal true
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should refuse an imported macro path token owned by a project folder``() =
        let imported =
            "<Project><ItemGroup><Content Include=\"Old/$(File)\" /></ItemGroup></Project>"

        let session =
            PipeTest.openProjectWithSetup
                "imported-macro-path-token-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    Directory.CreateDirectory old |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.cs"), "source")
                    File.WriteAllText(Path.Combine(directory, "Shared.props"), imported))
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Shared.props\" /></Project>"

        try
            let old = Path.Combine(session.Directory, "Old")
            let importedPath = Path.Combine(session.Directory, "Shared.props")

            PipeTest.previewFailure
                session
                3u
                "project.folder.rename"
                (PipeTest.map [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            Directory.Exists old |> should equal true
            Directory.Exists(Path.Combine(session.Directory, "New")) |> should equal false
            File.ReadAllText importedPath |> should equal imported
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should ignore an unrelated macro folder declaration when renaming``() =
        let contents =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<Content Include=\"$(MSBuildThisFileDirectory)Other/Old/Unrelated.cs\" />"
            + "<Content Include=\"Old/Source.cs\" /></ItemGroup></Project>"

        let session =
            PipeTest.openProjectWithSetup
                "unrelated-macro-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    let unrelated = Path.Combine(directory, "Other", "Old")
                    Directory.CreateDirectory old |> ignore
                    Directory.CreateDirectory unrelated |> ignore
                    File.WriteAllText(Path.Combine(old, "Source.cs"), "source")
                    File.WriteAllText(Path.Combine(unrelated, "Unrelated.cs"), "unrelated"))
                contents

        try
            let old = Path.Combine(session.Directory, "Old")
            let renamed = Path.Combine(session.Directory, "New")

            PipeTest.previewAndExecute
                session.Child
                3u
                "project.folder.rename"
                session.ProjectId
                (PipeTest.map [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L
                true

            File.Exists(Path.Combine(renamed, "Source.cs")) |> should equal true

            File.Exists(Path.Combine(session.Directory, "Other", "Old", "Unrelated.cs"))
            |> should equal true

            let project = File.ReadAllText session.Project

            Assert.Contains(
                "Include=\"$(MSBuildThisFileDirectory)Other/Old/Unrelated.cs\"",
                project
            )

            Assert.Contains("Include=\"New/Source.cs\"", project)

            let names = PipeTest.readAllProjectChildNames session 5u 1L

            names
            |> Array.exists (fun name ->
                name.StartsWith("Content: New/Source.cs", StringComparison.Ordinal))
            |> should equal true

            names
            |> Array.exists (fun name -> name.Contains(": Old/Source.cs", StringComparison.Ordinal))
            |> should equal false
        finally
            PipeTest.closeProject session

    [<Fact>]
    member _.``should refuse an affected multi-value folder declaration``() =
        let contents =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Content Include=\"Old/A.cs;Old/B.cs\" /></ItemGroup></Project>"

        let session =
            PipeTest.openProjectWithSetup
                "multi-value-folder"
                (fun directory ->
                    let old = Path.Combine(directory, "Old")
                    Directory.CreateDirectory old |> ignore
                    File.WriteAllText(Path.Combine(old, "A.cs"), "a")
                    File.WriteAllText(Path.Combine(old, "B.cs"), "b"))
                contents

        try
            let old = Path.Combine(session.Directory, "Old")

            PipeTest.previewFailure
                session
                3u
                "project.folder.rename"
                (PipeTest.map [ "path", RpcValue.String old; "name", RpcValue.String "New" ])
                0L

            File.Exists(Path.Combine(old, "A.cs")) |> should equal true
            File.Exists(Path.Combine(old, "B.cs")) |> should equal true
            File.ReadAllText session.Project |> should equal contents
        finally
            PipeTest.closeProject session
