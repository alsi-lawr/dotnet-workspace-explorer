namespace Dotnet.CLI.Plus.Tests

open System
open System.Collections.Immutable
open System.IO
open System.Text
open System.Threading
open Dotnet.CLI.Plus
open Dotnet.CLI.Plus.Core
open FsUnit.Xunit
open Xunit

module private MutationTest =
    type Clock(initial: DateTimeOffset) =
        inherit TimeProvider()

        let mutable now = initial

        member _.Advance value = now <- now.Add value
        override _.GetUtcNow() = now

    type RefusingTrash(message: string) =
        interface TrashBackend with
            member _.MoveToTrash _ = Error { Message = message }

    type SabotagingTrash(directory: string) =
        interface TrashBackend with
            member _.MoveToTrash _ =
                for path in
                    Directory.EnumerateFileSystemEntries(directory, "*.dotnet-plus-rollback-*") do
                    File.Delete path

                Error { Message = "refused after rollback damage" }

    type CancellingTrash(cancellation: CancellationTokenSource, holdingPath: string) =
        interface TrashBackend with
            member _.MoveToTrash path =
                File.Move(path, holdingPath)
                cancellation.Cancel()
                Ok()

    let directory name =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-{name}-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let request externalRoots targets intents revision =
        { CommandId = CommandId.Create "filesystem.mutate"
          Targets = targets |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange
          Arguments = CommandArguments.Create []
          ExpectedRevision = WorkspaceRevision.Create revision
          Intents = intents |> ImmutableHashSet.CreateRange
          AuthorizedRoots =
            externalRoots
            |> Seq.map WorkspaceArtifactPath.Create
            |> ImmutableArray.CreateRange }

    let folderRequest command paths arguments =
        { CommandId = CommandId.Create command
          Targets = paths |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange
          Arguments = CommandArguments.Create arguments
          ExpectedRevision = WorkspaceRevision.Create 0L
          Intents = ImmutableHashSet.Create MutationIntent.Overwrite
          AuthorizedRoots =
            paths |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange }

    let argument name value =
        { ParameterId = CommandParameterId.Create name
          Value = Path(WorkspaceArtifactPath.Create value) }

    let coordinator root clock revision trash =
        MutationCoordinator(WorkspaceArtifactPath.Create root, clock, revision, trash)

    let preview (coordinator: MutationCoordinator) (request: MutationPreviewRequest) actions =
        match coordinator.Prepare(request, actions) with
        | Success value -> value
        | Failure error -> failwithf "Preview failed: %A" error

    let failure =
        function
        | Failure error -> error
        | Success result -> failwithf "Expected failure, got %A" result

    let assertInvalid outcome =
        match failure outcome with
        | InvalidInput _ -> ()
        | result -> failwithf "Expected invalid input, got %A" result

    let runCompensation trash =
        let root = directory "compensation"
        let target = Path.Combine(root, "target.txt")
        let victim = Path.Combine(root, "victim.txt")
        File.WriteAllText(target, "old")
        File.WriteAllText(victim, "victim")

        let coordinator =
            coordinator
                root
                TimeProvider.System
                (fun () -> WorkspaceRevision.Create 0L)
                (trash root)

        let request = request [] [ target; victim ] [ MutationIntent.Overwrite ] 0L

        let actions =
            [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "new")
              MutationAction.Trash victim ]

        let preview = preview coordinator request actions

        root,
        target,
        victim,
        coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)

