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
type WorkspaceEditCancellationTests() =
    [<Fact>]
    member _.``should leave a pre-cancelled folder copy unapplied``() =
        let root = WorkspaceEditScenario.directory "pre-cancelled-folder-copy"
        let source = Path.Combine(root, "Source")
        let destination = Path.Combine(root, "Destination")
        let project = Path.Combine(root, "Demo.csproj")
        Directory.CreateDirectory source |> ignore
        File.WriteAllText(Path.Combine(source, "Source.txt"), "source")
        File.WriteAllText(project, "<Project />")

        try
            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let request =
                WorkspaceEditScenario.folderRequest
                    "project.folder.copy"
                    [ project; source; destination; root ]
                    [ WorkspaceEditScenario.argument "source" source
                      WorkspaceEditScenario.argument "path" destination ]

            let preview = WorkspaceEditScenario.preview coordinator request []
            use cancelled = new CancellationTokenSource()
            cancelled.Cancel()

            match coordinator.Execute(request, [], preview.Confirmation, cancelled.Token) with
            | Success(RolledBack(Cancelled _)) -> ()
            | result -> failwithf "Expected cancelled rollback, got %A" result

            Directory.Exists destination |> should equal false
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should report cancellation observed after a destructive action as partial``() =
        let root = WorkspaceEditScenario.directory "cancel"

        try
            let target = Path.Combine(root, "target.txt")
            let victim = Path.Combine(root, "victim.txt")
            let holding = Path.Combine(root, "trashed.txt")
            File.WriteAllText(target, "old")
            File.WriteAllText(victim, "victim")
            use cancellation = new CancellationTokenSource()

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.CancellingTrash(cancellation, holding))

            let request =
                WorkspaceEditScenario.request
                    []
                    [ target; victim ]
                    [ WorkspaceEditIntent.Overwrite ]
                    0L

            let actions =
                [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "new")
                  WorkspaceEditAction.Trash victim ]

            let preview = WorkspaceEditScenario.preview coordinator request actions

            match
                coordinator.Execute(request, actions, preview.Confirmation, cancellation.Token)
            with
            | Failure(PartialRecoveryRequired(detail, _)) -> Assert.Contains(victim, detail)
            | result -> failwithf "Expected partial cancellation result, got %A" result

            Assert.Equal("old", File.ReadAllText target)
            Assert.False(File.Exists victim)
            Assert.Equal("victim", File.ReadAllText holding)
        finally
            Directory.Delete(root, true)
