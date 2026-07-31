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
type WorkspaceEditConfirmationTests() =
    [<Fact>]
    member _.``contextual batch confirmations reject changed expired and reused authority before preserving one successful document composition``
        ()
        =
        let root = WorkspaceEditScenario.directory "contextual-batch-confirmation"

        try
            let source = Path.Combine(root, "Source.cs")
            let destination = Path.Combine(root, "Destination.cs")
            let document = Path.Combine(root, "Demo.csproj")
            File.WriteAllText(source, "source")
            File.WriteAllText(document, "original project")
            let clock = WorkspaceEditScenario.Clock DateTimeOffset.UtcNow

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    clock
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let request =
                { WorkspaceEditScenario.request [] [ source; destination; document ] [] 0L with
                    CommandId = CommandId.Create "workspace.move" }

            let actions =
                [ WorkspaceEditAction.Move(source, destination)
                  WorkspaceEditAction.ReplaceGeneratedDocument(
                      document,
                      Encoding.UTF8.GetBytes "moved project"
                  ) ]

            let changed = WorkspaceEditScenario.preview coordinator request actions
            File.WriteAllText(document, "external project")

            match
                coordinator.Execute(request, actions, changed.Confirmation, CancellationToken.None)
            with
            | Failure(Conflict _) -> ()
            | outcome -> failwithf "Expected the changed document to conflict: %A" outcome

            File.Exists source |> should equal true
            File.Exists destination |> should equal false

            let expired = WorkspaceEditScenario.preview coordinator request actions
            clock.Advance(TimeSpan.FromMinutes 6.0)

            coordinator.Execute(request, actions, expired.Confirmation, CancellationToken.None)
            |> WorkspaceEditScenario.assertInvalid

            let accepted = WorkspaceEditScenario.preview coordinator request actions

            coordinator.Execute(request, actions, accepted.Confirmation, CancellationToken.None)
            |> should equal (Success Applied)

            coordinator.Execute(request, actions, accepted.Confirmation, CancellationToken.None)
            |> WorkspaceEditScenario.assertInvalid

            File.Exists source |> should equal false
            File.ReadAllText destination |> should equal "source"
            File.ReadAllText document |> should equal "moved project"
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``a confirmation binds to its exact executable plan and cannot be reused``() =
        let root = WorkspaceEditScenario.directory "binding"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "original")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let request =
                WorkspaceEditScenario.request [] [ target ] [ WorkspaceEditIntent.Overwrite ] 0L

            let actions =
                [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "replacement") ]

            let bound = WorkspaceEditScenario.preview coordinator request actions

            coordinator.Execute(
                request,
                [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "different") ],
                bound.Confirmation,
                CancellationToken.None
            )
            |> WorkspaceEditScenario.assertInvalid

            (File.ReadAllText target) |> should equal ("original")

            let accepted = WorkspaceEditScenario.preview coordinator request actions

            coordinator.Execute(request, actions, accepted.Confirmation, CancellationToken.None)
            |> should equal (Success Applied)

            coordinator.Execute(request, actions, accepted.Confirmation, CancellationToken.None)
            |> WorkspaceEditScenario.assertInvalid
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``a stale workspace revision rejects execution before changing an artifact``() =
        let root = WorkspaceEditScenario.directory "revision"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "original")
            let mutable revision = 7L

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create revision)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let request =
                WorkspaceEditScenario.request
                    []
                    [ target ]
                    [ WorkspaceEditIntent.Overwrite ]
                    revision

            let actions =
                [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "replacement") ]

            let preview = WorkspaceEditScenario.preview coordinator request actions
            revision <- 8L

            match
                coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
            with
            | Failure(Conflict _) -> ()
            | outcome -> failwithf "Expected revision conflict, got %A" outcome

            (File.ReadAllText target) |> should equal ("original")
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``a changed artifact fingerprint rejects execution before writing``() =
        let root = WorkspaceEditScenario.directory "fingerprint"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "original")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let request =
                WorkspaceEditScenario.request [] [ target ] [ WorkspaceEditIntent.Overwrite ] 0L

            let actions =
                [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "replacement") ]

            let preview = WorkspaceEditScenario.preview coordinator request actions
            File.WriteAllText(target, "external")

            match
                coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
            with
            | Failure(Conflict _) -> ()
            | outcome -> failwithf "Expected fingerprint conflict, got %A" outcome

            (File.ReadAllText target) |> should equal ("external")
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``an expired confirmation rejects execution before writing``() =
        let root = WorkspaceEditScenario.directory "expiry"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "original")
            let clock = WorkspaceEditScenario.Clock DateTimeOffset.UtcNow

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    clock
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let request =
                WorkspaceEditScenario.request [] [ target ] [ WorkspaceEditIntent.Overwrite ] 0L

            let actions =
                [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "replacement") ]

            let preview = WorkspaceEditScenario.preview coordinator request actions
            clock.Advance(TimeSpan.FromMinutes 6.0)

            coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
            |> WorkspaceEditScenario.assertInvalid

            (File.ReadAllText target) |> should equal ("original")
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``destructive and external edits require explicit matching intents``() =
        let root = WorkspaceEditScenario.directory "intents"
        let external = WorkspaceEditScenario.directory "external"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "old")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            coordinator.Prepare(
                WorkspaceEditScenario.request [] [ target ] [] 0L,
                [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "new") ]
            )
            |> WorkspaceEditScenario.assertInvalid

            coordinator.Prepare(
                WorkspaceEditScenario.request [] [ target ] [] 0L,
                [ WorkspaceEditAction.Delete(target, true, false) ]
            )
            |> WorkspaceEditScenario.assertInvalid

            let repeated = Path.Combine(root, "repeated.txt")

            coordinator.Prepare(
                WorkspaceEditScenario.request [] [ repeated ] [] 0L,
                [ WorkspaceEditAction.ReplaceFile(repeated, Encoding.UTF8.GetBytes "first")
                  WorkspaceEditAction.ReplaceFile(repeated, Encoding.UTF8.GetBytes "second") ]
            )
            |> WorkspaceEditScenario.assertInvalid

            let outside = Path.Combine(external, "outside.txt")
            File.WriteAllText(outside, "outside")

            coordinator.Prepare(WorkspaceEditScenario.request [ external ] [ outside ] [] 0L, [])
            |> WorkspaceEditScenario.assertInvalid

            WorkspaceEditScenario.preview
                coordinator
                (WorkspaceEditScenario.request
                    [ external ]
                    [ outside ]
                    [ WorkspaceEditIntent.AccessExternalPath ]
                    0L)
                []
            |> ignore

            let nonEmpty = Path.Combine(root, "non-empty")
            Directory.CreateDirectory nonEmpty |> ignore
            File.WriteAllText(Path.Combine(nonEmpty, "child.txt"), "child")

            coordinator.Prepare(
                WorkspaceEditScenario.request
                    []
                    [ nonEmpty ]
                    [ WorkspaceEditIntent.PermanentDelete ]
                    0L,
                [ WorkspaceEditAction.Delete(nonEmpty, true, false) ]
            )
            |> WorkspaceEditScenario.assertInvalid

            coordinator.Prepare(
                WorkspaceEditScenario.request
                    []
                    [ nonEmpty ]
                    [ WorkspaceEditIntent.PermanentDelete ]
                    0L,
                [ WorkspaceEditAction.Delete(nonEmpty, true, true) ]
            )
            |> WorkspaceEditScenario.assertInvalid

            (File.Exists(Path.Combine(nonEmpty, "child.txt"))) |> should equal true
        finally
            Directory.Delete(root, true)
            Directory.Delete(external, true)
