namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Xml.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open FsUnit.Xunit
open Xunit

module private WorkspaceEditScenario =
    type Clock(initial: DateTimeOffset) =
        inherit TimeProvider()

        let mutable now = initial

        member _.Advance value = now <- now.Add value
        override _.GetUtcNow() = now

    type RefusingTrash(message: string) =
        interface ArtifactTrash with
            member _.MoveToTrash _ = Error { Message = message }

    type SabotagingTrash(directory: string) =
        interface ArtifactTrash with
            member _.MoveToTrash _ =
                for path in
                    Directory.EnumerateFileSystemEntries(
                        directory,
                        "*.dotnet-workspace-explorer-rollback-*"
                    ) do
                    File.Delete path

                Error { Message = "refused after rollback damage" }

    type CancellingTrash(cancellation: CancellationTokenSource, holdingPath: string) =
        interface ArtifactTrash with
            member _.MoveToTrash path =
                File.Move(path, holdingPath)
                cancellation.Cancel()
                Ok()

    let directory name =
        let path =
            Path.Combine(
                AppContext.BaseDirectory,
                $".dotnet-workspace-explorer-{name}-{Guid.NewGuid():N}"
            )

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
          Intents = ImmutableHashSet.Create WorkspaceEditIntent.Overwrite
          AuthorizedRoots =
            paths |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange }

    let argument name value =
        { ParameterId = CommandParameterId.Create name
          Value = Path(WorkspaceArtifactPath.Create value) }

    let coordinator root clock revision trash =
        WorkspaceEditTransaction(WorkspaceArtifactPath.Create root, clock, revision, trash)

    let preview
        (coordinator: WorkspaceEditTransaction)
        (request: WorkspaceEditPreviewRequest)
        actions
        =
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

        let request = request [] [ target; victim ] [ WorkspaceEditIntent.Overwrite ] 0L

        let actions =
            [ WorkspaceEditAction.ReplaceFile(target, Encoding.UTF8.GetBytes "new")
              WorkspaceEditAction.Trash victim ]

        let preview = preview coordinator request actions

        root,
        target,
        victim,
        coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
