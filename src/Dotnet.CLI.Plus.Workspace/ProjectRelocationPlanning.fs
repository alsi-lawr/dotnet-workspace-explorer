namespace Dotnet.CLI.Plus

open System
open System.Collections.Immutable
open System.IO
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution

module internal ProjectRelocationPlanning =
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

    let private missing name message =
        Failure(NotFound(name, diagnostic "not_found" message))

    let isComposite (commandId: CommandId) =
        commandId.Value = "project.physical-move"
        || commandId.Value = "solution.project.rename"

    let private argument id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun candidate ->
            if candidate.ParameterId.Value = id then
                Some candidate.Value
            else
                None)

    let private requiredPath id arguments =
        match argument id arguments with
        | Some(Path path) -> Ok path.Value
        | _ -> Error $"'{id}' is required."

    let private optionalNode id arguments =
        match argument id arguments with
        | None -> Ok None
        | Some(Node value) -> Ok(Some value)
        | _ -> Error $"'{id}' must be a node ID."

    let private requiredText id arguments =
        match argument id arguments with
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"'{id}' is required."

    let private pathEquals (left: string) (right: string) =
        String.Equals(
            MutationFiles.identity left,
            MutationFiles.identity right,
            StringComparison.Ordinal
        )

    let private directoryContains (parent: string) (child: string) =
        let relative = Path.GetRelativePath(parent, child)

        relative = "."
        || relative <> ".."
           && not (
               relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           )
           && not (
               relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
           )
           && not (Path.IsPathRooted relative)

    let private referencePath (projectPath: string) (includeValue: string) =
        Path.GetFullPath(
            includeValue,
            Path.GetDirectoryName projectPath
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())
        )

    let private replacementInclude
        (projectPath: string)
        (oldInclude: string)
        (destination: string)
        =
        let value =
            Path.GetRelativePath(
                Path.GetDirectoryName projectPath
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory()),
                destination
            )

        if oldInclude.Contains '\\' && not (oldInclude.Contains '/') then
            value.Replace('/', '\\')
        else
            value.Replace('\\', '/')

    let private hasIncomingReference (source: string) (snapshot: EvaluationSnapshot) =
        snapshot.Dimensions
        |> Seq.collect _.ProjectReferences
        |> Seq.exists (fun reference ->
            reference.ResolvedPath
            |> Option.ofObj
            |> Option.exists (fun path -> pathEquals path.Value source))

    let private rewriteReferences
        (source: string)
        (destination: string)
        (projectPath: string)
        (snapshot: EvaluationSnapshot)
        =
        let importedSourceReference =
            snapshot.Imports
            |> Seq.exists (fun imported ->
                try
                    if
                        not (pathEquals imported.Value projectPath) && File.Exists imported.Value
                    then
                        let document, _, _, _ = ProjectXml.readDocument imported.Value

                        document.Descendants(ProjectXml.name "ProjectReference")
                        |> Seq.choose (ProjectXml.attribute "Include")
                        |> Seq.exists (fun includeValue ->
                            not (includeValue.Contains("$(", StringComparison.Ordinal))
                            && pathEquals (referencePath projectPath includeValue) source)
                    else
                        false
                with :? IOException ->
                    true)

        if importedSourceReference then
            Error "The evaluated incoming project reference is declared by an import."
        else
            let document, encoding, preamble, lineEnding = ProjectXml.readDocument projectPath

            let references =
                document.Descendants(ProjectXml.name "ProjectReference")
                |> Seq.choose (fun element ->
                    ProjectXml.attribute "Include" element
                    |> Option.map (fun includeValue -> element, includeValue))
                |> Seq.toArray

            let matching =
                references
                |> Array.filter (fun (_, includeValue) ->
                    not (includeValue.Contains("$(", StringComparison.Ordinal))
                    && pathEquals (referencePath projectPath includeValue) source)

            if
                references
                |> Array.exists (fun (_, includeValue) ->
                    includeValue.Contains("$(", StringComparison.Ordinal))
            then
                Error "An incoming project reference uses a macro and cannot be rewritten safely."
            elif matching.Length = 0 then
                Error "The evaluated incoming project reference is imported or cannot be rewritten."
            else
                for element, includeValue in matching do
                    element.SetAttributeValue(
                        ProjectXml.name "Include",
                        replacementInclude projectPath includeValue destination
                    )

                Ok(ProjectXml.replaceProject projectPath document encoding preamble lineEnding)

    type private DirectReferences =
        { ReferencesSource: bool
          HasMacro: bool }

    let private directReferences source projectPath =
        let document, _, _, _ = ProjectXml.readDocument projectPath

        document.Descendants(ProjectXml.name "ProjectReference")
        |> Seq.choose (ProjectXml.attribute "Include")
        |> Seq.fold
            (fun declarations includeValue ->
                if includeValue.Contains("$(", StringComparison.Ordinal) then
                    { declarations with HasMacro = true }
                elif pathEquals (referencePath projectPath includeValue) source then
                    { declarations with
                        ReferencesSource = true }
                else
                    declarations)
            { ReferencesSource = false
              HasMacro = false }

    let private incomingActions
        (workspace: SolutionWorkspace)
        (state: WorkspaceState)
        source
        destination
        cancellationToken
        =
        task {
            let actions = ResizeArray<MutationAction>()
            let paths = ResizeArray<string>()
            let mutable failure = None

            for project in workspace.RootProjection.Projects do
                if failure.IsNone && not (pathEquals project.Path.AbsolutePath.Value source) then
                    let! hydrated = state.ProjectAsync(project.Node.NodeId, cancellationToken)

                    match hydrated with
                    | Failure outcome -> failure <- Some outcome
                    | Success(_, projection, snapshot) ->
                        let declarations =
                            directReferences source projection.Path.AbsolutePath.Value

                        if
                            hasIncomingReference source snapshot
                            || declarations.ReferencesSource
                            || declarations.HasMacro
                        then
                            match
                                rewriteReferences
                                    source
                                    destination
                                    projection.Path.AbsolutePath.Value
                                    snapshot
                            with
                            | Ok action ->
                                actions.Add action
                                paths.Add projection.Path.AbsolutePath.Value
                            | Error message ->
                                failure <-
                                    Some(
                                        InvalidInput(
                                            "reference",
                                            diagnostic "invalid_input" message
                                        )
                                    )

            return
                match failure with
                | Some outcome -> Error outcome
                | None -> Ok(actions |> Seq.toList, paths |> Seq.toList)
        }

    let private solutionRequest command target path =
        let arguments =
            [ yield
                  { ParameterId = CommandParameterId.Create "path"
                    Value = Path(WorkspaceArtifactPath.Create path) } ]

        { CommandId = CommandId.Create "solution.project.update-path"
          TargetId = Some target
          Arguments = CommandArguments.Create arguments
          ExpectedRevision = command.ExpectedRevision }

    let private planSolution workspace command target destination folder cancellationToken =
        SolutionPersistenceMutator.PlanRelocationAsync(
            workspace,
            solutionRequest command target destination,
            folder,
            cancellationToken
        )

    let private requestForActions
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (baseRequest: MutationPreviewRequest)
        paths
        =
        let solutionDirectory =
            Path.GetDirectoryName workspace.BackingPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let artifacts =
            paths |> Seq.map WorkspaceArtifactPath.Create |> Seq.distinct |> Seq.toArray

        let roots =
            artifacts
            |> Seq.map (fun artifact ->
                if Directory.Exists artifact.Value then
                    artifact
                else
                    WorkspaceArtifactPath.Create(
                        Path.GetDirectoryName artifact.Value
                        |> Option.ofObj
                        |> Option.defaultValue solutionDirectory
                    ))
            |> Seq.append [ WorkspaceArtifactPath.Create solutionDirectory ]
            |> Seq.distinct
            |> ImmutableArray.CreateRange

        let external =
            artifacts
            |> Seq.exists (fun artifact ->
                not (MutationFiles.isUnder solutionDirectory artifact.Value))

        { baseRequest with
            CommandId = command.CommandId
            Arguments = command.Arguments
            Targets = ImmutableArray.CreateRange artifacts
            AuthorizedRoots = roots
            Intents =
                [ yield MutationIntent.Overwrite
                  if external then
                      yield MutationIntent.AccessExternalPath ]
                |> ImmutableHashSet.CreateRange }

    let private planMove
        (workspace: SolutionWorkspace)
        (state: WorkspaceState)
        (command: CommandMutationRequest)
        cancellationToken
        =
        task {
            match command.TargetId with
            | None -> return missing "targetId" "A project target is required."
            | Some target ->
                let! hydrated = state.ProjectAsync(target, cancellationToken)

                match
                    hydrated,
                    requiredPath "destination" command.Arguments,
                    optionalNode "folder" command.Arguments
                with
                | Failure failure, _, _ -> return Failure failure
                | _, Error message, _
                | _, _, Error message -> return invalid "destination" message
                | Success(_, project, snapshot), Ok destination, Ok folder ->
                    let source = project.Path.AbsolutePath.Value

                    let sourceDirectory =
                        Path.GetDirectoryName source
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    let destination = Path.GetFullPath destination

                    let destinationParent =
                        Path.GetDirectoryName destination
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    let destinationProject =
                        Path.Combine(
                            destination,
                            Path.GetFileName source |> Option.ofObj |> Option.defaultValue "project"
                        )

                    let nestedProject =
                        workspace.RootProjection.Projects
                        |> Seq.exists (fun candidate ->
                            not (pathEquals candidate.Path.AbsolutePath.Value source)
                            && directoryContains sourceDirectory candidate.Path.AbsolutePath.Value)

                    if snapshot.CapabilityProfile <> WorkspaceCapabilityProfile.Full then
                        return
                            unsupported
                                "The evaluated project system does not grant project write capability."
                    elif directoryContains sourceDirectory workspace.BackingPath.Value then
                        return
                            invalid
                                "destination"
                                "The project directory contains the active solution."
                    elif
                        MutationFiles.isLink sourceDirectory || MutationFiles.isLink destination
                    then
                        return
                            invalid
                                "destination"
                                "Project relocation does not support symbolic links."
                    elif
                        not (Directory.Exists sourceDirectory)
                        || not (Directory.Exists destinationParent)
                        || MutationFiles.exists destination
                    then
                        return
                            invalid
                                "destination"
                                "The source directory or new destination directory is invalid."
                    elif
                        directoryContains sourceDirectory destination
                        || directoryContains destination sourceDirectory
                    then
                        return
                            invalid
                                "destination"
                                "The destination must not overlap the source project directory."
                    elif nestedProject then
                        return
                            invalid
                                "destination"
                                "The project directory contains another solution project."
                    else
                        let planningWorkspace =
                            SolutionProjection.EnrichProjectCapabilities(
                                workspace,
                                [ { ProjectId = target
                                    CapabilityProfile = snapshot.CapabilityProfile } ]
                            )

                        match
                            MutationFiles.canonicalNoFollow false sourceDirectory,
                            MutationFiles.canonicalNoFollow false destination
                        with
                        | Error message, _
                        | _, Error message -> return invalid "destination" message
                        | Ok _, Ok _ ->
                            let! incoming =
                                incomingActions
                                    workspace
                                    state
                                    source
                                    destinationProject
                                    cancellationToken

                            match incoming with
                            | Error failure -> return Failure failure
                            | Ok(referenceActions, referencePaths) ->
                                let! solution =
                                    planSolution
                                        planningWorkspace
                                        command
                                        target
                                        destinationProject
                                        folder
                                        cancellationToken

                                match solution with
                                | Failure failure -> return Failure failure
                                | Success plan ->
                                    let actions =
                                        [ yield MutationAction.Move(sourceDirectory, destination)
                                          yield! referenceActions
                                          yield
                                              MutationAction.ReplaceFile(
                                                  plan.BackingPath.Value,
                                                  plan.Contents
                                              ) ]

                                    let paths =
                                        [ yield sourceDirectory
                                          yield destination
                                          yield! referencePaths
                                          yield plan.BackingPath.Value ]

                                    return
                                        Success
                                            { Request =
                                                requestForActions
                                                    workspace
                                                    command
                                                    plan.Request
                                                    paths
                                              Actions = actions |> List.toArray
                                              Paths =
                                                paths
                                                |> Seq.map WorkspaceArtifactPath.Create
                                                |> Seq.distinct
                                                |> Seq.toArray }
        }

    let private planRename
        (workspace: SolutionWorkspace)
        (state: WorkspaceState)
        (command: CommandMutationRequest)
        cancellationToken
        =
        task {
            match command.TargetId, requiredText "name" command.Arguments with
            | None, _ -> return missing "targetId" "A project target is required."
            | _, Error message -> return invalid "name" message
            | Some target, Ok name ->
                let! hydrated = state.ProjectAsync(target, cancellationToken)

                match hydrated with
                | Failure failure -> return Failure failure
                | Success(_, project, snapshot) ->
                    if snapshot.CapabilityProfile <> WorkspaceCapabilityProfile.Full then
                        return
                            unsupported
                                "The evaluated project system does not grant project write capability."
                    else
                        let source = project.Path.AbsolutePath.Value

                        let destination =
                            Path.Combine(
                                Path.GetDirectoryName source
                                |> Option.ofObj
                                |> Option.defaultValue (Directory.GetCurrentDirectory()),
                                name + Path.GetExtension source
                            )

                        let! incoming =
                            incomingActions workspace state source destination cancellationToken

                        let! solution =
                            SolutionPersistenceMutator.PlanAsync(
                                workspace,
                                command,
                                cancellationToken
                            )

                        match incoming, solution with
                        | Error failure, _ -> return Failure failure
                        | _, Failure failure -> return Failure failure
                        | Ok(referenceActions, referencePaths), Success plan ->
                            let actions =
                                [ yield! referenceActions
                                  match plan.FileRename with
                                  | Some rename ->
                                      yield
                                          MutationAction.Rename(
                                              rename.Source.Value,
                                              rename.Destination.Value
                                          )
                                  | None -> ()
                                  yield
                                      MutationAction.ReplaceFile(
                                          plan.BackingPath.Value,
                                          plan.Contents
                                      ) ]

                            let paths =
                                [ yield! referencePaths
                                  yield plan.BackingPath.Value
                                  match plan.FileRename with
                                  | Some rename ->
                                      yield rename.Source.Value
                                      yield rename.Destination.Value
                                  | None -> () ]

                            return
                                Success
                                    { Request =
                                        requestForActions workspace command plan.Request paths
                                      Actions = actions |> List.toArray
                                      Paths =
                                        paths
                                        |> Seq.map WorkspaceArtifactPath.Create
                                        |> Seq.distinct
                                        |> Seq.toArray }
        }

    let plan workspace state (command: CommandMutationRequest) cancellationToken =
        match command.CommandId.Value with
        | "project.physical-move" -> planMove workspace state command cancellationToken
        | "solution.project.rename" -> planRename workspace state command cancellationToken
        | _ -> Task.FromResult(invalid "commandId" "The command is not a project relocation.")
