namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.Diagnostics
open System.IO
open System
open System.Threading
open Dotnet.WorkspaceExplorer
open Dotnet.WorkspaceExplorer.Rpc
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
            Directory.Delete(directory, true)

[<Collection("Workspace scenarios")>]
type WorkspaceGitStatusTests() =
    [<Fact>]
    member _.``NUL-delimited Git porcelain preserves spaces and both rename paths``() =
        let root = Path.GetFullPath(Path.GetTempPath())

        let parsed =
            WorkspaceGitStatusParsing.parsePorcelain
                root
                "?? File With Spaces.cs\u0000R  Renamed File.cs\u0000Original File.cs\u0000"

        match parsed with
        | Error error -> failwithf "Expected valid porcelain, got %s" error.Code
        | Ok changes ->
            changes.Length |> should equal 3

            changes[0]
            |> should equal (Added, Path.GetFullPath("File With Spaces.cs", root))

            changes[1] |> should equal (Changed, Path.GetFullPath("Renamed File.cs", root))
            changes[2] |> should equal (Changed, Path.GetFullPath("Original File.cs", root))

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
    member _.``an incomplete Git rename record returns the structured parse error``() =
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
    member _.``Git status is deterministic revisioned and gives added state precedence``() =
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

                WorkspaceRpcScenario.field "state" projectDecoration
                |> should equal (RpcValue.String "added")

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

                WorkspaceRpcScenario.field "state" changedProject
                |> should equal (RpcValue.String "changed")

                WorkspaceRpcScenario.shutdown child 99u
            finally
                WorkspaceRpcScenario.disposeProcess child)
