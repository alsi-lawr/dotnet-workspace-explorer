namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks

open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces

open ContextWorkspacePhysicalBatch
open ContextWorkspaceSolutionBatch

[<RequireQualifiedAccess>]
module internal ContextWorkspaceBatchActions =
    let private invalid message = Error(RpcErrors.invalidParams message)

    let private buildPlan
        (workspace: SolutionWorkspace)
        (commandId: string)
        (arguments: CommandArguments)
        (expectedRevision: int64)
        (actions: WorkspaceEditAction array)
        (summary: string)
        (effects: WorkspaceCommandEffect array)
        =
        let root =
            Path.GetDirectoryName workspace.SolutionPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let paths =
            actions
            |> Seq.collect (function
                | WorkspaceEditAction.CreateDirectory path
                | WorkspaceEditAction.ReplaceFile(path, _)
                | WorkspaceEditAction.ReplaceGeneratedDocument(path, _)
                | WorkspaceEditAction.Delete(path, _, _)
                | WorkspaceEditAction.Trash path -> [ path ]
                | WorkspaceEditAction.Rename(source, destination)
                | WorkspaceEditAction.Move(source, destination)
                | WorkspaceEditAction.Copy(source, destination) -> [ source; destination ])
            |> Seq.map Path.GetFullPath
            |> Seq.distinct
            |> Seq.toArray

        let external = paths |> Array.filter (isUnder root >> not)

        let roots =
            seq {
                yield WorkspaceArtifactPath.Create root

                for path in external do
                    yield
                        WorkspaceArtifactPath.Create(
                            Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue path
                        )
            }
            |> Seq.distinct
            |> ImmutableArray.CreateRange

        let request =
            { CommandId = CommandId.Create commandId
              Targets = paths |> Seq.map WorkspaceArtifactPath.Create |> ImmutableArray.CreateRange
              Arguments = arguments
              ExpectedRevision = WorkspaceRevision.Create expectedRevision
              Intents =
                seq {
                    if external.Length > 0 then
                        yield WorkspaceEditIntent.AccessExternalPath
                }
                |> ImmutableHashSet.CreateRange
              AuthorizedRoots = roots }

        { Plan = ContextPlan(request, actions, paths |> Array.map WorkspaceArtifactPath.Create)
          CommandRequest = None
          Summary = summary
          Effects = effects
          TemplateExecution = None }

    let prepareAsync
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (target: WorkspaceSemanticContext)
        (descriptor: CommandDescriptor)
        (arguments: CommandArguments)
        expectedRevision
        (cancellationToken: CancellationToken)
        =
        task {
            try
                match descriptor.Id.Value with
                | "workspace.rename" ->
                    match argumentText "name" arguments with
                    | None -> return invalid "The name argument is required."
                    | Some name when not (safeName name) ->
                        return invalid "The name must be one valid path segment."
                    | Some name ->
                        match target.Node.Kind with
                        | WorkspaceNodeKind.ProjectFile
                        | WorkspaceNodeKind.ProjectFolder ->
                            let! planned =
                                physicalPlan
                                    workspace
                                    state
                                    target
                                    [| target |]
                                    false
                                    (Some name)
                                    cancellationToken

                            return
                                planned
                                |> Result.map (fun (actions, effects) ->
                                    buildPlan
                                        workspace
                                        descriptor.Id.Value
                                        arguments
                                        expectedRevision
                                        actions
                                        $"Rename {target.Node.Name} to {name}"
                                        effects)
                        | WorkspaceNodeKind.Project
                        | WorkspaceNodeKind.SolutionFolder
                        | WorkspaceNodeKind.SolutionItem ->
                            let! planned =
                                solutionPlan
                                    workspace
                                    target
                                    [| target |]
                                    (Some name)
                                    cancellationToken

                            return
                                planned
                                |> Result.map (fun (actions, effects) ->
                                    buildPlan
                                        workspace
                                        descriptor.Id.Value
                                        arguments
                                        expectedRevision
                                        actions
                                        $"Rename {target.Node.Name} to {name}"
                                        effects)
                        | _ -> return invalid "The selected workspace node cannot be renamed."
                | "workspace.move"
                | "workspace.copy" ->
                    let copy = descriptor.Id.Value = "workspace.copy"

                    let! resolved =
                        resolveSources state arguments expectedRevision cancellationToken

                    match resolved with
                    | Error error -> return Error error
                    | Ok resolved ->
                        let sources = normalizeSources resolved

                        if
                            sources |> Array.exists (fun source -> source.Node.Id = target.Node.Id)
                        then
                            return invalid "The source and destination overlap."
                        elif
                            copy
                            && sources
                               |> Array.exists (fun source ->
                                   source.Node.Kind <> WorkspaceNodeKind.ProjectFile
                                   && source.Node.Kind <> WorkspaceNodeKind.ProjectFolder)
                        then
                            return
                                invalid "Copy supports only physical project files and directories."
                        else
                            let physical, logical =
                                sources
                                |> Array.partition (fun source ->
                                    source.Node.Kind = WorkspaceNodeKind.ProjectFile
                                    || source.Node.Kind = WorkspaceNodeKind.ProjectFolder)

                            let! physicalPlanResult =
                                if physical.Length = 0 then
                                    Task.FromResult(Ok([||], [||]))
                                else
                                    physicalPlan
                                        workspace
                                        state
                                        target
                                        physical
                                        copy
                                        None
                                        cancellationToken

                            match physicalPlanResult with
                            | Error error -> return Error error
                            | Ok(physicalActions, physicalEffects) when copy && logical.Length > 0 ->
                                return
                                    invalid
                                        "Copy supports only physical project files and directories."
                            | Ok(physicalActions, physicalEffects) ->
                                let! logicalPlanResult =
                                    if logical.Length = 0 then
                                        Task.FromResult(Ok([||], [||]))
                                    else
                                        solutionPlan workspace target logical None cancellationToken

                                match logicalPlanResult with
                                | Error error -> return Error error
                                | Ok(logicalActions, logicalEffects) ->
                                    let actions = Array.append physicalActions logicalActions

                                    let effects = Array.append physicalEffects logicalEffects

                                    return
                                        Ok(
                                            buildPlan
                                                workspace
                                                descriptor.Id.Value
                                                arguments
                                                expectedRevision
                                                actions
                                                $"{descriptor.Name} {sources.Length} workspace node(s)"
                                                effects
                                        )
                | _ -> return invalid "A contextual rename, move, or copy command is required."
            with
            | :? OperationCanceledException -> return Error RpcErrors.internalError
            | :? ArgumentException as error -> return invalid error.Message
            | :? IOException -> return invalid "The workspace artifacts could not be read."
            | :? UnauthorizedAccessException ->
                return invalid "The workspace artifacts could not be read."
        }
