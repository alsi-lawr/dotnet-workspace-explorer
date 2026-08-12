namespace Dotnet.WorkspaceExplorer

open System
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex

type internal WorkspaceGitStatus(workspacePath: string) =
    let gate = new SemaphoreSlim(1, 1)
    let mutable previous: GitStatusSnapshot option = None
    let mutable revision = 0L

    let outputLimit = 4 * 1024 * 1024

    let readPathSnapshotAsync cancellationToken =
        task {
            let solutionDirectory =
                Path.GetDirectoryName workspacePath
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let! worktree =
                WorkspaceGitProcess.runAsync
                    "git"
                    solutionDirectory
                    [ "rev-parse"; "--show-toplevel" ]
                    (16 * 1024)
                    cancellationToken

            match worktree with
            | Error error -> return Error error
            | Ok(exitCode, _, _) when exitCode <> 0 -> return Ok None
            | Ok(_, output, _) ->
                let root = output.TrimEnd('\r', '\n')

                if String.IsNullOrEmpty root || not (Directory.Exists root) then
                    return
                        Error(
                            RpcErrors.create
                                "git_parse_failed"
                                "Git returned an invalid worktree root."
                                None
                        )
                else
                    let! status =
                        WorkspaceGitProcess.runAsync
                            "git"
                            root
                            [ "status"
                              "--porcelain=v1"
                              "-z"
                              "--untracked-files=all"
                              "--ignored=matching"
                              "--ignore-submodules=all"
                              "--"
                              "." ]
                            outputLimit
                            cancellationToken

                    match status with
                    | Error error -> return Error error
                    | Ok(exitCode, _, error) when exitCode <> 0 ->
                        return
                            Error(
                                RpcErrors.create
                                    "git_status_failed"
                                    (if String.IsNullOrWhiteSpace error then
                                         "Git status failed."
                                     else
                                         "Git status failed safely.")
                                    None
                            )
                    | Ok(_, output, _) ->
                        return
                            WorkspaceGitStatusParsing.parsePorcelain root output |> Result.map Some
        }

    let withGate operation (cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                return! operation cancellationToken
            finally
                gate.Release() |> ignore
        }

    member _.ReadPathSnapshotAsync(cancellationToken: CancellationToken) =
        withGate readPathSnapshotAsync cancellationToken

    member _.ReadAsync
        (state: WorkspaceIndex, expectedRevision: int64, cancellationToken: CancellationToken)
        =
        withGate
            (fun cancellationToken ->
                task {
                    let! indexed = state.GitNodesAsync(expectedRevision, cancellationToken)

                    match indexed with
                    | Error error -> return Error error
                    | Ok(workspaceRevision, nodes) ->
                        let! acquired = readPathSnapshotAsync cancellationToken

                        let snapshot =
                            acquired
                            |> Result.bind (function
                                | None ->
                                    Ok
                                        { Available = false
                                          Decorations = [||] }
                                | Some pathSnapshot ->
                                    WorkspaceGitStatusMapping.mapDecorations
                                        workspacePath
                                        nodes
                                        pathSnapshot)

                        match snapshot with
                        | Error error -> return Error error
                        | Ok snapshot ->
                            let changed =
                                match previous with
                                | None -> true
                                | Some previousSnapshot -> previousSnapshot <> snapshot

                            if changed then
                                revision <- revision + 1L

                            previous <- Some snapshot

                            return
                                Ok(
                                    WorkspaceRpcResponses.gitStatusResult
                                        snapshot.Available
                                        workspaceRevision
                                        revision
                                        (snapshot.Decorations
                                         |> Seq.map (fun (nodeId, states) ->
                                             nodeId,
                                             states
                                             |> Seq.map (function
                                                 | Staged -> "staged"
                                                 | Unstaged -> "unstaged"
                                                 | Renamed -> "renamed"
                                                 | Deleted -> "deleted"
                                                 | Unmerged -> "unmerged"
                                                 | Untracked -> "untracked"
                                                 | Ignored -> "ignored")))
                                )
                })
            cancellationToken
