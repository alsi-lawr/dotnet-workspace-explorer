namespace Dotnet.CLI.Plus

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

module internal CanonicalMutationPlanning =
    let commandDescriptor commandId =
        try
            let id = CommandId.Create commandId

            SolutionPersistenceMutator.TryDescribe id
            |> Option.orElseWith (fun () -> ProjectMutations.tryDescribe id)
            |> Option.orElseWith (fun () -> CanonicalCommands.tryDescribe id)
        with :? ArgumentException as error ->
            raise (ArgumentException(error.Message, "commandId"))

    type PlannedMutation =
        | SolutionPlan of SolutionMutationPlan
        | ProjectPlan of ProjectMutationPlan
        | CanonicalPlan of MutationPreviewRequest * WorkspaceArtifactPath array

    let plannedActions =
        function
        | SolutionPlan plan ->
            seq {
                match plan.FileRename with
                | Some rename ->
                    yield MutationAction.Rename(rename.Source.Value, rename.Destination.Value)
                | None -> ()

                yield MutationAction.ReplaceFile(plan.BackingPath.Value, plan.Contents)
            }
        | ProjectPlan plan -> plan.Actions :> seq<MutationAction>
        | CanonicalPlan _ -> Seq.empty

    let plannedPaths =
        function
        | SolutionPlan plan ->
            seq {
                yield plan.BackingPath

                match plan.FileRename with
                | Some rename ->
                    yield rename.Source
                    yield rename.Destination
                | None -> ()
            }
        | ProjectPlan plan -> plan.Paths :> seq<WorkspaceArtifactPath>
        | CanonicalPlan(_, paths) -> paths :> seq<WorkspaceArtifactPath>

    let plannedRequest =
        function
        | SolutionPlan plan -> plan.Request
        | ProjectPlan plan -> plan.Request
        | CanonicalPlan(request, _) -> request

    let private projectPaths root (workspace: SolutionWorkspace) targetId =
        targetId
        |> Option.bind (fun target ->
            workspace.RootProjection.Projects
            |> Seq.tryFind (fun project -> project.Node.NodeId = target)
            |> Option.map (fun project ->
                WorkspaceArtifactPath.Create project.Path.AbsolutePath.Value))
        |> Option.map Array.singleton
        |> Option.defaultValue [| WorkspaceArtifactPath.Create root |]

    let private canonicalTargets root workspace request projectPaths =
        if CanonicalCommands.isPackageMutation request.CommandId.Value then
            let centralPackages =
                WorkspaceArtifactPath.Create(Path.Combine(root, "Directory.Packages.props"))

            Ok(
                Array.append projectPaths [| centralPackages |],
                [| WorkspaceArtifactPath.Create root |],
                ImmutableHashSet<MutationIntent>.Empty
            )
        elif request.CommandId.Value = "template.create" then
            CanonicalCommands.templateOutput workspace request
            |> Result.bind (fun output ->
                let output = Path.GetFullPath output

                if MutationFiles.isLink output then
                    Error "Canonical template output cannot be a symbolic link."
                else
                    let external = not (MutationFiles.isUnder root output)

                    let roots =
                        if external then
                            [| WorkspaceArtifactPath.Create root
                               WorkspaceArtifactPath.Create output |]
                        else
                            [| WorkspaceArtifactPath.Create root |]

                    let intents =
                        if external then
                            ImmutableHashSet.Create MutationIntent.AccessExternalPath
                        else
                            ImmutableHashSet<MutationIntent>.Empty

                    Ok(
                        [| WorkspaceArtifactPath.Create workspace.BackingPath.Value |],
                        roots,
                        intents
                    ))
        else
            Ok(
                projectPaths,
                [| WorkspaceArtifactPath.Create root |],
                ImmutableHashSet<MutationIntent>.Empty
            )

    let private packagePreflight
        root
        (state: WorkspaceState)
        request
        (cancellationToken: CancellationToken)
        =
        task {
            if not (CanonicalCommands.isPackageMutation request.CommandId.Value) then
                return Ok()
            else
                match request.TargetId with
                | None -> return Error "A package command requires a project target."
                | Some target ->
                    let! hydrated = state.ProjectAsync(target, cancellationToken)

                    return
                        match hydrated with
                        | Failure failure -> Error failure.Diagnostic.Message
                        | Success(_, _, snapshot) ->
                            CentralPackageManagement.preflight root snapshot
        }

    let private invalidInput parameter code message =
        Failure(
            InvalidInput(
                parameter,
                WorkspaceDiagnostic.CreateSimple(
                    WorkspaceDiagnosticSeverity.Error,
                    WorkspaceDiagnosticCode.Create code,
                    message,
                    false,
                    CorrelationId.New()
                )
            )
        )

    let private planCanonical
        (workspace: SolutionWorkspace)
        (state: WorkspaceState)
        request
        cancellationToken
        =
        task {
            let root =
                Path.GetDirectoryName workspace.BackingPath.Value
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let projectPaths = projectPaths root workspace request.TargetId
            let targets = canonicalTargets root workspace request projectPaths
            let! preflight = packagePreflight root state request cancellationToken

            return
                match preflight, targets with
                | Ok(), Ok(paths, _, _) when
                    paths |> Array.exists (fun path -> MutationFiles.isLink path.Value)
                    ->
                    invalidInput
                        "target"
                        "symbolic_link_unsupported"
                        "Canonical command targets cannot be symbolic links."
                | Ok(), Ok(paths, roots, intents) ->
                    Success(
                        CanonicalPlan(
                            { CommandId = request.CommandId
                              Targets = ImmutableArray.CreateRange paths
                              Arguments = request.Arguments
                              ExpectedRevision = request.ExpectedRevision
                              Intents = intents
                              AuthorizedRoots = ImmutableArray.CreateRange roots },
                            paths
                        )
                    )
                | Error message, _ -> invalidInput "package" "central_package_unsafe" message
                | _, Error message -> invalidInput "output" "invalid_input" message
        }

    let private planProject (state: WorkspaceState) request (cancellationToken: CancellationToken) =
        task {
            match request.TargetId with
            | None ->
                return
                    Failure(
                        NotFound(
                            "targetId",
                            WorkspaceDiagnostic.CreateSimple(
                                WorkspaceDiagnosticSeverity.Error,
                                WorkspaceDiagnosticCode.Create "not_found",
                                "A project target is required.",
                                false,
                                CorrelationId.New()
                            )
                        )
                    )
            | Some target ->
                let! project = state.ProjectAsync(target, cancellationToken)

                return
                    match project with
                    | Failure failure -> Failure failure
                    | Success(projectWorkspace, project, snapshot) ->
                        ProjectMutations.plan
                            projectWorkspace
                            project
                            snapshot
                            request
                            cancellationToken
                        |> function
                            | Success value -> Success(ProjectPlan value)
                            | Failure failure -> Failure failure
        }

    let planMutation
        (workspace: SolutionWorkspace)
        (state: WorkspaceState)
        (request: CommandMutationRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            match CanonicalCommands.tryDescribe request.CommandId with
            | Some _ when CanonicalCommands.isMutation request.CommandId.Value ->
                return! planCanonical workspace state request cancellationToken
            | _ ->
                match SolutionPersistenceMutator.TryDescribe request.CommandId with
                | Some _ ->
                    let! plan =
                        SolutionPersistenceMutator.PlanAsync(workspace, request, cancellationToken)

                    return
                        match plan with
                        | Success value -> Success(SolutionPlan value)
                        | Failure failure -> Failure failure
                | None -> return! planProject state request cancellationToken
        }
