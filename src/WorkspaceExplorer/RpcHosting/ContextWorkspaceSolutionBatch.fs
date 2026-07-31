namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
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

module internal ContextWorkspaceSolutionBatch =
    let private invalid message = Error(RpcErrors.invalidParams message)

    let private effect operation target recursive =
        { Operation = operation
          Target = target
          Recursive = recursive }

    let private targetLogicalFolder (context: WorkspaceSemanticContext) =
        match context.Node.Kind with
        | WorkspaceNodeKind.Workspace -> Some None
        | WorkspaceNodeKind.SolutionFolder -> Some context.LogicalFolderPath
        | _ -> Some context.LogicalFolderPath

    let private saveSolution
        (backingPath: string)
        (serializer: ISolutionSerializer)
        (model: SolutionModel)
        cancellationToken
        =
        task {
            use stream = new MemoryStream()

            match
                Path.GetExtension(backingPath)
                |> Option.ofObj
                |> Option.map _.ToLowerInvariant()
                |> Option.defaultValue String.Empty
            with
            | ".sln" ->
                let single = serializer :?> ISolutionSingleFileSerializer<SlnV12SerializerSettings>
                do! single.SaveAsync(stream, model, cancellationToken)
            | ".slnx" ->
                let single = serializer :?> ISolutionSingleFileSerializer<SlnxSerializerSettings>
                do! single.SaveAsync(stream, model, cancellationToken)
            | _ -> invalidArg (nameof backingPath) "Only .sln and .slnx files can be edited."

            return stream.ToArray()
        }

    let solutionPlan
        (workspace: SolutionWorkspace)
        (target: WorkspaceSemanticContext)
        (sources: WorkspaceSemanticContext array)
        (rename: string option)
        (cancellationToken: CancellationToken)
        =
        task {
            let backingPath = workspace.SolutionPath.Value

            let serializer =
                SolutionSerializers.GetSerializerByMoniker backingPath |> Option.ofObj

            match serializer with
            | None -> return invalid "The solution format is not editable."
            | Some serializer ->
                let! model = serializer.OpenAsync(backingPath, cancellationToken)

                let solutionDirectory =
                    Path.GetDirectoryName backingPath
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory())

                let destinationFolder =
                    targetLogicalFolder target
                    |> Option.flatten
                    |> Option.bind (fun path -> model.FindFolder path |> Option.ofObj)

                let mutable error = None
                let extraActions = ResizeArray<WorkspaceEditAction>()
                let effects = ResizeArray<WorkspaceCommandEffect>()

                for source in sources do
                    if error.IsNone then
                        match source.Node.Kind, rename with
                        | WorkspaceNodeKind.Project, Some name ->
                            match source.ProjectPath with
                            | None -> error <- Some "The project path was not found."
                            | Some path ->
                                let extension = Path.GetExtension path.Value

                                let destination =
                                    Path.Combine(
                                        Path.GetDirectoryName path.Value
                                        |> Option.ofObj
                                        |> Option.defaultValue solutionDirectory,
                                        $"{name}{extension}"
                                    )

                                match
                                    workspace.Contents.Projects
                                    |> Seq.tryFind (fun value -> value.Node.Id = source.Node.Id)
                                    |> Option.bind (fun value ->
                                        model.FindProject value.Path.SolutionRelativePath
                                        |> Option.ofObj)
                                with
                                | None -> error <- Some "The project was not found."
                                | Some project when
                                    ArtifactFiles.exists destination
                                    && not (ArtifactFiles.isCaseOnlyRename path.Value destination)
                                    ->
                                    error <- Some "The project rename destination already exists."
                                | Some project ->
                                    project.FilePath <-
                                        Path.GetRelativePath(solutionDirectory, destination)

                                    extraActions.Add(
                                        WorkspaceEditAction.Rename(path.Value, destination)
                                    )

                                    effects.Add(effect "rename" path.Value false)
                                    effects.Add(effect "create" destination false)
                        | WorkspaceNodeKind.SolutionFolder, Some name ->
                            match source.LogicalFolderPath with
                            | None -> error <- Some "The solution folder was not found."
                            | Some path ->
                                match model.FindFolder path |> Option.ofObj with
                                | None -> error <- Some "The solution folder was not found."
                                | Some folder ->
                                    folder.Name <- name
                                    effects.Add(effect "rename" path true)
                        | WorkspaceNodeKind.SolutionItem, Some name ->
                            match source.LogicalFolderPath, source.PhysicalPath with
                            | Some folderPath, Some path ->
                                match model.FindFolder folderPath |> Option.ofObj with
                                | None -> error <- Some "The solution item folder was not found."
                                | Some folder ->
                                    let relative =
                                        Path.GetRelativePath(solutionDirectory, path.Value)

                                    let destination =
                                        Path.Combine(
                                            Path.GetDirectoryName path.Value
                                            |> Option.ofObj
                                            |> Option.defaultValue solutionDirectory,
                                            name
                                        )

                                    if
                                        ArtifactFiles.exists destination
                                        && not (
                                            ArtifactFiles.isCaseOnlyRename path.Value destination
                                        )
                                    then
                                        error <-
                                            Some "The solution item destination already exists."
                                    elif not (folder.RemoveFile relative) then
                                        error <- Some "The solution item was not found."
                                    else
                                        folder.AddFile(
                                            Path.GetRelativePath(solutionDirectory, destination)
                                        )

                                        extraActions.Add(
                                            WorkspaceEditAction.Rename(path.Value, destination)
                                        )

                                        effects.Add(effect "rename" path.Value false)
                                        effects.Add(effect "create" destination false)
                            | _ -> error <- Some "The solution item was not found."
                        | WorkspaceNodeKind.Project, None ->
                            match
                                workspace.Contents.Projects
                                |> Seq.tryFind (fun value -> value.Node.Id = source.Node.Id)
                                |> Option.bind (fun value ->
                                    model.FindProject value.Path.SolutionRelativePath
                                    |> Option.ofObj)
                            with
                            | None -> error <- Some "The project was not found."
                            | Some project ->
                                project.MoveToFolder(destinationFolder |> Option.toObj)
                                effects.Add(effect "moveInSolution" source.Node.Name false)
                        | WorkspaceNodeKind.SolutionFolder, None ->
                            match source.LogicalFolderPath with
                            | None -> error <- Some "The solution folder was not found."
                            | Some path when
                                target.LogicalFolderPath
                                |> Option.exists (fun destination ->
                                    destination.StartsWith(
                                        path,
                                        StringComparison.OrdinalIgnoreCase
                                    ))
                                ->
                                error <- Some "A solution folder cannot move inside itself."
                            | Some path ->
                                match model.FindFolder path |> Option.ofObj with
                                | None -> error <- Some "The solution folder was not found."
                                | Some folder ->
                                    folder.MoveToFolder(destinationFolder |> Option.toObj)
                                    effects.Add(effect "moveInSolution" path true)
                        | WorkspaceNodeKind.SolutionItem, None ->
                            match
                                source.LogicalFolderPath,
                                source.PhysicalPath,
                                targetLogicalFolder target
                            with
                            | Some sourceFolderPath, Some path, Some(Some destinationFolderPath) ->
                                match
                                    model.FindFolder sourceFolderPath |> Option.ofObj,
                                    model.FindFolder destinationFolderPath |> Option.ofObj
                                with
                                | Some sourceFolder, Some destination ->
                                    let relative =
                                        Path.GetRelativePath(solutionDirectory, path.Value)

                                    if sourceFolder.RemoveFile relative then
                                        destination.AddFile relative
                                        effects.Add(effect "moveInSolution" relative false)
                                    else
                                        error <- Some "The solution item was not found."
                                | _ -> error <- Some "The solution item folder was not found."
                            | _ ->
                                error <-
                                    Some
                                        "Solution items require a logical solution-folder destination."
                        | _ -> error <- Some "The source is not supported by this solution edit."

                match error with
                | Some message -> return invalid message
                | None ->
                    let! contents = saveSolution backingPath serializer model cancellationToken

                    let actions =
                        Seq.append
                            extraActions
                            [ WorkspaceEditAction.ReplaceFile(backingPath, contents) ]
                        |> Seq.toArray

                    return Ok(actions, effects.ToArray())
        }
