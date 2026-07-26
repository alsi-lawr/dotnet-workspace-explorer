namespace Dotnet.CLI.Plus

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution

module internal ProjectFolderPlanning =
    open ProjectFolderPaths
    open ProjectFolderXml
    open ProjectItemPolicy
    open ProjectXml

    let private diagnostic code message =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            false,
            CorrelationId.New()
        )

    let private invalid name message =
        Failure(InvalidInput(name, diagnostic "invalid_input" message))

    let private unsupported message =
        Failure(
            UnsupportedCapability(
                WorkspaceCapabilityId.Write,
                diagnostic "unsupported_capability" message
            )
        )

    let private argument (name: string) (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun value ->
            if value.ParameterId.Value = name then
                Some value.Value
            else
                None)

    let private requiredPath name arguments =
        match argument name arguments with
        | Some(Path path) -> Ok path.Value
        | _ -> Error $"'{name}' is required."

    let private requiredText name arguments =
        match argument name arguments with
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let private requiredItemType arguments =
        match argument "itemType" arguments with
        | Some(Choice value) when itemTypes.Contains value.Value -> Ok value.Value
        | _ -> Error "The item type is not supported."

    let private request
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (paths: string list)
        =
        let solutionDirectory =
            Path.GetDirectoryName workspace.BackingPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let artifacts = paths |> List.map WorkspaceArtifactPath.Create

        let external =
            artifacts
            |> List.filter (fun path -> not (isProjectLocal solutionDirectory path.Value))

        { CommandId = command.CommandId
          Targets = artifacts |> Seq.distinct |> ImmutableArray.CreateRange
          Arguments = command.Arguments
          ExpectedRevision = command.ExpectedRevision
          Intents =
            [ yield MutationIntent.Overwrite
              if not external.IsEmpty then
                  yield MutationIntent.AccessExternalPath ]
            |> ImmutableHashSet.CreateRange
          AuthorizedRoots =
            seq {
                yield WorkspaceArtifactPath.Create solutionDirectory

                for path in external do
                    yield
                        WorkspaceArtifactPath.Create(
                            Path.GetDirectoryName path.Value
                            |> Option.ofObj
                            |> Option.defaultValue solutionDirectory
                        )
            }
            |> Seq.distinct
            |> ImmutableArray.CreateRange }

    let private makePlan
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (actions: MutationAction list)
        (paths: string list)
        =
        Success
            { Request = request workspace command paths
              Actions = actions |> List.toArray
              Paths = paths |> List.map WorkspaceArtifactPath.Create |> List.toArray }

    let private unwrap =
        function
        | Ok value -> value
        | Error message -> raise (ArgumentException message)

    let private descendantItems (snapshot: EvaluationSnapshot) projectDirectory folder =
        snapshot.Dimensions
        |> Seq.collect (fun (dimension: EvaluationDimensionSnapshot) -> dimension.Items)
        |> Seq.choose (fun item ->
            item.ResolvedPath
            |> Option.ofObj
            |> Option.map (fun path -> item.ItemType, path.Value))
        |> Seq.filter (fun (_, path) ->
            path.StartsWith(
                folder + string Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            ))
        |> Seq.map (fun (itemType, path) -> itemType, normalizedRelative projectDirectory path)
        |> Seq.distinct
        |> Seq.toArray

    let plan
        (workspace: SolutionWorkspace)
        (project: SolutionProjectProjection)
        (snapshot: EvaluationSnapshot)
        (command: CommandMutationRequest)
        (_: CancellationToken)
        =
        if snapshot.CapabilityProfile <> WorkspaceCapabilityProfile.Full then
            unsupported "The evaluated project system does not grant project write capability."
        elif command.TargetId <> Some project.Node.NodeId then
            invalid "targetId" "The command target was not found."
        elif ProjectFolderCommands.tryDescribe command.CommandId |> Option.isNone then
            invalid "commandId" "The command is not available."
        else
            try
                let projectPath = project.Path.AbsolutePath.Value
                let projectDirectory = ProjectItemPolicy.projectDirectory project
                let document, encoding, preamble, lineEnding = readDocument projectPath

                let save () =
                    replaceProject projectPath document encoding preamble lineEnding

                let path name =
                    requiredPath name command.Arguments |> unwrap

                let commandId = command.CommandId.Value

                match commandId with
                | "project.folder.new" ->
                    let destination = canonicalNewDirectory projectDirectory (path "path") |> unwrap
                    let relative = normalizedRelative projectDirectory destination
                    appendFolder document relative

                    makePlan
                        workspace
                        command
                        [ save () ]
                        [ projectPath; destination; destinationParent destination ]
                | "project.folder.copy" ->
                    let source =
                        canonicalExternalDirectory projectDirectory (path "source") |> unwrap

                    let destination = canonicalNewDirectory projectDirectory (path "path") |> unwrap

                    validateDestinationTree projectDirectory source destination
                    |> Result.map ignore
                    |> unwrap

                    makePlan
                        workspace
                        command
                        []
                        [ projectPath; source; destination; destinationParent destination ]
                | "project.folder.link" ->
                    let source =
                        canonicalExternalDirectory projectDirectory (path "source") |> unwrap

                    let destination =
                        canonicalVirtualDirectory projectDirectory (path "path") |> unwrap

                    let relative = normalizedRelative projectDirectory destination
                    let itemType = requiredItemType command.Arguments |> unwrap

                    completeTree projectDirectory source |> Result.map ignore |> unwrap

                    appendExternalLink
                        document
                        itemType
                        (source.Replace(Path.DirectorySeparatorChar, '/'))
                        relative

                    makePlan
                        workspace
                        command
                        [ save () ]
                        [ projectPath; source; destination; destinationParent destination ]
                | "project.folder.rename" ->
                    let source = canonicalDirectory projectDirectory (path "path") |> unwrap
                    let name = requiredText "name" command.Arguments |> unwrap

                    if
                        name = "."
                        || name = ".."
                        || name.Contains '/'
                        || name.Contains '\\'
                        || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                        || Path.IsPathRooted name
                    then
                        raise (ArgumentException "The folder name is invalid.")

                    let destination =
                        Path.Combine(
                            Path.GetDirectoryName(source)
                            |> Option.ofObj
                            |> Option.defaultWith (fun () -> projectDirectory),
                            name
                        )

                    canonicalNewDirectory projectDirectory destination |> unwrap |> ignore

                    validateDestinationTree projectDirectory source destination
                    |> Result.map ignore
                    |> unwrap

                    let sourceRelative = normalizedRelative projectDirectory source
                    let destinationRelative = normalizedRelative projectDirectory destination

                    ensureDirectOwnership projectPath sourceRelative source snapshot document
                    |> unwrap

                    rewriteOwnedDescendants sourceRelative destinationRelative document |> unwrap

                    makePlan
                        workspace
                        command
                        [ MutationAction.Move(source, destination); save () ]
                        [ projectPath; source; destination; destinationParent destination ]
                | "project.folder.move" ->
                    let source = canonicalDirectory projectDirectory (path "path") |> unwrap

                    let destination =
                        canonicalNewDirectory projectDirectory (path "destination") |> unwrap

                    validateDestinationTree projectDirectory source destination
                    |> Result.map ignore
                    |> unwrap

                    let sourceRelative = normalizedRelative projectDirectory source
                    let destinationRelative = normalizedRelative projectDirectory destination

                    ensureDirectOwnership projectPath sourceRelative source snapshot document
                    |> unwrap

                    rewriteOwnedDescendants sourceRelative destinationRelative document |> unwrap

                    makePlan
                        workspace
                        command
                        [ MutationAction.Move(source, destination); save () ]
                        [ projectPath; source; destination; destinationParent destination ]
                | "project.folder.remove"
                | "project.folder.delete" ->
                    let folder = canonicalDirectory projectDirectory (path "path") |> unwrap
                    completeTree projectDirectory folder |> Result.map ignore |> unwrap
                    let relative = normalizedRelative projectDirectory folder
                    removeOwnedDescendants relative document

                    let descendants = descendantItems snapshot projectDirectory folder

                    for itemType, includeValue in descendants do
                        appendRemove document itemType includeValue

                    let actions =
                        if commandId = "project.folder.delete" then
                            [ save (); MutationAction.Trash folder ]
                        else
                            [ save () ]

                    makePlan workspace command actions [ projectPath; folder ]
                | _ -> invalid "commandId" "The command is not available."
            with
            | :? ArgumentException as error -> invalid "path" error.Message
            | :? IOException as error -> invalid "path" error.Message
            | :? UnauthorizedAccessException as error -> invalid "path" error.Message
