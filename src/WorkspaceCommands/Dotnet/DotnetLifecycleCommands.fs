namespace Dotnet.WorkspaceExplorer.WorkspaceCommands

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing

open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

module internal DotnetLifecycleCommands =
    let private parameter id kind required name =
        CommandParameterDescriptor.Create(CommandParameterId.Create id, kind, required, name)

    let private descriptor id name targets parameters =
        CommandDescriptor.Create(CommandId.Create id, name, CommandAccess.Read, parameters, targets)

    let private extra =
        parameter "arguments" CommandParameterType.TextArray false "Additional dotnet arguments"

    let private noRestore =
        parameter "noRestore" CommandParameterType.Boolean false "Do not restore"

    let private workspaceAndProject =
        [ WorkspaceNodeKind.Workspace; WorkspaceNodeKind.Project ]

    let descriptors =
        ImmutableArray.CreateRange
            [ descriptor "dotnet.restore" "Restore" workspaceAndProject [ extra ]
              descriptor "dotnet.build" "Build" workspaceAndProject [ noRestore; extra ]
              descriptor "dotnet.test" "Test" workspaceAndProject [ extra ]
              descriptor "dotnet.run" "Run" [ WorkspaceNodeKind.Project ] [ noRestore; extra ] ]

    let tryDescribe id =
        descriptors |> Seq.tryFind (fun item -> item.Id = id)

    let discover (workspace: SolutionWorkspace) target =
        match target with
        | None ->
            descriptors
            |> Seq.filter (fun item -> item.Id.Value <> "dotnet.run")
            |> ImmutableArray.CreateRange
        | Some target when
            workspace.Contents.Projects
            |> Seq.exists (fun project -> project.Node.Id = target)
            ->
            descriptors
        | _ -> ImmutableArray<CommandDescriptor>.Empty


    let argv (workspace: SolutionWorkspace) (request: CommandMutationRequest) =
        match
            DotnetCommandArguments.extraArguments request.CommandId.Value request.Arguments,
            DotnetCommandArguments.noRestoreValue request.Arguments
        with
        | Error error, _
        | _, Error error -> Error error
        | Ok extra, Ok noRestore ->
            let project =
                request.TargetWorkspaceNodeId
                |> Option.bind (fun id ->
                    workspace.Contents.Projects |> Seq.tryFind (fun item -> item.Node.Id = id))

            match request.CommandId.Value, request.TargetWorkspaceNodeId, project with
            | "dotnet.restore", None, _ -> Ok([ "restore"; workspace.SolutionPath.Value ] @ extra)
            | "dotnet.restore", Some _, Some item ->
                Ok([ "restore"; item.Path.AbsolutePath.Value ] @ extra)
            | "dotnet.build", None, _ ->
                Ok(
                    [ "build"; workspace.SolutionPath.Value ]
                    @ (if noRestore then [ "--no-restore" ] else [])
                    @ extra
                )
            | "dotnet.build", Some _, Some item ->
                Ok(
                    [ "build"; item.Path.AbsolutePath.Value ]
                    @ (if noRestore then [ "--no-restore" ] else [])
                    @ extra
                )
            | "dotnet.test", None, _ -> Ok([ "test"; workspace.SolutionPath.Value ] @ extra)
            | "dotnet.test", Some _, Some item ->
                Ok([ "test"; item.Path.AbsolutePath.Value ] @ extra)
            | "dotnet.run", Some _, Some item ->
                Ok(
                    [ "run"; "--project"; item.Path.AbsolutePath.Value ]
                    @ (if noRestore then [ "--no-restore" ] else [])
                    @ extra
                )
            | "dotnet.run", None, _ -> Error "A project target is required."
            | "dotnet.restore", Some _, None
            | "dotnet.build", Some _, None
            | "dotnet.test", Some _, None ->
                Error "The lifecycle command requires a workspace or project target."
            | _ -> Error "The lifecycle command is not supported."
