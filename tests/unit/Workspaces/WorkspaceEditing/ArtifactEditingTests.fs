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
type ArtifactEditingTests() =
    [<Fact>]
    member _.``generated documents replace existing authority without granting overwrite to physical edits``
        ()
        =
        let root = WorkspaceEditScenario.directory "generated-document-authority"

        try
            let document = Path.Combine(root, "Demo.csproj")
            let source = Path.Combine(root, "Source.cs")
            let occupied = Path.Combine(root, "Occupied.cs")
            File.WriteAllText(document, "old project")
            File.WriteAllText(source, "source")
            File.WriteAllText(occupied, "occupied")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let documentRequest = WorkspaceEditScenario.request [] [ document ] [] 0L

            let documentEdit =
                [ WorkspaceEditAction.ReplaceGeneratedDocument(
                      document,
                      Encoding.UTF8.GetBytes "new project"
                  ) ]

            let preview = WorkspaceEditScenario.preview coordinator documentRequest documentEdit

            coordinator.Execute(
                documentRequest,
                documentEdit,
                preview.Confirmation,
                CancellationToken.None
            )
            |> should equal (Success Applied)

            File.ReadAllText document |> should equal "new project"

            coordinator.Prepare(
                WorkspaceEditScenario.request [] [ occupied ] [] 0L,
                [ WorkspaceEditAction.ReplaceFile(occupied, Encoding.UTF8.GetBytes "overwritten") ]
            )
            |> WorkspaceEditScenario.assertInvalid

            coordinator.Prepare(
                WorkspaceEditScenario.request [] [ source; occupied ] [] 0L,
                [ WorkspaceEditAction.Rename(source, occupied) ]
            )
            |> WorkspaceEditScenario.assertInvalid

            File.ReadAllText occupied |> should equal "occupied"
            File.ReadAllText source |> should equal "source"
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``destination identity follows the mounted filesystem case semantics for collisions``
        ()
        =
        let root = WorkspaceEditScenario.directory "filesystem-case-destinations"

        try
            let numericParent = Path.Combine(root, "12345")
            Directory.CreateDirectory numericParent |> ignore
            let existing = Path.Combine(numericParent, "Existing.cs")
            let caseVariant = Path.Combine(numericParent, "existing.cs")
            File.WriteAllText(existing, "existing")

            let identitiesMatch =
                ArtifactFiles.identity existing = ArtifactFiles.identity caseVariant

            let variantExists = ArtifactFiles.exists caseVariant
            identitiesMatch |> should equal variantExists

            let first = Path.Combine(numericParent, "First.cs")
            let second = Path.Combine(numericParent, "Second.cs")
            File.WriteAllText(first, "first")
            File.WriteAllText(second, "second")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let destinations =
                [ Path.Combine(numericParent, "Output.cs")
                  Path.Combine(numericParent, "output.cs") ]

            let request =
                WorkspaceEditScenario.request [] ([ first; second ] @ destinations) [] 0L

            let outcome =
                coordinator.Prepare(
                    request,
                    [ WorkspaceEditAction.Copy(first, destinations[0])
                      WorkspaceEditAction.Copy(second, destinations[1]) ]
                )

            if ArtifactFiles.identity destinations[0] = ArtifactFiles.identity destinations[1] then
                outcome |> WorkspaceEditScenario.assertInvalid
            else
                match outcome with
                | Success _ -> ()
                | failure ->
                    failwithf
                        "A case-sensitive filesystem rejected distinct destinations: %A"
                        failure
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``physical copy rename and move reject a destination introduced before staged commit``
        ()
        =
        let run name action =
            let root = WorkspaceEditScenario.directory $"physical-{name}-commit-collision"

            try
                let source = Path.Combine(root, "Source.cs")
                let destination = Path.Combine(root, "Destination.cs")
                File.WriteAllText(source, "source")

                let coordinator =
                    WorkspaceEditScenario.coordinator
                        root
                        TimeProvider.System
                        (fun () -> WorkspaceRevision.Create 0L)
                        (WorkspaceEditScenario.RefusingTrash "unused")

                let request =
                    WorkspaceEditScenario.folderRequest
                        "project.folder.new"
                        [ source; destination; root ]
                        [ WorkspaceEditScenario.argument "path" destination ]

                let actions = [ action source destination ]
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
                | outcome ->
                    failwithf
                        "Expected %s to reject the destination created during execution: %A"
                        name
                        outcome

                File.ReadAllText source |> should equal "source"
                ArtifactFiles.exists destination |> should equal false
            finally
                Directory.Delete(root, true)

        run "copy" (fun source destination -> WorkspaceEditAction.Copy(source, destination))
        run "rename" (fun source destination -> WorkspaceEditAction.Rename(source, destination))
        run "move" (fun source destination -> WorkspaceEditAction.Move(source, destination))

    [<Fact>]
    member _.``file replacement followed by a collision-safe rename persists content and cleans staging artifacts``
        ()
        =
        let root = WorkspaceEditScenario.directory "replace-rename"

        try
            let target = Path.Combine(root, "Target.txt")
            File.WriteAllText(target, "old")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let replaceRequest =
                WorkspaceEditScenario.request [] [ target ] [ WorkspaceEditIntent.Overwrite ] 0L

            let replace =
                [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "new") ]

            let replacePreview =
                WorkspaceEditScenario.preview coordinator replaceRequest replace

            (coordinator.Execute(
                replaceRequest,
                replace,
                replacePreview.Confirmation,
                CancellationToken.None
            ))
            |> should equal (Success Applied)

            let renamed = Path.Combine(root, "target.txt")
            let renameRequest = WorkspaceEditScenario.request [] [ target; renamed ] [] 0L
            let rename = [ WorkspaceEditAction.Rename(target, renamed) ]
            let renamePreview = WorkspaceEditScenario.preview coordinator renameRequest rename

            (coordinator.Execute(
                renameRequest,
                rename,
                renamePreview.Confirmation,
                CancellationToken.None
            ))
            |> should equal (Success Applied)

            (File.ReadAllText renamed) |> should equal ("new")

            (Directory.EnumerateFileSystemEntries(root, "*.dotnet-workspace-explorer-*")
             |> Seq.toArray)
            |> should be Empty
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``copying a directory through one transaction preserves source and destination trees``
        ()
        =
        let root = WorkspaceEditScenario.directory "copy"

        try
            let source = Path.Combine(root, "source")
            let destination = Path.Combine(root, "destination")
            Directory.CreateDirectory source |> ignore
            File.WriteAllText(Path.Combine(source, "child.txt"), "child")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let request = WorkspaceEditScenario.request [] [ source; destination ] [] 0L
            let copy = [ WorkspaceEditAction.Copy(source, destination) ]
            let preview = WorkspaceEditScenario.preview coordinator request copy

            coordinator.Execute(request, copy, preview.Confirmation, CancellationToken.None)
            |> should equal (Success Applied)

            File.ReadAllText(Path.Combine(source, "child.txt")) |> should equal "child"
            File.ReadAllText(Path.Combine(destination, "child.txt")) |> should equal "child"
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``terminal symbolic-link replacement and deletion avoid traversing linked targets``() =
        if not (OperatingSystem.IsWindows()) then
            let root = WorkspaceEditScenario.directory "links"
            let external = WorkspaceEditScenario.directory "link-target"

            try
                let outside = Path.Combine(external, "outside.txt")
                File.WriteAllText(outside, "outside")

                let coordinator =
                    WorkspaceEditScenario.coordinator
                        root
                        TimeProvider.System
                        (fun () -> WorkspaceRevision.Create 0L)
                        (WorkspaceEditScenario.RefusingTrash "unused")

                let replacementLink = Path.Combine(root, "replacement-link")
                File.CreateSymbolicLink(replacementLink, outside) |> ignore

                let linkRequest =
                    WorkspaceEditScenario.request
                        []
                        [ replacementLink ]
                        [ WorkspaceEditIntent.Overwrite ]
                        0L

                let linkReplacement =
                    [ WorkspaceEditAction.ReplaceFile(
                          replacementLink,
                          Encoding.UTF8.GetBytes "replacement"
                      ) ]

                let linkPreview =
                    WorkspaceEditScenario.preview coordinator linkRequest linkReplacement

                (coordinator.Execute(
                    linkRequest,
                    linkReplacement,
                    linkPreview.Confirmation,
                    CancellationToken.None
                ))
                |> should equal (Success Applied)

                (File.ReadAllText replacementLink) |> should equal ("replacement")
                (File.ReadAllText outside) |> should equal ("outside")

                let broken = Path.Combine(root, "broken-link")
                File.CreateSymbolicLink(broken, Path.Combine(root, "missing")) |> ignore

                let brokenRequest =
                    WorkspaceEditScenario.request
                        []
                        [ broken ]
                        [ WorkspaceEditIntent.PermanentDelete ]
                        0L

                let brokenDelete = [ WorkspaceEditAction.Delete(broken, true, false) ]

                let brokenPreview =
                    WorkspaceEditScenario.preview coordinator brokenRequest brokenDelete

                (coordinator.Execute(
                    brokenRequest,
                    brokenDelete,
                    brokenPreview.Confirmation,
                    CancellationToken.None
                ))
                |> should equal (Success Applied)

                ((FileInfo broken).LinkTarget) |> should be Null

                let linkedDirectory = Path.Combine(root, "linked-directory")
                Directory.CreateSymbolicLink(linkedDirectory, external) |> ignore

                coordinator.Prepare(
                    WorkspaceEditScenario.request
                        []
                        [ Path.Combine(linkedDirectory, "outside.txt") ]
                        []
                        0L,
                    []
                )
                |> WorkspaceEditScenario.assertInvalid

                let tree = Path.Combine(root, "tree")
                Directory.CreateDirectory tree |> ignore
                Directory.CreateSymbolicLink(Path.Combine(tree, "link"), external) |> ignore

                coordinator.Prepare(WorkspaceEditScenario.request [] [ tree ] [] 0L, [])
                |> WorkspaceEditScenario.assertInvalid
            finally
                Directory.Delete(root, true)
                Directory.Delete(external, true)

    [<Fact>]
    member _.``moving a directory to an authorised destination preserves its tree contents``() =
        let root = WorkspaceEditScenario.directory "move"

        let destinationRoot =
            if OperatingSystem.IsLinux() && Directory.Exists "/dev/shm" then
                "/dev/shm"
            else
                root

        let destination =
            Path.Combine(destinationRoot, $"dotnet-workspace-explorer-move-{Guid.NewGuid():N}")

        try
            let source = Path.Combine(root, "source")
            Directory.CreateDirectory source |> ignore
            File.WriteAllText(Path.Combine(source, "child.txt"), "child")

            let coordinator =
                WorkspaceEditScenario.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (WorkspaceEditScenario.RefusingTrash "unused")

            let isExternal = Path.GetFullPath destinationRoot <> Path.GetFullPath root

            let request =
                WorkspaceEditScenario.request
                    (if isExternal then [ destinationRoot ] else [])
                    [ source; destination ]
                    (if isExternal then
                         [ WorkspaceEditIntent.AccessExternalPath ]
                     else
                         [])
                    0L

            let move = [ WorkspaceEditAction.Move(source, destination) ]
            let preview = WorkspaceEditScenario.preview coordinator request move

            (coordinator.Execute(request, move, preview.Confirmation, CancellationToken.None))
            |> should equal (Success Applied)

            (Directory.Exists source) |> should equal false

            (File.ReadAllText(Path.Combine(destination, "child.txt")))
            |> should equal ("child")
        finally
            if Directory.Exists destination then
                Directory.Delete(destination, true)

            Directory.Delete(root, true)
