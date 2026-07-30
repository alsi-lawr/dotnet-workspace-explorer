namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Immutable
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.CommandLine
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces
open WorkspaceCommandEditing

type internal WorkspaceCommandEffect =
    { Operation: string
      Target: string
      Recursive: bool }

type internal ContextTemplateExecution =
    | ProjectTemplate of expectedOutputs: WorkspaceArtifactPath array
    | ItemTemplate of
        projectPath: WorkspaceArtifactPath *
        outputDirectory: WorkspaceArtifactPath *
        expectedOutputs: WorkspaceArtifactPath array

type internal PreparedContextMutation =
    { Plan: PlannedWorkspaceCommand
      CommandRequest: CommandMutationRequest option
      Summary: string
      Effects: WorkspaceCommandEffect array
      TemplateExecution: ContextTemplateExecution option }

[<RequireQualifiedAccess>]
module internal ContextWorkspaceActions =
    let private rpcFailure failure =
        Error(WorkspaceRpcResponses.failureError failure)

    let private argumentText id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = id then
                match argument.Value with
                | Text value -> Some value
                | _ -> None
            else
                None)

    let private parameter id value =
        { ParameterId = CommandParameterId.Create id
          Value = value }

    let private safeName (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value <> "."
        && value <> ".."
        && not (Path.IsPathRooted value)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && value.IndexOfAny [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |] < 0

    let private templateArguments
        (entry: WorkspaceTemplateEntry)
        (name: string)
        (output: WorkspaceArtifactPath)
        (projectPath: WorkspaceArtifactPath option)
        =
        CommandArguments.Create
            [ parameter "template" (Text entry.ShortName)
              parameter "output" (Path output)
              parameter
                  "arguments"
                  (TextArray(
                      seq {
                          yield "--name"
                          yield name

                          match entry.Language with
                          | Some language ->
                              yield "--language"
                              yield language
                          | None -> ()

                          match projectPath with
                          | Some path ->
                              yield "--project"
                              yield path.Value
                          | None -> ()
                      }
                      |> ImmutableArray.CreateRange
                  )) ]

    let private templatePreflightAsync
        (entry: WorkspaceTemplateEntry)
        (name: string)
        (output: WorkspaceArtifactPath)
        (projectPath: WorkspaceArtifactPath option)
        (cancellationToken: CancellationToken)
        =
        task {
            use standardOutput = new StringWriter()
            use standardError = new StringWriter()

            let arguments =
                [ yield "new"
                  yield entry.ShortName
                  yield "--name"
                  yield name

                  match entry.Language with
                  | Some language ->
                      yield "--language"
                      yield language
                  | None -> ()

                  match projectPath with
                  | Some project ->
                      yield "--project"
                      yield project.Value
                  | None -> ()

                  yield "--output"
                  yield output.Value
                  yield "--dry-run" ]

            let! result =
                DirectCommandRunner.ExecuteAsync(
                    arguments |> List.toArray,
                    Human(standardOutput, standardError, false, false),
                    cancellationToken
                )

            if not result.Success then
                let message =
                    result.Diagnostics
                    |> List.tryHead
                    |> Option.map _.Message
                    |> Option.defaultValue "The template preflight failed."

                return Error(RpcErrors.create "template_preflight_failed" message None)
            else
                let prefix = "Create:"

                let paths =
                    standardOutput
                        .ToString()
                        .Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Seq.map _.Trim()
                    |> Seq.choose (fun line ->
                        if line.StartsWith(prefix, StringComparison.Ordinal) then
                            let value = line[prefix.Length ..].Trim()

                            if String.IsNullOrWhiteSpace value then
                                None
                            else
                                Some(
                                    if Path.IsPathRooted value then
                                        Path.GetFullPath value
                                    else
                                        Path.GetFullPath(value, Directory.GetCurrentDirectory())
                                )
                        else
                            None)
                    |> Seq.distinct
                    |> Seq.sort
                    |> Seq.toArray

                if paths.Length = 0 then
                    return
                        Error(
                            RpcErrors.create
                                "template_preflight_failed"
                                "The template preflight did not report any output artifacts."
                                None
                        )
                else
                    return Ok paths
        }

    let private planLowLevel
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (request: CommandMutationRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            let! planned = planMutation workspace state request cancellationToken

            return
                match planned with
                | Failure failure -> rpcFailure failure
                | Success plan -> Ok plan
        }

    let private bindTemplatePreflight
        (workspace: SolutionWorkspace)
        (outputDirectory: WorkspaceArtifactPath)
        (outputs: string array)
        plan
        =
        match plan with
        | DotnetCommandPlan(request, existingPaths) ->
            let root =
                Path.GetDirectoryName workspace.SolutionPath.Value
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let outputPaths = outputs |> Seq.map WorkspaceArtifactPath.Create |> Seq.toArray
            let external = not (ArtifactFiles.isUnder root outputDirectory.Value)

            let request =
                { request with
                    Targets =
                        Seq.append request.Targets outputPaths
                        |> Seq.distinct
                        |> ImmutableArray.CreateRange
                    Intents =
                        if external then
                            request.Intents.Add WorkspaceEditIntent.AccessExternalPath
                        else
                            request.Intents
                    AuthorizedRoots =
                        if external then
                            Seq.append request.AuthorizedRoots [ outputDirectory ]
                            |> Seq.distinct
                            |> ImmutableArray.CreateRange
                        else
                            request.AuthorizedRoots }

            DotnetCommandPlan(
                request,
                Array.append (existingPaths |> Seq.toArray) outputPaths |> Array.distinct
            )
        | _ -> invalidOp "A template preflight requires a dotnet command plan."

    let private createPlan
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (context: WorkspaceSemanticContext)
        (arguments: CommandArguments)
        (expectedRevision: int64)
        (cancellationToken: CancellationToken)
        =
        task {
            match argumentText "selectionId" arguments, argumentText "name" arguments with
            | None, _
            | _, None -> return Error(RpcErrors.invalidParams "selectionId and name are required.")
            | Some _, Some name when not (safeName name) ->
                return Error(RpcErrors.invalidParams "name must be one valid filename component.")
            | Some selectionId, Some name ->
                let! catalog = WorkspaceTemplateCatalog.readAsync workspace cancellationToken

                match catalog with
                | Error rpcError -> return Error rpcError
                | Ok catalog ->
                    let available = WorkspaceTemplateCatalog.options context catalog

                    match
                        available |> Array.tryFind (fun entry -> entry.SelectionId = selectionId)
                    with
                    | None ->
                        return
                            Error(
                                RpcErrors.create
                                    "template_catalog_changed"
                                    "The selected creation option is no longer available."
                                    None
                            )
                    | Some entry when entry.Kind = WorkspaceCreateKind.Empty ->
                        match context.ProjectId, context.PhysicalDirectory with
                        | Some projectId, Some directory ->
                            let destination =
                                WorkspaceArtifactPath.Create(Path.Combine(directory.Value, name))

                            let! project = state.ProjectAsync(projectId, cancellationToken)

                            match project with
                            | Failure failure -> return rpcFailure failure
                            | Success(_, _, snapshot) ->
                                let itemType =
                                    ProjectItemInclusion.defaultItemType snapshot destination.Value
                                    |> Option.defaultValue "None"

                                let request =
                                    { CommandId = CommandId.Create "project.item.new"
                                      TargetWorkspaceNodeId = Some projectId
                                      Arguments =
                                        CommandArguments.Create
                                            [ parameter "path" (Path destination)
                                              parameter
                                                  "itemType"
                                                  (Choice(CommandChoiceId.Create itemType)) ]
                                      ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                                let! planned =
                                    planLowLevel workspace state request cancellationToken

                                return
                                    planned
                                    |> Result.map (fun plan ->
                                        { Plan = plan
                                          CommandRequest = Some request
                                          Summary = $"Create {destination.Value}"
                                          Effects =
                                            [| { Operation = "create"
                                                 Target = destination.Value
                                                 Recursive = false }
                                               { Operation = "addToProject"
                                                 Target = destination.Value
                                                 Recursive = false } |]
                                          TemplateExecution = None })
                        | _ ->
                            return
                                Error(
                                    RpcErrors.unsupported
                                        "An empty file requires a project context."
                                )
                    | Some entry when entry.Kind = WorkspaceCreateKind.ItemTemplate ->
                        match context.ProjectId, context.ProjectPath, context.PhysicalDirectory with
                        | Some _, Some projectPath, Some outputDirectory ->
                            let! preflight =
                                templatePreflightAsync
                                    entry
                                    name
                                    outputDirectory
                                    (Some projectPath)
                                    cancellationToken

                            match preflight with
                            | Error rpcError -> return Error rpcError
                            | Ok outputs ->
                                let request =
                                    { CommandId = CommandId.Create "template.create"
                                      TargetWorkspaceNodeId = None
                                      Arguments =
                                        templateArguments
                                            entry
                                            name
                                            outputDirectory
                                            (Some projectPath)
                                      ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                                let! planned =
                                    planLowLevel workspace state request cancellationToken

                                return
                                    planned
                                    |> Result.map (fun plan ->
                                        { Plan =
                                            bindTemplatePreflight
                                                workspace
                                                outputDirectory
                                                outputs
                                                plan
                                          CommandRequest = Some request
                                          Summary = $"Create {entry.DisplayName} '{name}'"
                                          Effects =
                                            outputs
                                            |> Array.map (fun path ->
                                                { Operation = "create"
                                                  Target = path
                                                  Recursive = false })
                                          TemplateExecution =
                                            Some(
                                                ItemTemplate(
                                                    projectPath,
                                                    outputDirectory,
                                                    outputs
                                                    |> Array.map WorkspaceArtifactPath.Create
                                                )
                                            ) })
                        | _ ->
                            return
                                Error(
                                    RpcErrors.unsupported
                                        "An item template requires a project context."
                                )
                    | Some entry ->
                        let solutionRoot =
                            Path.GetDirectoryName workspace.SolutionPath.Value
                            |> Option.ofObj
                            |> Option.defaultValue (Directory.GetCurrentDirectory())

                        let output = WorkspaceArtifactPath.Create(Path.Combine(solutionRoot, name))

                        let! preflight =
                            templatePreflightAsync entry name output None cancellationToken

                        match preflight with
                        | Error rpcError -> return Error rpcError
                        | Ok outputs ->
                            let request =
                                { CommandId = CommandId.Create "template.create"
                                  TargetWorkspaceNodeId = context.LogicalFolderId
                                  Arguments = templateArguments entry name output None
                                  ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                            let! planned = planLowLevel workspace state request cancellationToken

                            return
                                planned
                                |> Result.map (fun plan ->
                                    { Plan = bindTemplatePreflight workspace output outputs plan
                                      CommandRequest = Some request
                                      Summary = $"Create {entry.DisplayName} project '{name}'"
                                      Effects =
                                        Array.append
                                            (outputs
                                             |> Array.map (fun path ->
                                                 { Operation = "create"
                                                   Target = path
                                                   Recursive = false }))
                                            [| { Operation = "addToSolution"
                                                 Target = name
                                                 Recursive = false } |]
                                      TemplateExecution =
                                        Some(
                                            ProjectTemplate(
                                                outputs |> Array.map WorkspaceArtifactPath.Create
                                            )
                                        ) })
        }

    let private isUnder (directory: string) (path: string) =
        let relative = Path.GetRelativePath(directory, path)

        relative <> "."
        && not (Path.IsPathRooted relative)
        && relative <> ".."
        && not (relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        && not (
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )

    let private projectDescendantEffects
        (state: WorkspaceIndex)
        (projectId: WorkspaceNodeId)
        (folder: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let! project = state.ProjectAsync(projectId, cancellationToken)

            return
                match project with
                | Failure _ -> Array.empty
                | Success(_, _, snapshot) ->
                    snapshot.Dimensions
                    |> Seq.collect _.Items
                    |> Seq.choose (fun (item: EvaluatedItem) -> Option.ofObj item.ResolvedPath)
                    |> Seq.map _.Value
                    |> Seq.filter (isUnder folder)
                    |> Seq.distinct
                    |> Seq.sort
                    |> Seq.map (fun path ->
                        { Operation = "removeFromProject"
                          Target = path
                          Recursive = false })
                    |> Seq.toArray
        }

    let private solutionFolderEffects (workspace: SolutionWorkspace) (folderPath: string) =
        let descendant (candidate: string) =
            candidate = folderPath
            || candidate.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase)

        seq {
            for item in workspace.Contents.Items do
                if item.FolderPath |> Option.exists descendant then
                    yield
                        { Operation = "removeFromSolution"
                          Target = item.RelativePath
                          Recursive = false }

            for project in workspace.Contents.Projects do
                if project.ParentFolderPath |> Option.exists descendant then
                    yield
                        { Operation = "removeFromSolution"
                          Target = project.Path.SolutionRelativePath
                          Recursive = false }

            for folder in workspace.Contents.Folders |> Seq.sortByDescending _.Path.Length do
                if descendant folder.Path then
                    yield
                        { Operation = "removeFromSolution"
                          Target = folder.Path
                          Recursive = folder.Path = folderPath }
        }
        |> Seq.toArray

    let private solutionItemPlan
        (workspace: SolutionWorkspace)
        (context: WorkspaceSemanticContext)
        (arguments: CommandArguments)
        (expectedRevision: int64)
        (cancellationToken: CancellationToken)
        =
        task {
            match context.PhysicalPath with
            | None ->
                return
                    Error(
                        RpcErrors.create "not_found" "The solution item path is unavailable." None
                    )
            | Some path when not (File.Exists path.Value) ->
                return Error(RpcErrors.create "not_found" "The solution item does not exist." None)
            | Some path ->
                let request =
                    { CommandId = CommandId.Create "solution.item.remove"
                      TargetWorkspaceNodeId = Some context.Node.Id
                      Arguments = CommandArguments.Create []
                      ExpectedRevision = WorkspaceRevision.Create expectedRevision }

                let! planned = SolutionEditor.PlanAsync(workspace, request, cancellationToken)

                match planned with
                | Failure failure -> return rpcFailure failure
                | Success solutionPlan ->
                    let root =
                        Path.GetDirectoryName workspace.SolutionPath.Value
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    let external = not (ArtifactFiles.isUnder root path.Value)

                    let previewRequest: WorkspaceEditPreviewRequest =
                        { solutionPlan.Request with
                            CommandId = CommandId.Create "workspace.delete"
                            Targets =
                                ImmutableArray.Create(
                                    WorkspaceArtifactPath.Create workspace.SolutionPath.Value,
                                    path
                                )
                            Arguments = arguments
                            Intents =
                                if external then
                                    ImmutableHashSet.Create WorkspaceEditIntent.AccessExternalPath
                                else
                                    ImmutableHashSet<WorkspaceEditIntent>.Empty
                            AuthorizedRoots =
                                if external then
                                    ImmutableArray.Create(
                                        WorkspaceArtifactPath.Create root,
                                        WorkspaceArtifactPath.Create(
                                            Path.GetDirectoryName path.Value
                                            |> Option.ofObj
                                            |> Option.defaultValue root
                                        )
                                    )
                                else
                                    ImmutableArray.Create(WorkspaceArtifactPath.Create root) }

                    let actions =
                        Seq.append
                            (plannedActions (SolutionPlan solutionPlan))
                            [ WorkspaceEditAction.Trash path.Value ]
                        |> Seq.toArray

                    return
                        Ok(
                            ContextPlan(
                                previewRequest,
                                actions,
                                [| WorkspaceArtifactPath.Create workspace.SolutionPath.Value
                                   path |]
                            )
                        )
        }

    let private deletePlan
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (context: WorkspaceSemanticContext)
        (arguments: CommandArguments)
        (expectedRevision: int64)
        (cancellationToken: CancellationToken)
        =
        task {
            let request
                (commandId: string)
                (target: WorkspaceNodeId option)
                (commandArguments: CommandArgument list)
                : CommandMutationRequest =
                { CommandId = CommandId.Create commandId
                  TargetWorkspaceNodeId = target
                  Arguments = CommandArguments.Create commandArguments
                  ExpectedRevision = WorkspaceRevision.Create expectedRevision }

            match context.Node.Kind with
            | WorkspaceNodeKind.ProjectFile ->
                match context.ProjectId, context.PhysicalPath with
                | Some projectId, Some path ->
                    let! planned =
                        planLowLevel
                            workspace
                            state
                            (request
                                "project.item.delete"
                                (Some projectId)
                                [ parameter "path" (Path path) ])
                            cancellationToken

                    return
                        planned
                        |> Result.map (fun plan ->
                            { Plan = plan
                              CommandRequest = None
                              Summary = $"Delete {path.Value}"
                              Effects =
                                [| { Operation = "removeFromProject"
                                     Target = path.Value
                                     Recursive = false }
                                   { Operation = "trash"
                                     Target = path.Value
                                     Recursive = false } |]
                              TemplateExecution = None })
                | _ ->
                    return
                        Error(RpcErrors.create "not_found" "The project file was not found." None)
            | WorkspaceNodeKind.ProjectFolder ->
                match context.ProjectId, context.PhysicalPath with
                | Some projectId, Some path ->
                    let! descendants =
                        projectDescendantEffects state projectId path.Value cancellationToken

                    let! planned =
                        planLowLevel
                            workspace
                            state
                            (request
                                "project.folder.delete"
                                (Some projectId)
                                [ parameter "path" (Path path) ])
                            cancellationToken

                    return
                        planned
                        |> Result.map (fun plan ->
                            { Plan = plan
                              CommandRequest = None
                              Summary = $"Delete {path.Value} recursively"
                              Effects =
                                Array.append
                                    descendants
                                    [| { Operation = "trash"
                                         Target = path.Value
                                         Recursive = true } |]
                              TemplateExecution = None })
                | _ ->
                    return
                        Error(RpcErrors.create "not_found" "The project folder was not found." None)
            | WorkspaceNodeKind.Project ->
                let! planned =
                    planLowLevel
                        workspace
                        state
                        (request "solution.project.remove" (Some context.Node.Id) [])
                        cancellationToken

                return
                    planned
                    |> Result.map (fun plan ->
                        { Plan = plan
                          CommandRequest = None
                          Summary = $"Remove {context.Node.Name} from the solution"
                          Effects =
                            [| { Operation = "removeFromSolution"
                                 Target =
                                   context.ProjectPath
                                   |> Option.map _.Value
                                   |> Option.defaultValue context.Node.Name
                                 Recursive = false } |]
                          TemplateExecution = None })
            | WorkspaceNodeKind.SolutionFolder ->
                let! planned =
                    planLowLevel
                        workspace
                        state
                        (request
                            "solution.folder.remove"
                            (Some context.Node.Id)
                            [ parameter "recursive" (Boolean true) ])
                        cancellationToken

                return
                    planned
                    |> Result.map (fun plan ->
                        { Plan = plan
                          CommandRequest = None
                          Summary = $"Remove solution folder {context.Node.Name} recursively"
                          Effects =
                            context.LogicalFolderPath
                            |> Option.map (solutionFolderEffects workspace)
                            |> Option.defaultValue
                                [| { Operation = "removeFromSolution"
                                     Target = context.Node.Name
                                     Recursive = true } |]
                          TemplateExecution = None })
            | WorkspaceNodeKind.SolutionItem ->
                let! planned =
                    solutionItemPlan workspace context arguments expectedRevision cancellationToken

                return
                    planned
                    |> Result.map (fun plan ->
                        let target =
                            context.PhysicalPath
                            |> Option.map _.Value
                            |> Option.defaultValue context.Node.Name

                        { Plan = plan
                          CommandRequest = None
                          Summary = $"Delete solution item {context.Node.Name}"
                          Effects =
                            [| { Operation = "removeFromSolution"
                                 Target = context.Node.Name
                                 Recursive = false }
                               { Operation = "trash"
                                 Target = target
                                 Recursive = false } |]
                          TemplateExecution = None })
            | _ ->
                return
                    Error(
                        RpcErrors.unsupported
                            "Delete is not available for the selected workspace node."
                    )
        }

    let prepareAsync
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (context: WorkspaceSemanticContext)
        (descriptor: CommandDescriptor)
        (arguments: CommandArguments)
        (expectedRevision: int64)
        (cancellationToken: CancellationToken)
        =
        if context.Node.LoadState = WorkspaceNodeLoadState.FilteredOut then
            Task.FromResult(
                Error(
                    RpcErrors.unsupported
                        "Workspace mutations are not available for a filtered project placeholder."
                )
            )
        else
            match descriptor.Id.Value with
            | "workspace.create" ->
                createPlan workspace state context arguments expectedRevision cancellationToken
            | "workspace.delete" ->
                deletePlan workspace state context arguments expectedRevision cancellationToken
            | _ -> invalidArg (nameof descriptor) "A contextual workspace command is required."
