namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.Diagnostics
open System.IO
open System
open System.Threading
open Dotnet.WorkspaceExplorer
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Microsoft.VisualStudio.SolutionPersistence.Model
open Xunit

module private WorkspaceGitStatusScenario =
    let runGit directory arguments =
        let start = ProcessStartInfo "git"
        start.WorkingDirectory <- directory
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true

        for argument in arguments do
            start.ArgumentList.Add argument

        use child = Process.Start start
        child.WaitForExit()

        if child.ExitCode <> 0 then
            failwith (child.StandardError.ReadToEnd())

    let initialize capabilities =
        WorkspaceRpcScenario.map
            [ "protocolVersion",
              WorkspaceRpcScenario.map
                  [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
              "clientInfo", WorkspaceRpcScenario.map [ "name", RpcValue.String "git-status-test" ]
              "capabilities", capabilities |> Seq.map RpcValue.String |> RpcValue.array
              "limits",
              WorkspaceRpcScenario.map
                  [ "maxFrameBytes", RpcValue.Integer 65536L
                    "maxPageSize", RpcValue.Integer 50L ] ]

    let request child requestId expectedRevision =
        WorkspaceRpcScenario.send
            child
            false
            (WorkspaceRpcScenario.request
                requestId
                "workspace/git/status"
                (WorkspaceRpcScenario.map [ "expectedRevision", RpcValue.Integer expectedRevision ]))

        WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response requestId

    let private deleteRepository directory =
        if OperatingSystem.IsWindows() then
            Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            |> Seq.append [ directory ]
            |> Seq.iter (fun path ->
                let attributes = File.GetAttributes path

                if attributes.HasFlag FileAttributes.ReadOnly then
                    File.SetAttributes(path, attributes &&& ~~~FileAttributes.ReadOnly))

        Directory.Delete(directory, true)

    let withRepository action =
        let directory = WorkspaceRpcScenario.temporaryDirectory "git-status"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.csproj")
            let model = SolutionModel()
            model.AddProject(Path.GetFileName project, "Demo", null) |> ignore
            WorkspaceRpcScenario.writeProject project
            WorkspaceRpcScenario.save solution model
            runGit directory [ "init"; "--quiet" ]
            runGit directory [ "config"; "user.email"; "test@example.invalid" ]
            runGit directory [ "config"; "user.name"; "Test" ]
            runGit directory [ "add"; "." ]
            runGit directory [ "commit"; "--quiet"; "-m"; "baseline" ]
            action directory solution project
        finally
            deleteRepository directory

[<Collection("Workspace scenarios")>]
type WorkspaceGitStatusTests() =
    [<Fact>]
    member _.``Git process launch and nonzero execution remain bounded structured failure inputs``
        ()
        =
        let missing =
            WorkspaceGitProcess.runAsync
                $"missing-git-{Guid.NewGuid():N}"
                (Path.GetTempPath())
                []
                1024
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match missing with
        | Error error -> error.Code |> should equal "git_launch_failed"
        | success -> failwithf "A missing Git executable unexpectedly launched: %A" success

        let failed =
            WorkspaceGitProcess.runAsync
                "git"
                (Path.GetTempPath())
                [ "--definitely-not-a-git-option" ]
                (64 * 1024)
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match failed with
        | Ok(exitCode, _, error) ->
            (exitCode <> 0) |> should equal true
            String.IsNullOrWhiteSpace error |> should equal false
        | Error error ->
            failwithf "Git execution did not reach its bounded exit result: %s" error.Code

    [<Fact>]
    member _.``Git mapping projects all six non-ignored states through ancestors while ignored remains exact-path only``
        ()
        =
        let root = WorkspaceRpcScenario.temporaryDirectory "git-mapping"

        try
            let projectDirectory = Path.Combine(root, "Project")
            let folderDirectory = Path.Combine(projectDirectory, "Feature")
            let changedFile = Path.Combine(folderDirectory, "Changed.cs")
            let addedFile = Path.Combine(folderDirectory, "Added.cs")
            let ignoredDirectory = Path.Combine(projectDirectory, "obj")
            let ignoredFile = Path.Combine(ignoredDirectory, "Generated.cs")
            let ignoredDescendant = Path.Combine(ignoredDirectory, "nested", "Output.dll")
            Directory.CreateDirectory folderDirectory |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName ignoredDescendant) |> ignore
            File.WriteAllText(changedFile, "changed")
            File.WriteAllText(addedFile, "added")
            File.WriteAllText(ignoredFile, "ignored")
            File.WriteAllText(ignoredDescendant, "ignored descendant")

            let node id parent physical container =
                { NodeId = WorkspaceNodeId.Parse id
                  ParentNodeId = parent |> Option.map WorkspaceNodeId.Parse
                  PhysicalPath = physical |> Option.map WorkspaceArtifactPath.Create
                  ContainerPath = container |> Option.map WorkspaceArtifactPath.Create }

            let nodes =
                [| node "workspace" None None (Some root)
                   node "project" (Some "workspace") None (Some projectDirectory)
                   node "folder" (Some "project") None (Some folderDirectory)
                   node "file" (Some "folder") (Some changedFile) None
                   node "ignored-folder" (Some "project") None (Some ignoredDirectory)
                   node "ignored-file" (Some "ignored-folder") (Some ignoredFile) None |]

            match
                WorkspaceGitStatusMapping.mapDecorations
                    root
                    nodes
                    { RepositoryRoot = root
                      Entries =
                        [| { Path = changedFile
                             States = [| Unstaged; Renamed |] }
                           { Path = addedFile
                             States = [| Staged; Untracked |] }
                           { Path = Path.Combine(folderDirectory, "Deleted.cs")
                             States = [| Deleted; Unmerged |] } |] }
            with
            | Error error -> failwithf "Valid Git paths did not map: %s" error.Code
            | Ok snapshot ->

                snapshot.Decorations
                |> should
                    equal
                    [| "file", [| GitStatusState.Unstaged; GitStatusState.Renamed |]
                       "folder",
                       [| GitStatusState.Staged
                          GitStatusState.Unstaged
                          GitStatusState.Renamed
                          GitStatusState.Deleted
                          GitStatusState.Unmerged
                          GitStatusState.Untracked |]
                       "project",
                       [| GitStatusState.Staged
                          GitStatusState.Unstaged
                          GitStatusState.Renamed
                          GitStatusState.Deleted
                          GitStatusState.Unmerged
                          GitStatusState.Untracked |]
                       "workspace",
                       [| GitStatusState.Staged
                          GitStatusState.Unstaged
                          GitStatusState.Renamed
                          GitStatusState.Deleted
                          GitStatusState.Unmerged
                          GitStatusState.Untracked |] |]

            match
                WorkspaceGitStatusMapping.mapDecorations
                    root
                    nodes
                    { RepositoryRoot = root
                      Entries =
                        [| { Path = ignoredDirectory
                             States = [| Ignored |] }
                           { Path = ignoredFile
                             States = [| Ignored |] }
                           { Path = ignoredDescendant
                             States = [| Ignored |] } |] }
            with
            | Error error -> failwithf "Valid ignored Git paths did not map: %s" error.Code
            | Ok snapshot ->

                snapshot.Decorations
                |> should
                    equal
                    [| "ignored-file", [| GitStatusState.Ignored |]
                       "ignored-folder", [| GitStatusState.Ignored |] |]

            match
                WorkspaceGitStatusMapping.mapDecorations
                    root
                    nodes
                    { RepositoryRoot = root
                      Entries =
                        [| { Path = String [| '\u0000' |]
                             States = [| Unstaged |] } |] }
            with
            | Error error -> error.Code |> should equal "git_mapping_failed"
            | Ok snapshot -> failwithf "An invalid Git path unexpectedly mapped: %A" snapshot
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``real Git ignored directory output with a trailing separator decorates only the exact semantic and Add Existing directory rows``
        ()
        =
        WorkspaceGitStatusScenario.withRepository (fun directory solution project ->
            let ignoredDirectory = Path.Combine(directory, "ignored")
            File.WriteAllText(Path.Combine(directory, ".gitignore"), "ignored/\n")
            WorkspaceGitStatusScenario.runGit directory [ "add"; ".gitignore" ]
            WorkspaceGitStatusScenario.runGit directory [ "commit"; "--quiet"; "-m"; "ignore" ]
            Directory.CreateDirectory ignoredDirectory |> ignore
            File.WriteAllText(Path.Combine(ignoredDirectory, "Generated.cs"), "ignored")

            let pathSnapshot =
                let status = WorkspaceGitStatus solution

                match status.ReadPathSnapshotAsync(CancellationToken.None).Result with
                | Ok(Some snapshot) -> snapshot
                | Ok None -> failwith "The real Git repository was reported as unavailable."
                | Error error ->
                    failwithf "Real Git status acquisition failed: %s: %s" error.Code error.Message

            let ignoredEntry =
                pathSnapshot.Entries
                |> Array.find (fun entry ->
                    entry.States = [| GitStatusState.Ignored |]
                    && Path.GetFileName(Path.TrimEndingDirectorySeparator entry.Path) = "ignored")

            Path.EndsInDirectorySeparator ignoredEntry.Path |> should equal true

            let node id parent physical container =
                { NodeId = WorkspaceNodeId.Parse id
                  ParentNodeId = parent |> Option.map WorkspaceNodeId.Parse
                  PhysicalPath = physical |> Option.map WorkspaceArtifactPath.Create
                  ContainerPath = container |> Option.map WorkspaceArtifactPath.Create }

            let nodes =
                [| node "workspace" None None (Some directory)
                   node "project" (Some "workspace") (Some project) (Some directory)
                   node "ignored-folder" (Some "project") None (Some ignoredDirectory) |]

            match WorkspaceGitStatusMapping.mapDecorations solution nodes pathSnapshot with
            | Error error ->
                failwithf "Trailing-separator Git mapping failed: %s: %s" error.Code error.Message
            | Ok snapshot ->
                snapshot.Decorations
                |> should equal [| "ignored-folder", [| GitStatusState.Ignored |] |]

            let mixedSnapshot =
                { pathSnapshot with
                    Entries =
                        [| { ignoredEntry with
                               States = [| GitStatusState.Untracked; GitStatusState.Ignored |] } |] }

            match WorkspaceGitStatusMapping.mapDecorations solution nodes mixedSnapshot with
            | Error error ->
                failwithf
                    "Mixed trailing-separator mapping failed: %s: %s"
                    error.Code
                    error.Message
            | Ok snapshot ->
                snapshot.Decorations
                |> should
                    equal
                    [| "ignored-folder", [| GitStatusState.Untracked; GitStatusState.Ignored |]
                       "project", [| GitStatusState.Untracked |]
                       "workspace", [| GitStatusState.Untracked |] |]

            let workspace =
                match SolutionWorkspaceReader.OpenAsync(solution).Result with
                | Success value -> value
                | Failure failure -> failwithf "The test workspace did not open: %A" failure

            let state = WorkspaceIndex.CreateProduction(solution, workspace, 1)

            try
                let rootNode =
                    WorkspaceNode.Create(
                        workspace.Descriptor,
                        WorkspaceNodeKind.Workspace,
                        WorkspaceNodeIdentity.Create "root",
                        "Demo",
                        WorkspaceCapabilityProfile.Full
                    )

                let target: WorkspaceSemanticContext =
                    { Node = rootNode
                      ProjectId = None
                      ProjectPath = None
                      PhysicalPath = None
                      PhysicalDirectory = None
                      LogicalFolderId = None
                      LogicalFolderPath = None }

                use selector =
                    new AddExistingSelector(
                        (fun () -> 4096),
                        TimeProvider.System,
                        fun _ -> System.Threading.Tasks.Task.FromResult(Ok(Some pathSnapshot))
                    )

                let started =
                    match
                        selector
                            .StartAsync(
                                workspace,
                                state,
                                target,
                                "add-existing",
                                state.Revision,
                                Some 4096,
                                true,
                                CancellationToken.None
                            )
                            .Result
                    with
                    | Ok value -> value
                    | Error error ->
                        failwithf
                            "Trailing-separator selector start failed: %s: %s"
                            error.Code
                            error.Message

                WorkspaceRpcScenario.field "root" started
                |> WorkspaceRpcScenario.field "gitStates"
                |> RpcValue.requireArray "gitStates"
                |> should be Empty

                let ignoredDirectoryEntry =
                    WorkspaceRpcScenario.field "entries" started
                    |> RpcValue.requireArray "entries"
                    |> Seq.find (fun entry ->
                        WorkspaceRpcScenario.field "displayName" entry = RpcValue.String "ignored")

                WorkspaceRpcScenario.field "gitStates" ignoredDirectoryEntry
                |> RpcValue.requireArray "gitStates"
                |> Seq.toArray
                |> should equal [| RpcValue.String "ignored" |]
            finally
                state.DisposeAsync().GetAwaiter().GetResult())

    [<Fact>]
    member _.``Git mapping follows the workspace filesystem case-sensitivity contract``() =
        let root = WorkspaceRpcScenario.temporaryDirectory "git-case-mapping"

        try
            let physicalPath = Path.Combine(root, "CaseSensitive.cs")
            File.WriteAllText(physicalPath, "case")

            let differentlyCasedPath = Path.Combine(root, "casesensitive.cs")

            let nodes =
                [| { NodeId = WorkspaceNodeId.Parse "file"
                     ParentNodeId = None
                     PhysicalPath = Some(WorkspaceArtifactPath.Create physicalPath)
                     ContainerPath = None } |]

            let snapshot =
                { RepositoryRoot = root
                  Entries =
                    [| { Path = differentlyCasedPath
                         States = [| GitStatusState.Unstaged |] } |] }

            match WorkspaceGitStatusMapping.mapDecorations root nodes snapshot with
            | Error error -> failwithf "Case-aware Git mapping failed: %s" error.Code
            | Ok mapped ->
                let expected =
                    match FileSystemCaseSensitivityDetector.DetectFromExistingPath root with
                    | FileSystemCaseSensitivity.Insensitive ->
                        [| "file", [| GitStatusState.Unstaged |] |]
                    | _ -> [||]

                mapped.Decorations |> should equal expected
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``NUL-delimited Git porcelain preserves spaces and both ordered rename paths``() =
        let root = Path.GetFullPath(Path.GetTempPath())

        let parsed =
            WorkspaceGitStatusParsing.parsePorcelain
                root
                "?? File With Spaces.cs\u0000R  Renamed File.cs\u0000Original File.cs\u0000"

        match parsed with
        | Error error -> failwithf "Expected valid porcelain, got %s" error.Code
        | Ok snapshot ->
            snapshot.RepositoryRoot |> should equal root
            snapshot.Entries.Length |> should equal 3

            snapshot.Entries
            |> Array.map _.Path
            |> should
                equal
                [| Path.GetFullPath("File With Spaces.cs", root)
                   Path.GetFullPath("Original File.cs", root)
                   Path.GetFullPath("Renamed File.cs", root) |]

            snapshot.Entries
            |> Array.find (fun entry -> entry.Path = Path.GetFullPath("File With Spaces.cs", root))
            |> _.States
            |> should equal [| GitStatusState.Untracked |]

            snapshot.Entries
            |> Array.filter (fun entry ->
                entry.Path <> Path.GetFullPath("File With Spaces.cs", root))
            |> Array.iter (fun entry -> entry.States |> should equal [| GitStatusState.Renamed |])

    [<Fact>]
    member _.``Every supported porcelain XY state maps to the complete ordered Git state model``() =
        let root = Path.GetFullPath(Path.GetTempPath())

        let cases =
            [ "M ", [| GitStatusState.Staged |]
              " M", [| GitStatusState.Unstaged |]
              "C ", [| GitStatusState.Staged |]
              " C", [| GitStatusState.Unstaged |]
              "CM", [| GitStatusState.Staged; GitStatusState.Unstaged |]
              " T", [| GitStatusState.Unstaged |]
              "T ", [| GitStatusState.Staged |]
              "TM", [| GitStatusState.Staged; GitStatusState.Unstaged |]
              "MT", [| GitStatusState.Staged; GitStatusState.Unstaged |]
              "TT", [| GitStatusState.Staged; GitStatusState.Unstaged |]
              "TD", [| GitStatusState.Staged; GitStatusState.Deleted |]
              "MM", [| GitStatusState.Staged; GitStatusState.Unstaged |]
              "MD", [| GitStatusState.Staged; GitStatusState.Deleted |]
              "A ", [| GitStatusState.Staged |]
              "AD", [| GitStatusState.Staged; GitStatusState.Deleted |]
              "AT", [| GitStatusState.Staged; GitStatusState.Unstaged |]
              " A", [| GitStatusState.Untracked |]
              "AA", [| GitStatusState.Unmerged; GitStatusState.Untracked |]
              "AU", [| GitStatusState.Unmerged; GitStatusState.Untracked |]
              "AM", [| GitStatusState.Staged; GitStatusState.Unstaged |]
              "??", [| GitStatusState.Untracked |]
              "R ", [| GitStatusState.Renamed |]
              " R", [| GitStatusState.Renamed |]
              "RM", [| GitStatusState.Unstaged; GitStatusState.Renamed |]
              "RT", [| GitStatusState.Unstaged; GitStatusState.Renamed |]
              "CD", [| GitStatusState.Staged; GitStatusState.Deleted |]
              "CT", [| GitStatusState.Staged; GitStatusState.Unstaged |]
              "UU", [| GitStatusState.Unmerged |]
              "UD", [| GitStatusState.Unmerged |]
              "UA", [| GitStatusState.Unmerged |]
              " D", [| GitStatusState.Deleted |]
              "D ", [| GitStatusState.Deleted |]
              "DA", [| GitStatusState.Unstaged |]
              "RD", [| GitStatusState.Renamed; GitStatusState.Deleted |]
              "DD", [| GitStatusState.Deleted |]
              "DU", [| GitStatusState.Deleted; GitStatusState.Unmerged |]
              "!!", [| GitStatusState.Ignored |] ]

        for status, expectedStates in cases do
            let renameOrCopy = status.Contains 'R' || status.Contains 'C'

            let output =
                $"{status} Target.cs\u0000" + if renameOrCopy then "Source.cs\u0000" else ""

            match WorkspaceGitStatusParsing.parsePorcelain root output with
            | Error error ->
                failwithf "Expected porcelain state %A to parse, got %s" status error.Code
            | Ok snapshot ->
                snapshot.Entries
                |> Array.find (fun entry -> Path.GetFileName entry.Path = "Target.cs")
                |> _.States
                |> should equal expectedStates

    [<Fact>]
    member _.``Porcelain parsing rejects malformed records and incomplete rename or copy pairs``() =
        let root = Path.GetFullPath(Path.GetTempPath())

        [ "M  missing-final-nul"
          "M\u0000"
          "ZZ Unknown.cs\u0000"
          "R  Renamed.cs\u0000"
          "C  Copied.cs\u0000"
          "R  Renamed.cs\u0000\u0000" ]
        |> List.iter (fun output ->
            match WorkspaceGitStatusParsing.parsePorcelain root output with
            | Error error -> error.Code |> should equal "git_parse_failed"
            | Ok snapshot -> failwithf "Malformed porcelain unexpectedly parsed: %A" snapshot)

    [<Fact>]
    member _.``Duplicate porcelain paths produce one stable ordered union``() =
        let root = Path.GetFullPath(Path.GetTempPath())

        match
            WorkspaceGitStatusParsing.parsePorcelain
                root
                "M  Same.cs\u0000 M Same.cs\u0000?? Same.cs\u0000"
        with
        | Error error -> failwithf "Expected duplicate paths to union, got %s" error.Code
        | Ok snapshot ->
            snapshot.Entries.Length |> should equal 1

            snapshot.Entries[0].States
            |> should
                equal
                [| GitStatusState.Staged; GitStatusState.Unstaged; GitStatusState.Untracked |]

    [<Fact>]
    member _.``Git output reading rejects content beyond its bound after draining the reader``() =
        use reader = new StringReader(String('x', 1025))

        let result =
            WorkspaceGitProcess.readBoundedAsync reader 1024 CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match result with
        | Error() -> ()
        | Ok _ -> failwith "Expected the oversized Git output to be rejected."

        reader.ReadToEnd() |> should equal String.Empty

    [<Fact>]
    member _.``Reusable Git path snapshots include all untracked files and matching ignored entries``
        ()
        =
        WorkspaceGitStatusScenario.withRepository (fun directory solution _ ->
            let ignored = Path.Combine(directory, "private.ignored")
            let untracked = Path.Combine(directory, "File With Spaces.cs")
            File.WriteAllText(Path.Combine(directory, ".gitignore"), "*.ignored\n")
            WorkspaceGitStatusScenario.runGit directory [ "add"; ".gitignore" ]
            WorkspaceGitStatusScenario.runGit directory [ "commit"; "--quiet"; "-m"; "ignore" ]
            File.WriteAllText(ignored, "ignored")
            File.WriteAllText(untracked, "untracked")

            let acquired =
                WorkspaceGitStatus(solution).ReadPathSnapshotAsync(CancellationToken.None)
                |> _.GetAwaiter().GetResult()

            match acquired with
            | Error error -> failwithf "Git path snapshot failed: %s" error.Code
            | Ok None -> failwith "The initialized repository was reported unavailable."
            | Ok(Some snapshot) ->
                snapshot.RepositoryRoot |> should equal (Path.GetFullPath directory)

                snapshot.Entries
                |> Array.map (fun entry -> entry.Path, entry.States)
                |> should
                    equal
                    [| untracked, [| GitStatusState.Untracked |]
                       ignored, [| GitStatusState.Ignored |] |])


    [<Fact>]
    member _.``Git process cancellation propagates without translating the caller cancellation``() =
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        (fun () ->
            WorkspaceGitProcess.runAsync
                "git"
                (Path.GetTempPath())
                [ "--version" ]
                1024
                cancellation.Token
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> should throw typeof<OperationCanceledException>

    [<Fact>]
    member _.``An incomplete Git rename record returns the structured parse error``() =
        let result =
            WorkspaceGitStatusParsing.parsePorcelain
                (Path.GetFullPath(Path.GetTempPath()))
                "R  Renamed File.cs\u0000"

        match result with
        | Error error -> error.Code |> should equal "git_parse_failed"
        | Ok _ -> failwith "Expected the incomplete rename record to be rejected."

    [<Fact>]
    member _.``Git status outside a worktree returns one stable unavailable snapshot``() =
        let directory =
            Path.Combine(
                Path.GetTempPath(),
                $"dotnet-workspace-explorer-git-status-unavailable-{System.Guid.NewGuid():N}"
            )

        Directory.CreateDirectory directory |> ignore

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let project = Path.Combine(directory, "Demo.csproj")
            let model = SolutionModel()
            model.AddProject(Path.GetFileName project, "Demo", null) |> ignore
            WorkspaceRpcScenario.writeProject project
            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "git-status-unavailable" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        1u
                        "initialize"
                        (WorkspaceGitStatusScenario.initialize [ "workspace.git.status" ]))

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                let firstError, first = WorkspaceGitStatusScenario.request child 2u 0L
                firstError |> should equal None

                WorkspaceRpcScenario.field "available" first
                |> should equal (RpcValue.Boolean false)

                WorkspaceRpcScenario.field "decorations" first
                |> RpcValue.requireArray "decorations"
                |> should be Empty

                WorkspaceRpcScenario.field "statusRevision" first
                |> RpcValue.requireInteger "statusRevision"
                |> should equal 1L

                let repeatedError, repeated = WorkspaceGitStatusScenario.request child 3u 0L
                repeatedError |> should equal None

                WorkspaceRpcScenario.field "statusRevision" repeated
                |> RpcValue.requireInteger "statusRevision"
                |> should equal 1L

                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``Git status requires negotiated capability without changing the workspace``() =
        WorkspaceGitStatusScenario.withRepository (fun _ solution _ ->
            use child = WorkspaceRpcScenario.startWorkspaceRpc "git-status-capability" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        1u
                        "initialize"
                        (WorkspaceGitStatusScenario.initialize [ "workspace.root" ]))

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                let error, _ = WorkspaceGitStatusScenario.request child 2u 0L
                error |> Option.map _.Code |> should equal (Some "unsupported_capability")
                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child)

    [<Fact>]
    member _.``Git status ignores a dirty nested repository recorded as a submodule``() =
        WorkspaceGitStatusScenario.withRepository (fun directory solution _ ->
            let nested = Path.Combine(directory, "Nested")
            Directory.CreateDirectory nested |> ignore
            WorkspaceGitStatusScenario.runGit nested [ "init"; "--quiet" ]

            WorkspaceGitStatusScenario.runGit
                nested
                [ "config"; "user.email"; "test@example.invalid" ]

            WorkspaceGitStatusScenario.runGit nested [ "config"; "user.name"; "Test" ]
            let nestedFile = Path.Combine(nested, "Nested.cs")
            File.WriteAllText(nestedFile, "class Nested {}")
            WorkspaceGitStatusScenario.runGit nested [ "add"; "." ]

            WorkspaceGitStatusScenario.runGit
                nested
                [ "commit"; "--quiet"; "-m"; "nested baseline" ]

            WorkspaceGitStatusScenario.runGit directory [ "add"; "Nested" ]
            WorkspaceGitStatusScenario.runGit directory [ "commit"; "--quiet"; "-m"; "add nested" ]
            File.AppendAllText(nestedFile, "\n// dirty\n")
            use child = WorkspaceRpcScenario.startWorkspaceRpc "git-status-submodule" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        1u
                        "initialize"
                        (WorkspaceGitStatusScenario.initialize [ "workspace.git.status" ]))

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> fst
                |> should equal None

                let error, result = WorkspaceGitStatusScenario.request child 2u 0L
                error |> should equal None

                WorkspaceRpcScenario.field "available" result
                |> should equal (RpcValue.Boolean true)

                WorkspaceRpcScenario.field "decorations" result
                |> RpcValue.requireArray "decorations"
                |> should be Empty

                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child)

    [<Fact>]
    member _.``Git status returns ordered non-ignored ancestor states``() =
        WorkspaceGitStatusScenario.withRepository (fun directory solution project ->
            let ignored = Path.Combine(directory, "private.ignored")
            let untracked = Path.Combine(directory, "Untracked.cs")
            File.WriteAllText(Path.Combine(directory, ".gitignore"), "*.ignored\n")
            WorkspaceGitStatusScenario.runGit directory [ "add"; ".gitignore" ]
            WorkspaceGitStatusScenario.runGit directory [ "commit"; "--quiet"; "-m"; "ignore" ]
            File.AppendAllText(project, "\n<!-- staged -->\n")
            WorkspaceGitStatusScenario.runGit directory [ "add"; Path.GetFileName project ]
            File.AppendAllText(project, "\n<!-- unstaged -->\n")
            File.WriteAllText(ignored, "ignored")
            File.WriteAllText(untracked, "untracked")

            let expectedStates =
                [ RpcValue.String "staged"
                  RpcValue.String "unstaged"
                  RpcValue.String "untracked" ]

            for name, capabilities in [ "git-status", [ "workspace.git.status" ] ] do
                use child = WorkspaceRpcScenario.startWorkspaceRpc name solution

                try
                    WorkspaceRpcScenario.send
                        child
                        false
                        (WorkspaceRpcScenario.request
                            1u
                            "initialize"
                            (WorkspaceGitStatusScenario.initialize capabilities))

                    let initializeError, initialized =
                        WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

                    initializeError |> should equal None

                    initialized
                    |> WorkspaceRpcScenario.field "capabilities"
                    |> RpcValue.requireArray "capabilities"
                    |> Seq.contains (RpcValue.String "workspace.git.status")
                    |> should equal true

                    let firstError, first = WorkspaceGitStatusScenario.request child 2u 0L
                    firstError |> should equal None

                    RpcValue.requireMap "git.status" first
                    |> _.Keys
                    |> Seq.sort
                    |> Seq.toList
                    |> should
                        equal
                        [ "available"; "decorations"; "statusRevision"; "workspaceRevision" ]

                    let decorations =
                        WorkspaceRpcScenario.field "decorations" first
                        |> RpcValue.requireArray "decorations"

                    decorations
                    |> Seq.iter (fun decoration ->
                        RpcValue.requireMap "git.status.decoration" decoration
                        |> _.Keys
                        |> Seq.sort
                        |> Seq.toList
                        |> should equal [ "nodeId"; "states" ]

                        WorkspaceRpcScenario.field "states" decoration
                        |> RpcValue.requireArray "states"
                        |> should not' (be Empty))

                    decorations
                    |> Seq.exists (fun decoration ->
                        let states =
                            WorkspaceRpcScenario.field "states" decoration
                            |> RpcValue.requireArray "states"
                            |> Seq.toList

                        states = expectedStates)
                    |> should equal true

                    decorations
                    |> Seq.collect (fun decoration ->
                        WorkspaceRpcScenario.field "states" decoration
                        |> RpcValue.requireArray "states")
                    |> should not' (contain (RpcValue.String "ignored"))

                    let repeatedError, repeated = WorkspaceGitStatusScenario.request child 3u 0L

                    repeatedError |> should equal None

                    WorkspaceRpcScenario.field "statusRevision" repeated
                    |> RpcValue.requireInteger "statusRevision"
                    |> should equal 1L

                    WorkspaceRpcScenario.shutdown child 99u
                finally
                    WorkspaceRpcScenario.disposeProcess child)

    [<Fact>]
    member _.``Git status is deterministic revisioned and preserves complete states``() =
        WorkspaceGitStatusScenario.withRepository (fun directory solution project ->
            File.AppendAllText(project, "\n<!-- changed -->\n")
            let added = Path.Combine(directory, "New.cs")
            File.WriteAllText(added, "class New {}")
            use child = WorkspaceRpcScenario.startWorkspaceRpc "git-status-revisions" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        1u
                        "initialize"
                        (WorkspaceGitStatusScenario.initialize
                            [ "workspace.root"; "workspace.git.status" ]))

                let initializeError, initialized =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 1u

                initializeError |> should equal None

                initialized
                |> WorkspaceRpcScenario.field "capabilities"
                |> RpcValue.requireArray "capabilities"
                |> Seq.contains (RpcValue.String "workspace.git.status")
                |> should equal true

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 2u "workspace/root" RpcValue.emptyMap)

                let rootError, root =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 2u

                rootError |> should equal None

                let initialWorkspaceRevision =
                    WorkspaceRpcScenario.field "revision" root
                    |> RpcValue.requireInteger "revision"

                let projectId =
                    WorkspaceRpcScenario.rootChildren child 3u root
                    |> WorkspaceRpcScenario.field "nodes"
                    |> RpcValue.requireArray "nodes"
                    |> Seq.find (fun node ->
                        WorkspaceRpcScenario.field "name" node = RpcValue.String "Demo")
                    |> WorkspaceRpcScenario.field "id"
                    |> RpcValue.requireString "id"

                let staleError, _ = WorkspaceGitStatusScenario.request child 40u 1L
                staleError |> Option.map _.Code |> should equal (Some "workspace_conflict")

                let firstError, first = WorkspaceGitStatusScenario.request child 4u 0L
                firstError |> should equal None
                let firstFields = RpcValue.requireMap "git.status" first

                firstFields.Keys
                |> Seq.sort
                |> Seq.toList
                |> should
                    equal
                    [ "available"; "decorations"; "statusRevision"; "workspaceRevision" ]

                firstFields["available"] |> should equal (RpcValue.Boolean true)

                firstFields["workspaceRevision"]
                |> RpcValue.requireInteger "workspaceRevision"
                |> should equal 0L

                firstFields["statusRevision"]
                |> RpcValue.requireInteger "statusRevision"
                |> should equal 1L

                let projectDecoration =
                    firstFields["decorations"]
                    |> RpcValue.requireArray "decorations"
                    |> Seq.find (fun decoration ->
                        (WorkspaceRpcScenario.field "nodeId" decoration) = RpcValue.String
                            projectId)

                RpcValue.requireMap "git.decoration" projectDecoration
                |> _.Keys
                |> Seq.sort
                |> Seq.toList
                |> should equal [ "nodeId"; "states" ]

                WorkspaceRpcScenario.field "states" projectDecoration
                |> RpcValue.requireArray "states"
                |> Seq.toList
                |> should equal [ RpcValue.String "unstaged"; RpcValue.String "untracked" ]

                let repeatedError, repeated = WorkspaceGitStatusScenario.request child 5u 0L
                repeatedError |> should equal None

                WorkspaceRpcScenario.field "statusRevision" repeated
                |> RpcValue.requireInteger "statusRevision"
                |> should equal 1L

                File.Delete added
                let changedError, changed = WorkspaceGitStatusScenario.request child 6u 0L
                changedError |> should equal None

                WorkspaceRpcScenario.field "statusRevision" changed
                |> RpcValue.requireInteger "statusRevision"
                |> should equal 2L

                let changedProject =
                    changed
                    |> WorkspaceRpcScenario.field "decorations"
                    |> RpcValue.requireArray "decorations"
                    |> Seq.find (fun decoration ->
                        (WorkspaceRpcScenario.field "nodeId" decoration) = RpcValue.String
                            projectId)

                WorkspaceRpcScenario.field "states" changedProject
                |> RpcValue.requireArray "states"
                |> Seq.toList
                |> should equal [ RpcValue.String "unstaged" ]

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 7u "workspace/root" RpcValue.emptyMap)

                let afterGitError, afterGit =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 7u

                afterGitError |> should equal None

                WorkspaceRpcScenario.field "revision" afterGit
                |> RpcValue.requireInteger "revision"
                |> should equal initialWorkspaceRevision

                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child)
