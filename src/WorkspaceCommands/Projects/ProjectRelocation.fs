namespace Dotnet.WorkspaceExplorer.WorkspaceCommands

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing

open System
open System.Collections.Immutable
open System.IO
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Solutions

module internal ProjectRelocation =
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
        commandId.Value = "project.relocate"
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
            ArtifactFiles.identity left,
            ArtifactFiles.identity right,
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

    let private hasIncomingReference (source: string) (snapshot: ProjectEvaluationSnapshot) =
        snapshot.Dimensions
        |> Seq.collect _.ProjectReferences
        |> Seq.exists (fun reference ->
            reference.ResolvedPath
            |> Option.ofObj
            |> Option.exists (fun path -> pathEquals path.Value source))

    let private importedReference source projectPath (snapshot: ProjectEvaluationSnapshot) =
        snapshot.Imports
        |> Seq.exists (fun imported ->
            try
                if not (pathEquals imported.Value projectPath) && File.Exists imported.Value then
                    let document, _, _, _ = MsBuildProjectDocument.readDocument imported.Value

                    document.Descendants(MsBuildProjectDocument.name "ProjectReference")
                    |> Seq.choose (MsBuildProjectDocument.attribute "Include")
                    |> Seq.exists (fun includeValue ->
                        includeValue.Contains("$(", StringComparison.Ordinal)
                        || pathEquals (referencePath imported.Value includeValue) source)
                else
                    false
            with :? IOException ->
                true)

    let private rewriteReferences
        (source: string)
        (destination: string)
        (projectPath: string)
        (snapshot: ProjectEvaluationSnapshot)
        =
        if importedReference source projectPath snapshot then
            Error "The evaluated incoming project reference is declared by an import."
        else
            let document, encoding, preamble, lineEnding =
                MsBuildProjectDocument.readDocument projectPath

            let references =
                document.Descendants(MsBuildProjectDocument.name "ProjectReference")
                |> Seq.choose (fun element ->
                    MsBuildProjectDocument.attribute "Include" element
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
                        MsBuildProjectDocument.name "Include",
                        replacementInclude projectPath includeValue destination
                    )

                Ok(
                    MsBuildProjectDocument.replaceProject
                        projectPath
                        document
                        encoding
                        preamble
                        lineEnding
                )

    type private DirectReferences =
        { ReferencesSource: bool
          HasMacro: bool }

    let private directReferences source projectPath =
        let document, _, _, _ = MsBuildProjectDocument.readDocument projectPath

        document.Descendants(MsBuildProjectDocument.name "ProjectReference")
        |> Seq.choose (MsBuildProjectDocument.attribute "Include")
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
        (state: WorkspaceIndex)
        source
        destination
        cancellationToken
        =
        task {
            let actions = ResizeArray<WorkspaceEditAction>()
            let paths = ResizeArray<string>()
            let mutable failure = None

            for project in workspace.Contents.Projects do
                if failure.IsNone && not (pathEquals project.Path.AbsolutePath.Value source) then
                    let! hydrated = state.ProjectAsync(project.Node.Id, cancellationToken)

                    match hydrated with
                    | Failure outcome -> failure <- Some outcome
                    | Success(_, projection, snapshot) ->
                        let declarations =
                            directReferences source projection.Path.AbsolutePath.Value

                        let imported =
                            importedReference source projection.Path.AbsolutePath.Value snapshot

                        if
                            imported
                            || hasIncomingReference source snapshot
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
          TargetWorkspaceNodeId = Some target
          Arguments = CommandArguments.Create arguments
          ExpectedRevision = command.ExpectedRevision }

    let private planSolution workspace command target destination folder cancellationToken =
        SolutionEditor.PlanRelocationAsync(
            workspace,
            solutionRequest command target destination,
            folder,
            cancellationToken
        )

    let private requestForActions
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (baseRequest: WorkspaceEditPreviewRequest)
        paths
        =
        let solutionDirectory =
            Path.GetDirectoryName workspace.SolutionPath.Value
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
                not (ArtifactFiles.isUnder solutionDirectory artifact.Value))

        { baseRequest with
            CommandId = command.CommandId
            Arguments = command.Arguments
            Targets = ImmutableArray.CreateRange artifacts
            AuthorizedRoots = roots
            Intents =
                [ yield WorkspaceEditIntent.Overwrite
                  if external then
                      yield WorkspaceEditIntent.AccessExternalPath ]
                |> ImmutableHashSet.CreateRange }

    let private planMove
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (command: CommandMutationRequest)
        cancellationToken
        =
        task {
            match command.TargetWorkspaceNodeId with
            | None -> return missing "targetNodeId" "A project target is required."
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
                        workspace.Contents.Projects
                        |> Seq.exists (fun candidate ->
                            not (pathEquals candidate.Path.AbsolutePath.Value source)
                            && directoryContains sourceDirectory candidate.Path.AbsolutePath.Value)

                    if snapshot.CapabilityProfile <> WorkspaceCapabilityProfile.Full then
                        return
                            unsupported
                                "The evaluated project system does not grant project write capability."
                    elif directoryContains sourceDirectory workspace.SolutionPath.Value then
                        return
                            invalid
                                "destination"
                                "The project directory contains the active solution."
                    elif
                        ArtifactFiles.isLink sourceDirectory || ArtifactFiles.isLink destination
                    then
                        return
                            invalid
                                "destination"
                                "Project relocation does not support symbolic links."
                    elif
                        not (Directory.Exists sourceDirectory)
                        || not (Directory.Exists destinationParent)
                        || ArtifactFiles.exists destination
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
                            SolutionWorkspaceCapabilities.EnrichProjectCapabilities(
                                workspace,
                                [ { ProjectId = target
                                    CapabilityProfile = snapshot.CapabilityProfile } ]
                            )

                        match
                            ArtifactFiles.canonicalNoFollow false sourceDirectory,
                            ArtifactFiles.canonicalNoFollow false destination
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
                                        [ yield
                                              WorkspaceEditAction.Move(sourceDirectory, destination)
                                          yield! referenceActions
                                          yield
                                              WorkspaceEditAction.ReplaceFile(
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
        (state: WorkspaceIndex)
        (command: CommandMutationRequest)
        cancellationToken
        =
        task {
            match command.TargetWorkspaceNodeId, requiredText "name" command.Arguments with
            | None, _ -> return missing "targetNodeId" "A project target is required."
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
                            SolutionEditor.PlanAsync(workspace, command, cancellationToken)

                        match incoming, solution with
                        | Error failure, _ -> return Failure failure
                        | _, Failure failure -> return Failure failure
                        | Ok(referenceActions, referencePaths), Success plan ->
                            let actions =
                                [ yield! referenceActions
                                  match plan.FileRename with
                                  | Some rename ->
                                      yield
                                          WorkspaceEditAction.Rename(
                                              rename.Source.Value,
                                              rename.Destination.Value
                                          )
                                  | None -> ()
                                  yield
                                      WorkspaceEditAction.ReplaceFile(
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
        | "project.relocate" -> planMove workspace state command cancellationToken
        | "solution.project.rename" -> planRename workspace state command cancellationToken
        | _ -> Task.FromResult(invalid "commandId" "The command is not a project relocation.")
