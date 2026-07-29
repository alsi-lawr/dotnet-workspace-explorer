namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Text
open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Xunit

[<Collection("Workspace edits")>]
type ArtifactEditingTests() =
    [<Fact>]
    member _.``should replace a file and complete a collision-safe rename``() =
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
            let renameRequest = WorkspaceEditScenario.request [] [ target; renamed ] [] 0L
            let rename = [ WorkspaceEditAction.Rename(target, renamed) ]
            let renamePreview = WorkspaceEditScenario.preview coordinator renameRequest rename

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
                Directory.EnumerateFileSystemEntries(root, "*.dotnet-workspace-explorer-*")
                |> Seq.toArray
            )
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``should mutate terminal link artifacts without traversing linked directories``() =
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
                    WorkspaceEditScenario.request
                        []
                        [ broken ]
                        [ WorkspaceEditIntent.PermanentDelete ]
                        0L

                let brokenDelete = [ WorkspaceEditAction.Delete(broken, true, false) ]

                let brokenPreview =
                    WorkspaceEditScenario.preview coordinator brokenRequest brokenDelete

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
    member _.``should preserve tree contents while moving to an authorised destination``() =
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
