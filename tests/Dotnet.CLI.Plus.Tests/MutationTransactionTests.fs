namespace Dotnet.CLI.Plus.Tests

open System
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Dotnet.CLI.Plus
open Dotnet.CLI.Plus.Core
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private MutationTest =
    type Clock(initial: DateTimeOffset) =
        let mutable value = initial
        member _.Advance(span) = value <- value.Add span

        interface MutationClock with
            member _.UtcNow = value

    type RefusingTrash() =
        interface TrashBackend with
            member _.MoveToTrash _ = Error { Message = "refused for test" }

    let directory name =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-{name}-{Guid.NewGuid():N}")

        Directory.CreateDirectory(path) |> ignore
        path

    let request root targets intents =
        { CommandId = CommandId.Create "filesystem.mutate"
          Targets = targets |> ImmutableArray.CreateRange
          Arguments = CommandArguments.Create []
          ExpectedRevision = WorkspaceRevision.Create 7L
          Intents = intents |> ImmutableHashSet.CreateRange
          AuthorizedRoots = ImmutableArray.Create(WorkspaceArtifactPath.Create root) }

    let coordinator root clock trash =
        MutationCoordinator(root, clock, (fun () -> WorkspaceRevision.Create 7L), trash)

    let writableSolution path =
        let model = SolutionModel()

        match SolutionSerializers.GetSerializerByMoniker(path) |> Option.ofObj with
        | Some serializer -> serializer.SaveAsync(path, model, CancellationToken.None).GetAwaiter().GetResult()
        | None -> failwith "A solution serializer was not found."

    let expectFailure outcome =
        match outcome with
        | Failure _ -> ()
        | Success _ -> failwith "Expected a typed mutation refusal."

    let product =
        let rec root path =
            if File.Exists(Path.Combine(path, "Directory.Packages.props")) then
                path
            else
                match Directory.GetParent(path) |> Option.ofObj with
                | Some parent -> root parent.FullName
                | None -> failwith "The repository root was not found."

        let configuration =
            match (DirectoryInfo(AppContext.BaseDirectory)).Parent |> Option.ofObj with
            | Some parent -> parent.Name
            | None -> failwith "The test configuration directory was not found."

        let executable =
            if OperatingSystem.IsWindows() then
                "Dotnet.CLI.Plus.exe"
            else
                "Dotnet.CLI.Plus"

        Path.Combine(
            root AppContext.BaseDirectory,
            "src",
            "Dotnet.CLI.Plus",
            "bin",
            configuration,
            "net10.0",
            executable
        )

