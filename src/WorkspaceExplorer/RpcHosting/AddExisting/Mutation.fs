namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces
open Microsoft.VisualStudio.SolutionPersistence
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Microsoft.VisualStudio.SolutionPersistence.Serializer.SlnV12
open Microsoft.VisualStudio.SolutionPersistence.Serializer.Xml

[<RequireQualifiedAccess>]
module internal AddExistingMutation =
    let private extension (path: string) =
        Path.GetExtension path |> Option.ofObj |> Option.defaultValue String.Empty

    let private argumentText id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = id then
                match argument.Value with
                | Text value -> Some value
                | _ -> None
            else
                None)

    let private argumentTexts id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = id then
                match argument.Value with
                | TextArray values -> Some(values |> Seq.toArray)
                | _ -> None
            else
                None)

    let private saveSolution
        (path: string)
        (serializer: ISolutionSerializer)
        (model: SolutionModel)
        cancellationToken
        =
        task {
            use stream = new MemoryStream()

            match extension path |> _.ToLowerInvariant() with
            | ".sln" ->
                let single = serializer :?> ISolutionSingleFileSerializer<SlnV12SerializerSettings>
                do! single.SaveAsync(stream, model, cancellationToken)
            | ".slnx" ->
                let single = serializer :?> ISolutionSingleFileSerializer<SlnxSerializerSettings>
                do! single.SaveAsync(stream, model, cancellationToken)
            | _ -> invalidArg (nameof path) "Only .sln and .slnx files can be edited."

            return stream.ToArray()
        }

    let private projectExtension (path: string) =
        match extension path |> _.ToLowerInvariant() with
        | ".csproj"
        | ".fsproj"
        | ".vbproj" -> true
        | _ -> false

    let private projectPlanAsync
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (target: WorkspaceSemanticContext)
        (entries: AddExistingEntry array)
        cancellationToken
        =
        task {
            match target.ProjectId with
            | None -> return Error(RpcErrors.invalidParams "A project target is required.")
            | Some projectId ->
                match! state.ProjectAsync(projectId, cancellationToken) with
                | Failure failure -> return Error(WorkspaceRpcResponses.failureError failure)
                | Success(_, project, snapshot) ->
                    let projectPath = project.Path.AbsolutePath.Value

                    let directory =
                        Path.GetDirectoryName projectPath
                        |> Option.ofObj
                        |> Option.defaultValue (Directory.GetCurrentDirectory())

                    let document, encoding, preamble, lineEnding =
                        MsBuildProjectDocument.readDocument projectPath

                    let mutable changed = false
                    let mutable error = None

                    for entry in entries do
                        if projectExtension entry.Path then
                            error <- Some "Project files cannot be added as project items."
                        else
                            let alreadyIncluded =
                                snapshot.Dimensions
                                |> Seq.collect _.Items
                                |> Seq.exists (fun item ->
                                    item.ResolvedPath
                                    |> Option.ofObj
                                    |> Option.exists (fun path ->
                                        ArtifactFiles.identity path.Value = ArtifactFiles.identity
                                            entry.Path))

                            if alreadyIncluded then
                                error <-
                                    Some "A selected item is already registered by the project."
                            else
                                let itemType =
                                    ProjectItemInclusion.defaultItemType snapshot entry.Path
                                    |> Option.defaultWith (fun () ->
                                        match extension entry.Path |> _.ToLowerInvariant() with
                                        | ".cs"
                                        | ".fs"
                                        | ".vb" -> "Compile"
                                        | ".resx"
                                        | ".resw" -> "EmbeddedResource"
                                        | _ -> "None")

                                let includeValue =
                                    ProjectItemInclusion.relativePath
                                        directory
                                        (WorkspaceArtifactPath.Create entry.Path)

                                MsBuildProjectDocument.appendItem document itemType includeValue []
                                changed <- true

                    match error with
                    | Some message -> return Error(RpcErrors.invalidParams message)
                    | None when not changed ->
                        return Error(RpcErrors.invalidParams "No project membership changed.")
                    | None ->
                        let contents =
                            MsBuildProjectDocument.saveDocument
                                document
                                encoding
                                preamble
                                lineEnding

                        return
                            Ok(
                                [| WorkspaceEditAction.ReplaceGeneratedDocument(
                                       projectPath,
                                       contents
                                   ) |],
                                [| projectPath |]
                            )
        }

    let private solutionPlanAsync
        (workspace: SolutionWorkspace)
        (target: WorkspaceSemanticContext)
        (entries: AddExistingEntry array)
        cancellationToken
        =
        task {
            let solutionPath = workspace.SolutionPath.Value

            let serializer =
                SolutionSerializers.GetSerializerByMoniker solutionPath |> Option.ofObj

            match serializer with
            | None -> return Error(RpcErrors.unsupported "The solution format is not editable.")
            | Some serializer ->
                let! model = serializer.OpenAsync(solutionPath, cancellationToken)

                let solutionDirectory =
                    Path.GetDirectoryName solutionPath
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory())

                let folder =
                    match target.Node.Kind, target.LogicalFolderPath with
                    | WorkspaceNodeKind.Workspace, _ -> Some None
                    | WorkspaceNodeKind.SolutionFolder, Some path ->
                        model.FindFolder path |> Option.ofObj |> Option.map Some
                    | _ -> None

                match folder with
                | None -> return Error(RpcErrors.invalidParams "The solution folder was not found.")
                | Some folder ->
                    let comparison =
                        match
                            FileSystemCaseSensitivityDetector.DetectFromExistingPath solutionPath
                        with
                        | FileSystemCaseSensitivity.Insensitive -> StringComparer.OrdinalIgnoreCase
                        | _ -> StringComparer.Ordinal

                    let mutable error = None
                    let effects = ResizeArray<WorkspaceCommandEffect>()

                    for entry in entries do
                        if error.IsNone then
                            let relative = Path.GetRelativePath(solutionDirectory, entry.Path)

                            if projectExtension entry.Path then
                                if
                                    model.SolutionProjects
                                    |> Seq.exists (fun (project: SolutionProjectModel) ->
                                        comparison.Equals(project.FilePath, relative))
                                then
                                    error <- Some "A selected project is already in the solution."
                                else
                                    model.AddProject(relative, null, folder |> Option.toObj)
                                    |> ignore

                                    effects.Add
                                        { Operation = "addToSolution"
                                          Target = entry.DisplayName
                                          Recursive = false }
                            else
                                match folder with
                                | None ->
                                    error <-
                                        Some
                                            "Only C#, F#, and VB project files can be added at the solution root."
                                | Some folder ->
                                    if
                                        folder.Files
                                        |> Option.ofObj
                                        |> Option.map (fun files ->
                                            files
                                            |> Seq.exists (fun path ->
                                                comparison.Equals(path, relative)))
                                        |> Option.defaultValue false
                                    then
                                        error <-
                                            Some
                                                "A selected item is already in the solution folder."
                                    else
                                        folder.AddFile relative

                                        effects.Add
                                            { Operation = "addToSolution"
                                              Target = entry.DisplayName
                                              Recursive = false }

                    match error with
                    | Some message -> return Error(RpcErrors.invalidParams message)
                    | None ->
                        let! contents = saveSolution solutionPath serializer model cancellationToken

                        return
                            Ok(
                                [| WorkspaceEditAction.ReplaceGeneratedDocument(
                                       solutionPath,
                                       contents
                                   ) |],
                                [| solutionPath |],
                                effects.ToArray()
                            )
        }

    let prepareAsync
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (selector: AddExistingSelector)
        (target: WorkspaceSemanticContext)
        (arguments: CommandArguments)
        expectedRevision
        cancellationToken
        =
        task {
            match argumentText "selectorId" arguments, argumentTexts "entryIds" arguments with
            | Some selectorId, Some entryIds ->
                match
                    selector.ResolveSelection(
                        selectorId,
                        expectedRevision,
                        target.Node.Id.Value,
                        entryIds
                    )
                with
                | Error error -> return Error error
                | Ok(session, entries) ->
                    let! planned =
                        match target.Node.Kind with
                        | WorkspaceNodeKind.Workspace
                        | WorkspaceNodeKind.SolutionFolder ->
                            task {
                                match!
                                    solutionPlanAsync workspace target entries cancellationToken
                                with
                                | Error error -> return Error error
                                | Ok(actions, paths, effects) -> return Ok(actions, paths, effects)
                            }
                        | WorkspaceNodeKind.Project
                        | WorkspaceNodeKind.ProjectFolder ->
                            task {
                                match!
                                    projectPlanAsync
                                        workspace
                                        state
                                        target
                                        entries
                                        cancellationToken
                                with
                                | Error error -> return Error error
                                | Ok(actions, paths) ->
                                    let effects =
                                        entries
                                        |> Array.map (fun entry ->
                                            { Operation = "addToProject"
                                              Target = entry.DisplayName
                                              Recursive = false })

                                    return Ok(actions, paths, effects)
                            }
                        | _ ->
                            Task.FromResult(
                                Error(RpcErrors.unsupported "Add Existing is unavailable here.")
                            )

                    return
                        planned
                        |> Result.map (fun (actions, mutationPaths, effects) ->
                            let allTargets =
                                Seq.append mutationPaths (entries |> Seq.map _.Path)
                                |> Seq.distinct
                                |> Seq.map WorkspaceArtifactPath.Create
                                |> ImmutableArray.CreateRange

                            let request =
                                { CommandId = CommandId.Create "workspace.addExisting"
                                  Targets = allTargets
                                  Arguments = arguments
                                  ExpectedRevision = WorkspaceRevision.Create expectedRevision
                                  Intents = ImmutableHashSet.Create WorkspaceEditIntent.Overwrite
                                  AuthorizedRoots =
                                    ImmutableArray.Create(
                                        WorkspaceArtifactPath.Create session.RootPath
                                    ) }

                            { Plan =
                                ContextPlan(
                                    request,
                                    actions,
                                    mutationPaths
                                    |> Seq.map WorkspaceArtifactPath.Create
                                    |> Seq.toArray
                                )
                              CommandRequest = None
                              Summary = $"Add {entries.Length} existing workspace item(s)"
                              Effects = effects
                              TemplateExecution = None })
            | _ -> return Error(RpcErrors.invalidParams "selectorId and entryIds are required.")
        }
