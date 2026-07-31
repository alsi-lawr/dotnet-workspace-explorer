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
    member _.``a later write failure compensates created and copied folders``() =
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
    member _.``a later write failure compensates a staged physical project move``() =
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
    member _.``trash deletion refusal rolls back prior writes completely``() =
        let root, target, victim, outcome =
            WorkspaceEditScenario.runCompensation (fun _ ->
                WorkspaceEditScenario.RefusingTrash "expected refusal")

        try
            match outcome with
            | Success(RolledBack(UnsupportedCapability _)) -> ()
            | result -> failwithf "Expected complete rollback, got %A" result

            (File.ReadAllText target) |> should equal ("old")
            (File.ReadAllText victim) |> should equal ("victim")
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``missing compensation evidence preserves the current destination and reports partial recovery``
        ()
        =
        let root, target, _, outcome =
            WorkspaceEditScenario.runCompensation WorkspaceEditScenario.SabotagingTrash

        try
            match outcome with
            | Failure(PartialRecoveryRequired(detail, _)) ->
                (detail) |> should haveSubstring (target)
                (detail) |> should haveSubstring ("restore")
            | result -> failwithf "Expected incomplete compensation, got %A" result

            (File.ReadAllText target) |> should equal ("new")
        finally
            Directory.Delete(root, true)