type MutationTransactionTests() =
    [<Fact>]
    member _.``preview tokens bind arguments targets revisions fingerprints and expiry without writes``() =
        let root = MutationTest.directory "mutation-token"

        try
            let source = Path.Combine(root, "source.txt")
            File.WriteAllText(source, "before")
            let clock = MutationTest.Clock(DateTimeOffset.UtcNow)

            let argument =
                { ParameterId = CommandParameterId.Create "name"
                  Value = Text "value" }

            let request =
                { MutationTest.request root [ WorkspaceArtifactPath.Create source ] [ MutationIntent.Overwrite ] with
                    Arguments = CommandArguments.Create [ argument ] }

            let coordinator = MutationTest.coordinator root clock (MutationTest.RefusingTrash())

            let preview =
                match coordinator.Prepare request with
                | Success value -> value
                | Failure failure -> failwithf "Preparation failed: %A" failure

            let recreated =
                { request with
                    Arguments = CommandArguments.Create [ argument ] }

            coordinator.Execute(
                preview.Token,
                recreated,
                [ MutationAction.ReplaceFile(source, Encoding.UTF8.GetBytes "replacement") ]
            )
            |> function
                | Success() -> ()
                | Failure failure -> failwithf "Recreated request failed: %A" failure

            for changed in
                [ { request with
                      CommandId = CommandId.Create "filesystem.other" }
                  { request with
                      ExpectedRevision = WorkspaceRevision.Create 8L }
                  { request with
                      Arguments = CommandArguments.Create [ { argument with Value = Text "other" } ] } ] do
                let bound =
                    match coordinator.Prepare request with
                    | Success value -> value
                    | Failure failure -> failwithf "Preparation failed: %A" failure

                coordinator.Execute(bound.Token, changed, []) |> MutationTest.expectFailure

            File.WriteAllText(source, "external edit")

            coordinator.Execute(
                preview.Token,
                request,
                [ MutationAction.ReplaceFile(source, Encoding.UTF8.GetBytes "replacement") ]
            )
            |> MutationTest.expectFailure

            Assert.Equal("external edit", File.ReadAllText source)
            coordinator.Execute(preview.Token, request, []) |> MutationTest.expectFailure

            let expired =
                match coordinator.Prepare request with
                | Success value -> value
                | Failure failure -> failwithf "Preparation failed: %A" failure

            clock.Advance(TimeSpan.FromMinutes 6.0)
            coordinator.Execute(expired.Token, request, []) |> MutationTest.expectFailure
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``intents authorization staged replacement and move compensation preserve artifacts``() =
        let root = MutationTest.directory "mutation-files"
        let outside = MutationTest.directory "mutation-outside"

        try
            let source = Path.Combine(root, "source.txt")
            let destination = Path.Combine(root, "destination.txt")
            File.WriteAllText(source, "source")
            File.WriteAllText(destination, "original")
            let clock = MutationTest.Clock(DateTimeOffset.UtcNow)

            let request =
                MutationTest.request
                    root
                    [ WorkspaceArtifactPath.Create source
                      WorkspaceArtifactPath.Create destination ]
                    [ MutationIntent.Overwrite ]

            let coordinator = MutationTest.coordinator root clock (MutationTest.RefusingTrash())

            let preview =
                match coordinator.Prepare request with
                | Success value -> value
                | Failure failure -> failwithf "Preparation failed: %A" failure

            coordinator.Execute(
                preview.Token,
                request,
                [ MutationAction.ReplaceFile(destination, Encoding.UTF8.GetBytes "changed")
                  MutationAction.Trash source ]
            )
            |> MutationTest.expectFailure

            Assert.Equal("original", File.ReadAllText destination)
            Assert.True(File.Exists source)

            let external = Path.Combine(outside, "external.txt")
            File.WriteAllText(external, "keep")

            let externalRequest =
                MutationTest.request root [ WorkspaceArtifactPath.Create external ] [ MutationIntent.PermanentDelete ]

            coordinator.Prepare externalRequest |> MutationTest.expectFailure

            Assert.True(File.Exists external)

            if not (OperatingSystem.IsWindows()) then
                let link = Path.Combine(root, "outside-link")
                File.CreateSymbolicLink(link, external) |> ignore

                MutationTest.request root [ WorkspaceArtifactPath.Create link ] []
                |> coordinator.Prepare
                |> MutationTest.expectFailure

            let moveRequest =
                MutationTest.request
                    root
                    [ WorkspaceArtifactPath.Create source
                      WorkspaceArtifactPath.Create(Path.Combine(root, "moved.txt")) ]
                    []

            let movePreview =
                match coordinator.Prepare moveRequest with
                | Success value -> value
                | Failure failure -> failwithf "Preparation failed: %A" failure

            coordinator.Execute(
                movePreview.Token,
                moveRequest,
                [ MutationAction.Move(source, Path.Combine(root, "moved.txt")) ]
            )
            |> function
                | Success() -> ()
                | Failure failure -> failwithf "Move failed: %A" failure

            Assert.False(File.Exists source)
            Assert.Equal("source", File.ReadAllText(Path.Combine(root, "moved.txt")))
        finally
            Directory.Delete(root, true)
            Directory.Delete(outside, true)

    [<Fact>]
    member _.``freedesktop trash is real and never requires permanent fallback``() =
        let selected = MutationTrash.CreateForCurrentUser()

        if OperatingSystem.IsWindows() then
            Assert.Equal("Windows", selected.GetType().Name)
        elif OperatingSystem.IsMacOS() then
            Assert.Equal("MacOS", selected.GetType().Name)

        if OperatingSystem.IsLinux() then
            let root = MutationTest.directory "freedesktop-trash"

            try
                let source = Path.Combine(root, "discard.txt")
                File.WriteAllText(source, "discard")

                match (MutationTrash.CreateFreedesktop(Path.Combine(root, "data"))).MoveToTrash source with
                | Ok() ->
                    Assert.False(File.Exists source)

                    Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "data", "Trash", "info")))
                    |> ignore
                | Error failure -> failwithf "Freedesktop trash refused an ordinary local file: %s" failure.Message

                if Directory.Exists("/dev/shm") then
                    let crossSource = Path.Combine(root, "cross-device.txt")

                    let crossData =
                        Path.Combine("/dev/shm", $"dotnet-cli-plus-trash-{Guid.NewGuid():N}")

                    File.WriteAllText(crossSource, "preserve")

                    try
                        match (MutationTrash.CreateFreedesktop(crossData)).MoveToTrash crossSource with
                        | Error _ ->
                            Assert.True(File.Exists crossSource)
                            Assert.Empty(Directory.EnumerateFiles(Path.Combine(crossData, "Trash", "info")))
                        | Ok() -> Assert.True(true, "Cross-device failure probe is not applicable on this host.")
                    finally
                        if Directory.Exists crossData then
                            Directory.Delete(crossData, true)
            finally
                Directory.Delete(root, true)

    [<Fact>]
    member _.``apphost refuses a writable startup when recovery remains manual``() =
        let root = MutationTest.directory "mutation-startup"

        try
            let solution = Path.Combine(root, "Demo.slnx")
            MutationTest.writableSolution solution

            let clock = MutationTest.Clock(DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))

            let serialize (state: string) (roots: string array) (steps: JournalStep array) (version: int) =
                JsonSerializer.Serialize(
                    { Version = version
                      Id = "transaction"
                      State = state
                      Roots = roots
                      Steps = steps
                      CreatedUtc = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
                )

            let recover (name: string) (contents: string) (expected: MutationRecoveryDisposition) =
                let state = Path.Combine(root, name)
                Directory.CreateDirectory(state) |> ignore
                File.WriteAllText(Path.Combine(state, "transaction.json"), contents)
                Assert.Equal(expected, MutationCoordinator.Recover(state, clock))
                state

            recover "prepared" (serialize "prepared" [| root |] [||] 1) MutationRecoveryDisposition.Ready
            |> ignore

            recover "applied" (serialize "applied" [| root |] [||] 1) MutationRecoveryDisposition.Ready
            |> ignore

            let applying = Path.Combine(root, "applying")
            Directory.CreateDirectory(applying) |> ignore
            let destination = Path.Combine(applying, "target.txt")
            let backup = Path.Combine(applying, ".target.txt.dotnet-plus-backup-matrix")
            File.WriteAllText(destination, "changed")
            File.WriteAllText(backup, "original")

            let step =
                { Kind = "replace"
                  Source = ""
                  Destination = destination
                  Stage = Path.Combine(applying, ".target.txt.dotnet-plus-stage-matrix")
                  Backup = backup
                  Applied = true }

            File.WriteAllText(
                Path.Combine(applying, "transaction.json"),
                serialize "applying" [| applying |] [| step |] 1
            )

            Assert.Equal(MutationRecoveryDisposition.Ready, MutationCoordinator.Recover(applying, clock))
            Assert.Equal("original", File.ReadAllText(destination))

            recover
                "manual"
                (serialize "manual-recovery" [| root |] [||] 1)
                MutationRecoveryDisposition.PartialRecoveryRequired
            |> ignore

            recover
                "version"
                (serialize "prepared" [| root |] [||] 2)
                MutationRecoveryDisposition.PartialRecoveryRequired
            |> ignore

            recover
                "tampered"
                (serialize
                    "applying"
                    [| root |]
                    [| { Kind = "replace"
                         Source = ""
                         Destination = "/etc/passwd"
                         Stage = ""
                         Backup = ""
                         Applied = false } |]
                    1)
                MutationRecoveryDisposition.PartialRecoveryRequired
            |> ignore

            recover "malformed" "{" MutationRecoveryDisposition.PartialRecoveryRequired
            |> ignore

            File.WriteAllText(
                Path.Combine(root, "manual.json"),
                "{\"Version\":1,\"Id\":\"manual\",\"State\":\"manual-recovery\",\"Steps\":[],\"CreatedUtc\":\"2026-01-01T00:00:00+00:00\"}"
            )

            let retainedArtifact = Path.Combine(root, "retained.txt")
            File.WriteAllText(retainedArtifact, "old")

            let oldClock =
                MutationTest.Clock(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))

            let retentionRequest =
                MutationTest.request root [ WorkspaceArtifactPath.Create retainedArtifact ] [ MutationIntent.Overwrite ]

            let retentionCoordinator =
                MutationTest.coordinator root oldClock (MutationTest.RefusingTrash())

            let retentionPreview =
                match retentionCoordinator.Prepare retentionRequest with
                | Success value -> value
                | Failure failure -> failwithf "Preparation failed: %A" failure

            retentionCoordinator.Execute(
                retentionPreview.Token,
                retentionRequest,
                [ MutationAction.ReplaceFile(retainedArtifact, Encoding.UTF8.GetBytes "new") ]
            )
            |> function
                | Success() -> ()
                | Failure failure -> failwithf "Retention transaction failed: %A" failure

            MutationCoordinator.Recover(root, MutationTest.Clock(DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            |> ignore

            let remaining = Directory.EnumerateFiles(root, "*.json") |> Seq.toArray

            Assert.DoesNotContain(
                remaining,
                fun path ->
                    Path.GetFileName(path) <> "manual.json"
                    && File.ReadAllText(path).Contains("\"State\":\"completed\"")
            )

            let start = ProcessStartInfo(MutationTest.product)
            start.UseShellExecute <- false
            start.RedirectStandardError <- true
            start.RedirectStandardOutput <- true
            start.Environment["DOTNET_PLUS_STATE_ROOT"] <- root
            start.ArgumentList.Add "solution"
            start.ArgumentList.Add(solution)
            start.ArgumentList.Add "--pipe"

            use child =
                Process.Start(start)
                |> Option.ofObj
                |> Option.defaultWith (fun () -> failwith "The apphost did not start.")

            Assert.True(child.WaitForExit(10000), "The apphost did not finish startup recovery.")
            Assert.Equal(64, child.ExitCode)
            Assert.Contains("partial_recovery_required", child.StandardError.ReadToEnd())
        finally
            Directory.Delete(root, true)