type MutationCoordinatorTests() =
    [<Fact>]
    member _.``should leave a pre-cancelled folder copy unapplied``() =
        let root = MutationTest.directory "pre-cancelled-folder-copy"
        let source = Path.Combine(root, "Source")
        let destination = Path.Combine(root, "Destination")
        let project = Path.Combine(root, "Demo.csproj")
        Directory.CreateDirectory source |> ignore
        File.WriteAllText(Path.Combine(source, "Source.txt"), "source")
        File.WriteAllText(project, "<Project />")

        try
            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            let request =
                MutationTest.folderRequest
                    "project.folder.copy"
                    [ project; source; destination; root ]
                    [ MutationTest.argument "source" source
                      MutationTest.argument "path" destination ]

            let preview = MutationTest.preview coordinator request []
            use cancelled = new CancellationTokenSource()
            cancelled.Cancel()

            match coordinator.Execute(request, [], preview.Confirmation, cancelled.Token) with
            | Success(RolledBack(Cancelled _)) -> ()
            | result -> failwithf "Expected cancelled rollback, got %A" result

            Directory.Exists destination |> should equal false
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should compensate created and copied folders when a later write fails``() =
        let root = MutationTest.directory "folder-action-compensation"
        let project = Path.Combine(root, "Demo.csproj")
        File.WriteAllText(project, "<Project />")

        try
            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            let run command arguments destination =
                let failedWrite = Path.Combine(root, "missing", "failure.txt")

                let argumentPaths =
                    arguments
                    |> List.choose (fun argument ->
                        match argument.Value with
                        | Path path -> Some path.Value
                        | _ -> None)

                let request =
                    MutationTest.folderRequest
                        command
                        ([ project; destination; root; failedWrite ] @ argumentPaths)
                        arguments

                let actions =
                    [ MutationAction.ReplaceFile(failedWrite, Encoding.UTF8.GetBytes "fail") ]

                let preview = MutationTest.preview coordinator request actions

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
            run "project.folder.new" [ MutationTest.argument "path" created ] created

            let source = Path.Combine(root, "Source")
            Directory.CreateDirectory source |> ignore
            File.WriteAllText(Path.Combine(source, "Source.txt"), "source")
            let copied = Path.Combine(root, "Copied")

            run
                "project.folder.copy"
                [ MutationTest.argument "source" source; MutationTest.argument "path" copied ]
                copied

            File.ReadAllText(Path.Combine(source, "Source.txt")) |> should equal "source"
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should consume confirmations once and bind them to the exact executable plan``() =
        let root = MutationTest.directory "binding"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "original")

            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            let request = MutationTest.request [] [ target ] [ MutationIntent.Overwrite ] 0L

            let actions =
                [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "replacement") ]

            let bound = MutationTest.preview coordinator request actions

            coordinator.Execute(
                request,
                [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "different") ],
                bound.Confirmation,
                CancellationToken.None
            )
            |> MutationTest.assertInvalid

            Assert.Equal("original", File.ReadAllText target)

            let accepted = MutationTest.preview coordinator request actions

            coordinator.Execute(request, actions, accepted.Confirmation, CancellationToken.None)
            |> should equal (Success Applied)

            coordinator.Execute(request, actions, accepted.Confirmation, CancellationToken.None)
            |> MutationTest.assertInvalid
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should reject a stale workspace revision before changing an artifact``() =
        let root = MutationTest.directory "revision"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "original")
            let mutable revision = 7L

            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create revision)
                    (MutationTest.RefusingTrash "unused")

            let request =
                MutationTest.request [] [ target ] [ MutationIntent.Overwrite ] revision

            let actions =
                [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "replacement") ]

            let preview = MutationTest.preview coordinator request actions
            revision <- 8L

            match
                coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
            with
            | Failure(Conflict _) -> ()
            | outcome -> failwithf "Expected revision conflict, got %A" outcome

            Assert.Equal("original", File.ReadAllText target)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should reject a changed artifact fingerprint before writing``() =
        let root = MutationTest.directory "fingerprint"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "original")

            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            let request = MutationTest.request [] [ target ] [ MutationIntent.Overwrite ] 0L

            let actions =
                [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "replacement") ]

            let preview = MutationTest.preview coordinator request actions
            File.WriteAllText(target, "external")

            match
                coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
            with
            | Failure(Conflict _) -> ()
            | outcome -> failwithf "Expected fingerprint conflict, got %A" outcome

            Assert.Equal("external", File.ReadAllText target)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should reject an expired confirmation before writing``() =
        let root = MutationTest.directory "expiry"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "original")
            let clock = MutationTest.Clock DateTimeOffset.UtcNow

            let coordinator =
                MutationTest.coordinator
                    root
                    clock
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            let request = MutationTest.request [] [ target ] [ MutationIntent.Overwrite ] 0L

            let actions =
                [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "replacement") ]

            let preview = MutationTest.preview coordinator request actions
            clock.Advance(TimeSpan.FromMinutes 6.0)

            coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
            |> MutationTest.assertInvalid

            Assert.Equal("original", File.ReadAllText target)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should require explicit overwrite deletion and external-path intents``() =
        let root = MutationTest.directory "intents"
        let external = MutationTest.directory "external"

        try
            let target = Path.Combine(root, "target.txt")
            File.WriteAllText(target, "old")

            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            coordinator.Prepare(
                MutationTest.request [] [ target ] [] 0L,
                [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "new") ]
            )
            |> MutationTest.assertInvalid

            coordinator.Prepare(
                MutationTest.request [] [ target ] [] 0L,
                [ MutationAction.Delete(target, true, false) ]
            )
            |> MutationTest.assertInvalid

            let repeated = Path.Combine(root, "repeated.txt")

            coordinator.Prepare(
                MutationTest.request [] [ repeated ] [] 0L,
                [ MutationAction.ReplaceFile(repeated, Encoding.UTF8.GetBytes "first")
                  MutationAction.ReplaceFile(repeated, Encoding.UTF8.GetBytes "second") ]
            )
            |> MutationTest.assertInvalid

            let outside = Path.Combine(external, "outside.txt")
            File.WriteAllText(outside, "outside")

            coordinator.Prepare(MutationTest.request [ external ] [ outside ] [] 0L, [])
            |> MutationTest.assertInvalid

            MutationTest.preview
                coordinator
                (MutationTest.request
                    [ external ]
                    [ outside ]
                    [ MutationIntent.AccessExternalPath ]
                    0L)
                []
            |> ignore

            let nonEmpty = Path.Combine(root, "non-empty")
            Directory.CreateDirectory nonEmpty |> ignore
            File.WriteAllText(Path.Combine(nonEmpty, "child.txt"), "child")

            coordinator.Prepare(
                MutationTest.request [] [ nonEmpty ] [ MutationIntent.PermanentDelete ] 0L,
                [ MutationAction.Delete(nonEmpty, true, false) ]
            )
            |> MutationTest.assertInvalid

            coordinator.Prepare(
                MutationTest.request [] [ nonEmpty ] [ MutationIntent.PermanentDelete ] 0L,
                [ MutationAction.Delete(nonEmpty, true, true) ]
            )
            |> MutationTest.assertInvalid

            Assert.True(File.Exists(Path.Combine(nonEmpty, "child.txt")))
        finally
            Directory.Delete(root, true)
            Directory.Delete(external, true)

    [<Fact>]
    member _.``should replace a file and complete a collision-safe rename``() =
        let root = MutationTest.directory "replace-rename"

        try
            let target = Path.Combine(root, "Target.txt")
            File.WriteAllText(target, "old")

            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            let replaceRequest =
                MutationTest.request [] [ target ] [ MutationIntent.Overwrite ] 0L

            let replace = [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "new") ]
            let replacePreview = MutationTest.preview coordinator replaceRequest replace

            Assert.Equal(
                Success Applied,
                coordinator.Execute(
                    replaceRequest,
                    replace,
                    replacePreview.Confirmation,
                    CancellationToken.None
                )
            )

            let renamed = Path.Combine(root, "target.txt")
            let renameRequest = MutationTest.request [] [ target; renamed ] [] 0L
            let rename = [ MutationAction.Rename(target, renamed) ]
            let renamePreview = MutationTest.preview coordinator renameRequest rename

            Assert.Equal(
                Success Applied,
                coordinator.Execute(
                    renameRequest,
                    rename,
                    renamePreview.Confirmation,
                    CancellationToken.None
                )
            )

            Assert.Equal("new", File.ReadAllText renamed)

            Assert.Empty(
                Directory.EnumerateFileSystemEntries(root, "*.dotnet-plus-*") |> Seq.toArray
            )
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should mutate terminal link artifacts without traversing linked directories``() =
        if not (OperatingSystem.IsWindows()) then
            let root = MutationTest.directory "links"
            let external = MutationTest.directory "link-target"

            try
                let outside = Path.Combine(external, "outside.txt")
                File.WriteAllText(outside, "outside")

                let coordinator =
                    MutationTest.coordinator
                        root
                        TimeProvider.System
                        (fun () -> WorkspaceRevision.Create 0L)
                        (MutationTest.RefusingTrash "unused")

                let replacementLink = Path.Combine(root, "replacement-link")
                File.CreateSymbolicLink(replacementLink, outside) |> ignore

                let linkRequest =
                    MutationTest.request [] [ replacementLink ] [ MutationIntent.Overwrite ] 0L

                let linkReplacement =
                    [ MutationAction.ReplaceFile(
                          replacementLink,
                          Encoding.UTF8.GetBytes "replacement"
                      ) ]

                let linkPreview = MutationTest.preview coordinator linkRequest linkReplacement

                Assert.Equal(
                    Success Applied,
                    coordinator.Execute(
                        linkRequest,
                        linkReplacement,
                        linkPreview.Confirmation,
                        CancellationToken.None
                    )
                )

                Assert.Equal("replacement", File.ReadAllText replacementLink)
                Assert.Equal("outside", File.ReadAllText outside)

                let broken = Path.Combine(root, "broken-link")
                File.CreateSymbolicLink(broken, Path.Combine(root, "missing")) |> ignore

                let brokenRequest =
                    MutationTest.request [] [ broken ] [ MutationIntent.PermanentDelete ] 0L

                let brokenDelete = [ MutationAction.Delete(broken, true, false) ]
                let brokenPreview = MutationTest.preview coordinator brokenRequest brokenDelete

                Assert.Equal(
                    Success Applied,
                    coordinator.Execute(
                        brokenRequest,
                        brokenDelete,
                        brokenPreview.Confirmation,
                        CancellationToken.None
                    )
                )

                Assert.Null((FileInfo broken).LinkTarget)

                let linkedDirectory = Path.Combine(root, "linked-directory")
                Directory.CreateSymbolicLink(linkedDirectory, external) |> ignore

                coordinator.Prepare(
                    MutationTest.request [] [ Path.Combine(linkedDirectory, "outside.txt") ] [] 0L,
                    []
                )
                |> MutationTest.assertInvalid

                let tree = Path.Combine(root, "tree")
                Directory.CreateDirectory tree |> ignore
                Directory.CreateSymbolicLink(Path.Combine(tree, "link"), external) |> ignore

                coordinator.Prepare(MutationTest.request [] [ tree ] [] 0L, [])
                |> MutationTest.assertInvalid
            finally
                Directory.Delete(root, true)
                Directory.Delete(external, true)

    [<Fact>]
    member _.``should preserve tree contents while moving to an authorised destination``() =
        let root = MutationTest.directory "move"

        let destinationRoot =
            if OperatingSystem.IsLinux() && Directory.Exists "/dev/shm" then
                "/dev/shm"
            else
                root

        let destination =
            Path.Combine(destinationRoot, $"dotnet-plus-move-{Guid.NewGuid():N}")

        try
            let source = Path.Combine(root, "source")
            Directory.CreateDirectory source |> ignore
            File.WriteAllText(Path.Combine(source, "child.txt"), "child")

            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            let isExternal = Path.GetFullPath destinationRoot <> Path.GetFullPath root

            let request =
                MutationTest.request
                    (if isExternal then [ destinationRoot ] else [])
                    [ source; destination ]
                    (if isExternal then
                         [ MutationIntent.AccessExternalPath ]
                     else
                         [])
                    0L

            let move = [ MutationAction.Move(source, destination) ]
            let preview = MutationTest.preview coordinator request move

            Assert.Equal(
                Success Applied,
                coordinator.Execute(request, move, preview.Confirmation, CancellationToken.None)
            )

            Assert.False(Directory.Exists source)
            Assert.Equal("child", File.ReadAllText(Path.Combine(destination, "child.txt")))
        finally
            if Directory.Exists destination then
                Directory.Delete(destination, true)

            Directory.Delete(root, true)

    [<Fact>]
    member _.``should compensate a staged physical project move when a later write fails``() =
        let root = MutationTest.directory "physical-move-compensation"

        try
            let source = Path.Combine(root, "src", "One")
            let destination = Path.Combine(root, "moved", "One")
            let failedWrite = Path.Combine(root, "missing", "Demo.slnx")
            Directory.CreateDirectory(Path.Combine(source, "nested")) |> ignore
            Directory.CreateDirectory(Path.Combine(root, "moved")) |> ignore
            File.WriteAllText(Path.Combine(source, "nested", "keep.txt"), "keep")

            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.RefusingTrash "unused")

            let request =
                { MutationTest.request
                      []
                      [ source; destination; failedWrite ]
                      [ MutationIntent.Overwrite ]
                      0L with
                    CommandId = CommandId.Create "project.physical-move" }

            let actions =
                [ MutationAction.Move(source, destination)
                  MutationAction.ReplaceFile(failedWrite, Encoding.UTF8.GetBytes "unreachable") ]

            let preview = MutationTest.preview coordinator request actions

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
            MutationTest.runCompensation (fun _ -> MutationTest.RefusingTrash "expected refusal")

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
            MutationTest.runCompensation MutationTest.SabotagingTrash

        try
            match outcome with
            | Failure(PartialRecoveryRequired(detail, _)) ->
                Assert.Contains(target, detail)
                Assert.Contains("restore", detail)
            | result -> failwithf "Expected incomplete compensation, got %A" result

            Assert.Equal("new", File.ReadAllText target)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should report cancellation observed after a destructive action as partial``() =
        let root = MutationTest.directory "cancel"

        try
            let target = Path.Combine(root, "target.txt")
            let victim = Path.Combine(root, "victim.txt")
            let holding = Path.Combine(root, "trashed.txt")
            File.WriteAllText(target, "old")
            File.WriteAllText(victim, "victim")
            use cancellation = new CancellationTokenSource()

            let coordinator =
                MutationTest.coordinator
                    root
                    TimeProvider.System
                    (fun () -> WorkspaceRevision.Create 0L)
                    (MutationTest.CancellingTrash(cancellation, holding))

            let request =
                MutationTest.request [] [ target; victim ] [ MutationIntent.Overwrite ] 0L

            let actions =
                [ MutationAction.ReplaceFile(target, Encoding.UTF8.GetBytes "new")
                  MutationAction.Trash victim ]

            let preview = MutationTest.preview coordinator request actions

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

    [<Fact>]
    member _.``should select the native trash backend for the current host``() =
        let selected = MutationTrash.CreateForCurrentUser()

        if OperatingSystem.IsWindows() then
            Assert.Equal("Windows", selected.GetType().Name)
        elif OperatingSystem.IsMacOS() then
            Assert.Equal("MacOS", selected.GetType().Name)
        else
            Assert.Equal("Freedesktop", selected.GetType().Name)
