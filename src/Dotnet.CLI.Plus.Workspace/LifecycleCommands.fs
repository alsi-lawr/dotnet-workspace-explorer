namespace Dotnet.CLI.Plus

open System
open System.Collections.Immutable
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

module internal LifecycleCommands =
    let private parameter id kind required name =
        CommandParameterDescriptor.Create(CommandParameterId.Create id, kind, required, name)

    let private descriptor id name targets parameters =
        CommandDescriptor.Create(CommandId.Create id, name, CommandAccess.Read, parameters, targets)

    let private extra =
        parameter "arguments" CommandParameterType.TextArray false "Additional canonical arguments"

    let private noRestore =
        parameter "noRestore" CommandParameterType.Boolean false "Do not restore"

    let private workspaceAndProject =
        [ WorkspaceNodeKind.Workspace; WorkspaceNodeKind.Project ]

    let descriptors =
        ImmutableArray.CreateRange
            [ descriptor "lifecycle.restore" "Restore" workspaceAndProject [ extra ]
              descriptor "lifecycle.build" "Build" workspaceAndProject [ noRestore; extra ]
              descriptor "lifecycle.test" "Test" workspaceAndProject [ extra ]
              descriptor "lifecycle.run" "Run" [ WorkspaceNodeKind.Project ] [ noRestore; extra ] ]

    let tryDescribe id =
        descriptors |> Seq.tryFind (fun item -> item.CommandId = id)

    let discover (workspace: SolutionWorkspace) target =
        match target with
        | None ->
            descriptors
            |> Seq.filter (fun item -> item.CommandId.Value <> "lifecycle.run")
            |> ImmutableArray.CreateRange
        | Some target when
            workspace.RootProjection.Projects
            |> Seq.exists (fun project -> project.Node.NodeId = target)
            ->
            descriptors
        | _ -> ImmutableArray<CommandDescriptor>.Empty

    let private argument id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryFind (fun item -> item.ParameterId.Value = id)
        |> Option.map _.Value

    let private extraArguments command arguments =
        match argument "arguments" arguments with
        | None -> Ok []
        | Some(TextArray values) when
            (command = "lifecycle.build" || command = "lifecycle.run")
            && values |> Seq.exists ((=) "--no-restore")
            ->
            Error "Use noRestore instead of --no-restore in arguments."
        | Some(TextArray values) -> Ok(values |> Seq.toList)
        | _ -> Error "arguments must be a text array."

    let private noRestoreValue arguments =
        match argument "noRestore" arguments with
        | None -> Ok false
        | Some(Boolean value) -> Ok value
        | _ -> Error "noRestore must be a boolean."

    let argv (workspace: SolutionWorkspace) (request: CommandMutationRequest) =
        match
            extraArguments request.CommandId.Value request.Arguments,
            noRestoreValue request.Arguments
        with
        | Error error, _
        | _, Error error -> Error error
        | Ok extra, Ok noRestore ->
            let project =
                request.TargetId
                |> Option.bind (fun id ->
                    workspace.RootProjection.Projects
                    |> Seq.tryFind (fun item -> item.Node.NodeId = id))

            match request.CommandId.Value, request.TargetId, project with
            | "lifecycle.restore", None, _ -> Ok([ "restore"; workspace.BackingPath.Value ] @ extra)
            | "lifecycle.restore", Some _, Some item ->
                Ok([ "restore"; item.Path.AbsolutePath.Value ] @ extra)
            | "lifecycle.build", None, _ ->
                Ok(
                    [ "build"; workspace.BackingPath.Value ]
                    @ (if noRestore then [ "--no-restore" ] else [])
                    @ extra
                )
            | "lifecycle.build", Some _, Some item ->
                Ok(
                    [ "build"; item.Path.AbsolutePath.Value ]
                    @ (if noRestore then [ "--no-restore" ] else [])
                    @ extra
                )
            | "lifecycle.test", None, _ -> Ok([ "test"; workspace.BackingPath.Value ] @ extra)
            | "lifecycle.test", Some _, Some item ->
                Ok([ "test"; item.Path.AbsolutePath.Value ] @ extra)
            | "lifecycle.run", Some _, Some item ->
                Ok(
                    [ "run"; "--project"; item.Path.AbsolutePath.Value ]
                    @ (if noRestore then [ "--no-restore" ] else [])
                    @ extra
                )
            | "lifecycle.run", None, _ -> Error "A project target is required."
            | "lifecycle.restore", Some _, None
            | "lifecycle.build", Some _, None
            | "lifecycle.test", Some _, None ->
                Error "The lifecycle command requires a workspace or project target."
            | _ -> Error "The lifecycle command is not supported."
