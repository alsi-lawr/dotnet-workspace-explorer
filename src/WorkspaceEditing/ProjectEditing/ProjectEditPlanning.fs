namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open System
open System.Collections.Immutable
open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

module internal ProjectEditPlanning =
    let private diagnostic code message =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            false,
            CorrelationId.New()
        )

    let invalid name message =
        Failure(InvalidInput(name, diagnostic "invalid_input" message))

    let unsupported message =
        Failure(
            UnsupportedCapability(
                WorkspaceCapabilityId.Write,
                diagnostic "unsupported_capability" message
            )
        )

    let missing name message =
        Failure(NotFound(name, diagnostic "not_found" message))

    let value (name: string) (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = name then
                Some argument.Value
            else
                None)

    let requiredPath name arguments =
        match value name arguments with
        | Some(Path path) -> Ok path
        | _ -> Error $"'{name}' is required."

    let requiredText name arguments =
        match value name arguments with
        | Some(Text text) when not (String.IsNullOrWhiteSpace text) -> Ok text
        | _ -> Error $"'{name}' is required."

    let optionalText name arguments =
        match value name arguments with
        | None -> Ok None
        | Some(Text text) -> Ok(Some text)
        | _ -> Error $"'{name}' must be text."

    let requiredChoice name arguments =
        match value name arguments with
        | Some(Choice choice) -> Ok choice.Value
        | _ -> Error $"'{name}' is required."

    let optionalBoolean name arguments =
        match value name arguments with
        | None -> Ok false
        | Some(Boolean choice) -> Ok choice
        | _ -> Error $"'{name}' must be a boolean."

    let unwrap =
        function
        | Ok result -> result
        | Error message -> raise (ArgumentException message)

    let private external directory path =
        let relative = Path.GetRelativePath(directory, path)

        Path.IsPathRooted relative
        || relative = ".."
        || relative.StartsWith $"..{Path.DirectorySeparatorChar}"
        || relative.StartsWith $"..{Path.AltDirectorySeparatorChar}"

    let private request
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (targets: WorkspaceArtifactPath list)
        (intents: WorkspaceEditIntent list)
        =
        let solutionDirectory =
            Path.GetDirectoryName workspace.SolutionPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let externalTargets =
            targets
            |> Seq.filter (fun path -> external solutionDirectory path.Value)
            |> Seq.toArray

        let roots =
            seq {
                yield WorkspaceArtifactPath.Create solutionDirectory

                for target in externalTargets do
                    yield
                        WorkspaceArtifactPath.Create(
                            Path.GetDirectoryName target.Value
                            |> Option.ofObj
                            |> Option.defaultValue solutionDirectory
                        )
            }
            |> Seq.distinct
            |> ImmutableArray.CreateRange

        let values =
            [ yield WorkspaceEditIntent.Overwrite
              if externalTargets.Length > 0 then
                  yield WorkspaceEditIntent.AccessExternalPath
              yield! intents ]
            |> ImmutableHashSet.CreateRange

        { CommandId = command.CommandId
          Targets = targets |> Seq.distinct |> ImmutableArray.CreateRange
          Arguments = command.Arguments
          ExpectedRevision = command.ExpectedRevision
          Intents = values
          AuthorizedRoots = roots }

    let makePlan
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (actions: WorkspaceEditAction list)
        (paths: string list)
        (intents: WorkspaceEditIntent list)
        =
        let artifacts = paths |> List.map WorkspaceArtifactPath.Create

        Success
            { Request = request workspace command artifacts intents
              Actions = actions |> List.toArray
              Paths = artifacts |> List.toArray }
