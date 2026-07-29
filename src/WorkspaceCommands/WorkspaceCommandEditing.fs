namespace Dotnet.WorkspaceExplorer.WorkspaceCommands

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

module internal WorkspaceCommandEditing =
    let plannedActions =
        function
        | SolutionPlan plan ->
            seq {
                match plan.FileRename with
                | Some rename ->
                    yield WorkspaceEditAction.Rename(rename.Source.Value, rename.Destination.Value)
                | None -> ()

                yield WorkspaceEditAction.ReplaceFile(plan.BackingPath.Value, plan.Contents)
            }
        | ProjectPlan plan -> plan.Actions :> seq<WorkspaceEditAction>
        | CompositePlan plan -> plan.Actions :> seq<WorkspaceEditAction>
        | DotnetCommandPlan _ -> Seq.empty
        | LaunchProfilePlan(_, action, _) -> Seq.singleton action

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
        | CompositePlan plan -> plan.Paths :> seq<WorkspaceArtifactPath>
        | DotnetCommandPlan(_, paths) -> paths :> seq<WorkspaceArtifactPath>
        | LaunchProfilePlan(_, _, path) -> Seq.singleton path

    let plannedRequest =
        function
        | SolutionPlan plan -> plan.Request
        | ProjectPlan plan -> plan.Request
        | CompositePlan plan -> plan.Request
        | DotnetCommandPlan(request, _) -> request
        | LaunchProfilePlan(request, _, _) -> request

    let private projectPaths root (workspace: SolutionWorkspace) targetNodeId =
        targetNodeId
        |> Option.bind (fun target ->
            workspace.Contents.Projects
            |> Seq.tryFind (fun project -> project.Node.Id = target)
            |> Option.map (fun project ->
                WorkspaceArtifactPath.Create project.Path.AbsolutePath.Value))
        |> Option.map Array.singleton
        |> Option.defaultValue [| WorkspaceArtifactPath.Create root |]

    let private canonicalTargets root workspace request projectPaths =
        if DotnetCommandCatalog.isPackageMutation request.CommandId.Value then
            let centralPackages =
                WorkspaceArtifactPath.Create(Path.Combine(root, "Directory.Packages.props"))

            Ok(
                Array.append projectPaths [| centralPackages |],
                [| WorkspaceArtifactPath.Create root |],
                ImmutableHashSet<WorkspaceEditIntent>.Empty
            )
        elif request.CommandId.Value = "template.create" then
            DotnetCommandCatalog.templateOutput workspace request
            |> Result.bind (fun output ->
                let output = Path.GetFullPath output

                if ArtifactFiles.isLink output then
                    Error "Template output cannot be a symbolic link."
                else
                    let external = not (ArtifactFiles.isUnder root output)

                    let roots =
                        if external then
                            [| WorkspaceArtifactPath.Create root
                               WorkspaceArtifactPath.Create output |]
                        else
                            [| WorkspaceArtifactPath.Create root |]

                    let intents =
                        if external then
                            ImmutableHashSet.Create WorkspaceEditIntent.AccessExternalPath
                        else
                            ImmutableHashSet<WorkspaceEditIntent>.Empty

                    Ok(
                        [| WorkspaceArtifactPath.Create workspace.SolutionPath.Value |],
                        roots,
                        intents
                    ))
        else
            Ok(
                projectPaths,
                [| WorkspaceArtifactPath.Create root |],
                ImmutableHashSet<WorkspaceEditIntent>.Empty
            )

    let private packagePreflight
        root
        (state: WorkspaceIndex)
        request
        (cancellationToken: CancellationToken)
        =
        task {
            if not (DotnetCommandCatalog.isPackageMutation request.CommandId.Value) then
                return Ok()
            else
                match request.TargetWorkspaceNodeId with
                | None -> return Error "A package command requires a project target."
                | Some target ->
                    let! hydrated = state.ProjectAsync(target, cancellationToken)

                    return
                        match hydrated with
                        | Failure failure -> Error failure.Diagnostic.Message
                        | Success(_, _, snapshot) -> CentralPackageVersions.preflight root snapshot
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

    let private planDotnetCommand
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        request
        cancellationToken
        =
        task {
            let root =
                Path.GetDirectoryName workspace.SolutionPath.Value
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let projectPaths = projectPaths root workspace request.TargetWorkspaceNodeId
            let targets = canonicalTargets root workspace request projectPaths
            let! preflight = packagePreflight root state request cancellationToken

            return
                match preflight, targets with
                | Ok(), Ok(paths, _, _) when
                    paths |> Array.exists (fun path -> ArtifactFiles.isLink path.Value)
                    ->
                    invalidInput
                        "target"
                        "symbolic_link_unsupported"
                        "Dotnet command targets cannot be symbolic links."
                | Ok(), Ok(paths, roots, intents) ->
                    Success(
                        DotnetCommandPlan(
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

    let private planProject (state: WorkspaceIndex) request (cancellationToken: CancellationToken) =
        task {
            match request.TargetWorkspaceNodeId with
            | None ->
                return
                    Failure(
                        NotFound(
                            "targetNodeId",
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
                        ProjectEditing.plan
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
        (state: WorkspaceIndex)
        (request: CommandMutationRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            match request.CommandId with
            | commandId when ProjectRelocation.isComposite commandId ->
                let! plan = ProjectRelocation.plan workspace state request cancellationToken

                return
                    match plan with
                    | Success value -> Success(CompositePlan value)
                    | Failure failure -> Failure failure
            | _ when SolutionLaunchProfileCommands.tryDescribe request.CommandId |> Option.isSome ->
                return
                    match SolutionLaunchProfileCommands.plan workspace request with
                    | Ok(request, action, path) -> Success(LaunchProfilePlan(request, action, path))
                    | Error failure -> Failure failure
            | _ ->
                match DotnetCommandCatalog.tryDescribe request.CommandId with
                | Some _ when DotnetCommandCatalog.isMutation request.CommandId.Value ->
                    return! planDotnetCommand workspace state request cancellationToken
                | _ ->
                    match SolutionEditor.TryDescribe request.CommandId with
                    | Some _ ->
                        let! plan = SolutionEditor.PlanAsync(workspace, request, cancellationToken)

                        return
                            match plan with
                            | Success value -> Success(SolutionPlan value)
                            | Failure failure -> Failure failure
                    | None -> return! planProject state request cancellationToken
        }
