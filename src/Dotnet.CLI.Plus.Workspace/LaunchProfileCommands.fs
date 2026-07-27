namespace Dotnet.CLI.Plus

open System
open System.Collections.Immutable
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

module internal LaunchProfileCommandPlanning =
    let private parameter id kind required name =
        CommandParameterDescriptor.Create(CommandParameterId.Create id, kind, required, name)

    let private descriptor id name access parameters =
        CommandDescriptor.Create(
            CommandId.Create id,
            name,
            access,
            parameters,
            [ WorkspaceNodeKind.Workspace ]
        )

    let private profileName =
        parameter "name" CommandParameterType.Text true "Profile name"

    let private projects =
        parameter "projects" CommandParameterType.TextArray true "Projects in start order"

    let descriptors =
        ImmutableArray.CreateRange
            [ descriptor "solution.launch.list" "List launch profiles" CommandAccess.Read []
              descriptor
                  "solution.launch.set"
                  "Set launch profile"
                  CommandAccess.Write
                  [ profileName; projects ]
              descriptor
                  "solution.launch.remove"
                  "Remove launch profile"
                  CommandAccess.Write
                  [ profileName ] ]

    let tryDescribe id =
        descriptors |> Seq.tryFind (fun item -> item.CommandId = id)

    let discover (workspace: SolutionWorkspace) (target: NodeId option) =
        if target.IsNone then
            if workspace.WorkspaceDescriptor.IsReadOnly then
                descriptors
                |> Seq.filter (fun item -> item.CommandAccess = CommandAccess.Read)
                |> ImmutableArray.CreateRange
            else
                descriptors
        else
            ImmutableArray<CommandDescriptor>.Empty

    let private argument id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryFind (fun item -> item.ParameterId.Value = id)
        |> Option.map _.Value

    let private requiredText id arguments =
        match argument id arguments with
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"{id} is required."

    let private requiredTexts id arguments =
        match argument id arguments with
        | Some(TextArray values) when values.Length > 0 -> Ok(values |> Seq.toList)
        | _ -> Error $"{id} is required."

    let argv (workspace: SolutionWorkspace) (request: CommandMutationRequest) =
        match request.CommandId.Value, request.TargetId with
        | "solution.launch.list", None ->
            Ok [ "solution"; workspace.BackingPath.Value; "launch"; "list" ]
        | "solution.launch.set", None ->
            match
                requiredText "name" request.Arguments, requiredTexts "projects" request.Arguments
            with
            | Ok name, Ok projects ->
                Ok([ "solution"; workspace.BackingPath.Value; "launch"; "set"; name ] @ projects)
            | Error error, _
            | _, Error error -> Error error
        | "solution.launch.remove", None ->
            requiredText "name" request.Arguments
            |> Result.map (fun name ->
                [ "solution"; workspace.BackingPath.Value; "launch"; "remove"; name ])
        | _ -> Error "The launch-profile command requires a workspace target."

    let private planFailure parameter message =
        InvalidInput(
            parameter,
            WorkspaceDiagnostic.CreateSimple(
                WorkspaceDiagnosticSeverity.Error,
                WorkspaceDiagnosticCode.Create "invalid_input",
                message,
                false,
                CorrelationId.New()
            )
        )

    let plan (workspace: SolutionWorkspace) (request: CommandMutationRequest) =
        let name = requiredText "name" request.Arguments

        let prepared =
            match request.TargetId, request.CommandId.Value, name with
            | Some _, _, _ -> Error "The launch-profile command requires a workspace target."
            | None, "solution.launch.set", Ok name ->
                requiredTexts "projects" request.Arguments
                |> Result.bind (LaunchProfiles.prepareSet workspace name)
            | None, "solution.launch.remove", Ok name -> LaunchProfiles.prepareRemove workspace name
            | None, _, Error error -> Error error
            | _ -> Error "The launch-profile command is not supported."

        prepared
        |> Result.mapError (planFailure "arguments")
        |> Result.map (fun (path, contents) ->
            let root =
                IO.Path.GetDirectoryName workspace.BackingPath.Value
                |> Option.ofObj
                |> Option.defaultValue (IO.Directory.GetCurrentDirectory())

            { CommandId = request.CommandId
              Targets = ImmutableArray.Create(WorkspaceArtifactPath.Create path)
              Arguments = request.Arguments
              ExpectedRevision = request.ExpectedRevision
              Intents = ImmutableHashSet.Create MutationIntent.Overwrite
              AuthorizedRoots = ImmutableArray.Create(WorkspaceArtifactPath.Create root) },
            MutationAction.ReplaceFile(path, contents),
            WorkspaceArtifactPath.Create path)

    let verify (workspace: SolutionWorkspace) (request: CommandMutationRequest) =
        match request.TargetId, request.CommandId.Value, requiredText "name" request.Arguments with
        | Some _, _, _ -> Error "The launch-profile command requires a workspace target."
        | None, "solution.launch.set", Ok name ->
            requiredTexts "projects" request.Arguments
            |> Result.bind (LaunchProfiles.verifySet workspace name)
        | None, "solution.launch.remove", Ok name -> LaunchProfiles.verifyRemove workspace name
        | None, _, Error error -> Error error
        | _ -> Error "The launch-profile command is not supported."
