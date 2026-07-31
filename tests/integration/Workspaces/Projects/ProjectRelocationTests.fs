namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace-command scenarios")>]
type ProjectRelocationTests() =
    [<Fact>]
    member _.``should move a project tree through one completed public operation``() =
        let session =
            WorkspaceCommandScenario.start "physical-project-move" (fun directory model ->
                let source = Path.Combine(directory, "src", "One")
                let incoming = Path.Combine(directory, "src", "Ref")
                Directory.CreateDirectory(Path.Combine(source, "nested")) |> ignore
                Directory.CreateDirectory incoming |> ignore
                Directory.CreateDirectory(Path.Combine(directory, "moved")) |> ignore
                File.WriteAllText(Path.Combine(source, "nested", "keep.txt"), "keep")

                File.WriteAllText(
                    Path.Combine(source, "One.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(incoming, "Ref.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../One/One.fsproj\" Condition=\"'$(Configuration)' == 'Debug'\" /></ItemGroup><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                model.AddFolder "/moved/" |> ignore
                model.AddProject("src/One/One.fsproj", null, null) |> ignore
                model.AddProject("src/Ref/Ref.fsproj", null, null) |> ignore)

        try
            let completion =
                WorkspaceCommandScenario.execute
                    session
                    3u
                    "project.relocate"
                    session.ProjectId
                    (WorkspaceCommandScenario.argumentMap
                        [ "destination", RpcValue.String "moved/One"
                          "folder", RpcValue.String session.FolderId.Value ])
                    0L

            completion.Outcome |> should equal "succeeded"

            completion.Notifications
            |> should equal [ "workspace/operations/progress"; "workspace/operations/completed" ]

            completion.WorkspaceNotifications |> should contain "workspace/delta"

            Directory.Exists(Path.Combine(session.Directory, "src", "One"))
            |> should equal false

            File.Exists(Path.Combine(session.Directory, "moved", "One", "One.fsproj"))
            |> should equal true

            File.ReadAllText(Path.Combine(session.Directory, "moved", "One", "nested", "keep.txt"))
            |> should equal "keep"

            File.ReadAllText(Path.Combine(session.Directory, "src", "Ref", "Ref.fsproj"))
            |> fun contents -> contents.Contains "moved/One/One.fsproj"
            |> should equal true

            File.ReadAllText(Path.Combine(session.Directory, "src", "Ref", "Ref.fsproj"))
            |> fun contents -> contents.Contains "Condition=\"'$(Configuration)' == 'Debug'\""
            |> should equal true

            WorkspaceCommandScenario.openSolution session.Solution
            |> fun reopened ->
                reopened.SolutionProjects
                |> Seq.find (fun project ->
                    project.FilePath.Replace('\\', '/') = "moved/One/One.fsproj")
                |> fun project -> project.Parent.Path
                |> should equal "/moved/"
        finally
            WorkspaceCommandScenario.stop session

    [<Fact>]
    member _.``should refuse a relocation when any direct project reference uses a macro``() =
        let session =
            WorkspaceCommandScenario.start "physical-project-move-macro" (fun directory model ->
                let source = Path.Combine(directory, "src", "One")
                let incoming = Path.Combine(directory, "src", "Ref")
                Directory.CreateDirectory source |> ignore
                Directory.CreateDirectory incoming |> ignore
                Directory.CreateDirectory(Path.Combine(directory, "moved")) |> ignore

                File.WriteAllText(
                    Path.Combine(source, "One.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(incoming, "Ref.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"$(MSBuildProjectDirectory)/NoSuch.fsproj\" Condition=\"'$(Configuration)' == 'Never'\" /></ItemGroup><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                model.AddProject("src/One/One.fsproj", null, null) |> ignore
                model.AddProject("src/Ref/Ref.fsproj", null, null) |> ignore)

        try
            let error =
                try
                    WorkspaceCommandScenario.beginMutation
                        session
                        3u
                        "project.relocate"
                        session.ProjectId
                        (WorkspaceCommandScenario.argumentMap
                            [ "destination", RpcValue.String "moved/One" ])
                        0L
                    |> ignore

                    failwith "The relocation unexpectedly succeeded."
                with error ->
                    error

            error.Message |> should haveSubstring "macro"

            Directory.Exists(Path.Combine(session.Directory, "src", "One"))
            |> should equal true

            Directory.Exists(Path.Combine(session.Directory, "moved", "One"))
            |> should equal false
        finally
            WorkspaceCommandScenario.stop session

    [<Fact>]
    member _.``should refuse a relocation when an import declares an inactive project reference``
        ()
        =
        let session =
            WorkspaceCommandScenario.start "physical-project-move-import" (fun directory model ->
                let source = Path.Combine(directory, "src", "One")
                let incoming = Path.Combine(directory, "src", "Ref")
                Directory.CreateDirectory source |> ignore
                Directory.CreateDirectory incoming |> ignore
                Directory.CreateDirectory(Path.Combine(directory, "moved")) |> ignore

                File.WriteAllText(
                    Path.Combine(source, "One.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(incoming, "Ref.props"),
                    "<Project><ItemGroup><ProjectReference Include=\"../One/One.fsproj\" Condition=\"'$(Configuration)' == 'Never'\" /></ItemGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(incoming, "Ref.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Ref.props\" /><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                )

                model.AddProject("src/One/One.fsproj", null, null) |> ignore
                model.AddProject("src/Ref/Ref.fsproj", null, null) |> ignore)

        try
            let error =
                try
                    WorkspaceCommandScenario.beginMutation
                        session
                        3u
                        "project.relocate"
                        session.ProjectId
                        (WorkspaceCommandScenario.argumentMap
                            [ "destination", RpcValue.String "moved/One" ])
                        0L
                    |> ignore

                    failwith "The relocation unexpectedly succeeded."
                with error ->
                    error

            error.Message |> should haveSubstring "declared by an import"

            Directory.Exists(Path.Combine(session.Directory, "src", "One"))
            |> should equal true

            Directory.Exists(Path.Combine(session.Directory, "moved", "One"))
            |> should equal false
        finally
            WorkspaceCommandScenario.stop session
