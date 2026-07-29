namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Text
open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open FsUnit.Xunit
open Xunit

[<Collection("Workspace edits")>]
type WorkspaceEditCompensationTests() =
    [<Fact>]
    member _.``should compensate created and copied folders when a later write fails``() =
        let root = WorkspaceEditScenario.directory "folder-action-compensation"
        let project = Path.Combine(root, "Demo.csproj")
        File.WriteAllText(project, "<Project />")

        try
            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let run command arguments destination =
                let failedWrite = Path.Combine(root, "missing", "failure.txt")

                let argumentPaths =
                    arguments
                    |> List.choose (fun argument ->
                        match argument.Value with
                        | Path path -> Some path.Value
                        | _ -> None)

                let request =
                    WorkspaceEditScenario.folderRequest
                        command
                        ([ project; destination; root; failedWrite ] @ argumentPaths)
                        arguments

                let actions =
                    [ WorkspaceEditAction.ReplaceFile(failedWrite, Encoding.UTF8.GetBytes "fail") ]

                let preview = WorkspaceEditScenario.preview coordinator request actions

                match
                    coordinator.Execute(
                        request,
                        actions,
                        preview.Confirmation,
                        CancellationToken.None
                    )
                with
                | Success(RolledBack(Internal _)) -> ()
                | result -> failwithf "Expected rollback, got %A" result

                Directory.Exists destination |> should equal false

            let created = Path.Combine(root, "Created")
            run "project.folder.new" [ WorkspaceEditScenario.argument "path" created ] created

            let source = Path.Combine(root, "Source")
            Directory.CreateDirectory source |> ignore
            File.WriteAllText(Path.Combine(source, "Source.txt"), "source")
            let copied = Path.Combine(root, "Copied")

            run
                "project.folder.copy"
                [ WorkspaceEditScenario.argument "source" source
                  WorkspaceEditScenario.argument "path" copied ]
                copied

            File.ReadAllText(Path.Combine(source, "Source.txt")) |> should equal "source"
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should compensate a staged physical project move when a later write fails``() =
        let root = WorkspaceEditScenario.directory "physical-move-compensation"

        try
            let source = Path.Combine(root, "src", "One")
            let destination = Path.Combine(root, "moved", "One")
            let failedWrite = Path.Combine(root, "missing", "Demo.slnx")
            Directory.CreateDirectory(Path.Combine(source, "nested")) |> ignore
            Directory.CreateDirectory(Path.Combine(root, "moved")) |> ignore
            File.WriteAllText(Path.Combine(source, "nested", "keep.txt"), "keep")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let request =
                { WorkspaceEditScenario.request
                      []
                      [ source; destination; failedWrite ]
                      [ WorkspaceEditIntent.Overwrite ]
                      0L with
                    CommandId = CommandId.Create "project.relocate" }

            let actions =
                [ WorkspaceEditAction.Move(source, destination)
                  WorkspaceEditAction.ReplaceFile(failedWrite, Encoding.UTF8.GetBytes "unreachable") ]

            let preview = WorkspaceEditScenario.preview coordinator request actions

            match
                coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
            with
            | Success(RolledBack(Internal _)) -> ()
            | result -> failwithf "Expected complete physical-move compensation, got %A" result

            Directory.Exists source |> should equal true

            File.ReadAllText(Path.Combine(source, "nested", "keep.txt"))
            |> should equal "keep"

            Directory.Exists destination |> should equal false
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should roll back prior writes when trash refuses deletion``() =
        let root, target, victim, outcome =
            WorkspaceEditScenario.runCompensation (fun _ ->
                WorkspaceEditScenario.RefusingTrash "expected refusal")

        try
            match outcome with
            | Success(RolledBack(UnsupportedCapability _)) -> ()
            | result -> failwithf "Expected complete rollback, got %A" result

            Assert.Equal("old", File.ReadAllText target)
            Assert.Equal("victim", File.ReadAllText victim)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should preserve the current destination when compensation evidence is missing``() =
        let root, target, _, outcome =
            WorkspaceEditScenario.runCompensation WorkspaceEditScenario.SabotagingTrash

        try
            match outcome with
            | Failure(PartialRecoveryRequired(detail, _)) ->
                Assert.Contains(target, detail)
                Assert.Contains("restore", detail)
            | result -> failwithf "Expected incomplete compensation, got %A" result

            Assert.Equal("new", File.ReadAllText target)
        finally
            Directory.Delete(root, true)
