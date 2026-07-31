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

type private PhysicalBatchSource =
    { Context: WorkspaceSemanticContext
      Source: string
      Destination: string
      IsDirectory: bool
      SourceProject: SolutionProject
      DestinationProject: SolutionProject }

type private ProjectDocumentState =
    { Document: XDocument
      Encoding: Text.Encoding
      HasPreamble: bool
      LineEnding: string }


module internal ContextWorkspacePhysicalBatch =
    let private invalid message = Error(RpcErrors.invalidParams message)

    let argumentText id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = id then
                match argument.Value with
                | Text value -> Some value
                | _ -> None
            else
                None)

    let private argumentNodes id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = id then
                match argument.Value with
                | NodeIdArray values -> Some values
                | _ -> None
            else
                None)

    let safeName (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value <> "."
        && value <> ".."
        && not (Path.IsPathRooted value)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && value.IndexOfAny [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |] < 0

    let isUnder directory path =
        let relative = Path.GetRelativePath(directory, path)

        relative = "."
        || (not (Path.IsPathRooted relative)
            && relative <> ".."
            && not (
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            )
            && not (
                relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            ))

    let private findProject (workspace: SolutionWorkspace) id =
        workspace.Contents.Projects |> Seq.tryFind (fun project -> project.Node.Id = id)

    let private physicalDirectory (context: WorkspaceSemanticContext) =
        match context.Node.Kind with
        | WorkspaceNodeKind.ProjectFolder -> context.PhysicalPath
        | _ -> context.PhysicalDirectory

    let private targetLogicalFolder (context: WorkspaceSemanticContext) =
        match context.Node.Kind with
        | WorkspaceNodeKind.Workspace -> Some None
        | WorkspaceNodeKind.SolutionFolder -> Some context.LogicalFolderPath
        | _ -> Some context.LogicalFolderPath

    let resolveSources
        (state: WorkspaceIndex)
        (arguments: CommandArguments)
        expectedRevision
        (cancellationToken: CancellationToken)
        =
        task {
            match argumentNodes "sourceNodeIds" arguments with
            | None -> return invalid "The sourceNodeIds argument is required."
            | Some ids ->
                let resolved = ResizeArray<WorkspaceSemanticContext>()
                let mutable failure = None

                for id in ids do
                    if failure.IsNone then
                        let! context =
                            state.SemanticContextAsync(
                                id.Value,
                                Some expectedRevision,
                                cancellationToken
                            )

                        match context with
                        | Error error -> failure <- Some error
                        | Ok(_, value) -> resolved.Add value

                return
                    match failure with
                    | Some error -> Error error
                    | None -> Ok(resolved.ToArray())
        }

    let normalizeSources (sources: WorkspaceSemanticContext array) =
        let unique =
            sources
            |> Array.distinctBy _.Node.Id
            |> Array.sortBy (fun source ->
                source.PhysicalPath
                |> Option.map (fun path -> path.Value.Length)
                |> Option.defaultValue Int32.MaxValue)

        unique
        |> Array.filter (fun source ->
            unique
            |> Array.exists (fun ancestor ->
                ancestor.Node.Id <> source.Node.Id
                && match ancestor.PhysicalPath, source.PhysicalPath with
                   | Some parent, Some child when
                       ancestor.Node.Kind = WorkspaceNodeKind.ProjectFolder
                       ->
                       isUnder parent.Value child.Value
                   | _ ->
                       match ancestor.LogicalFolderPath, source.LogicalFolderPath with
                       | Some parent, Some child when
                           ancestor.Node.Kind = WorkspaceNodeKind.SolutionFolder
                           ->
                           child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                       | _ -> false)
            |> not)

    let private effect operation target recursive =
        { Operation = operation
          Target = target
          Recursive = recursive }

    let private documentState path =
        let document, encoding, hasPreamble, lineEnding =
            MsBuildProjectDocument.readDocument path

        { Document = document
          Encoding = encoding
          HasPreamble = hasPreamble
          LineEnding = lineEnding }

    let private documentBytes state =
        MsBuildProjectDocument.saveDocument
            state.Document
            state.Encoding
            state.HasPreamble
            state.LineEnding

    let private relative directory path =
        ProjectItemInclusion.relativePath directory (WorkspaceArtifactPath.Create path)

    let private itemElements (document: XDocument) =
        document.Descendants()
        |> Seq.filter (fun element ->
            ProjectItemInclusion.itemTypes.Contains element.Name.LocalName)

    let private declaredPath directory (element: XElement) =
        [ "Include"; "Update"; "Remove" ]
        |> Seq.tryPick (fun name ->
            MsBuildProjectDocument.attribute name element
            |> Option.bind (fun value ->
                if value.Contains("$(", StringComparison.Ordinal) then
                    None
                else
                    try
                        Some(
                            Path.GetFullPath(
                                value.Replace('/', Path.DirectorySeparatorChar),
                                directory
                            )
                        )
                    with
                    | :? ArgumentException
                    | :? NotSupportedException
                    | :? PathTooLongException -> None))

    let private moveExplicitItems
        (state: ProjectDocumentState)
        sourceDirectory
        sourceRoot
        destinationDirectory
        destinationRoot
        copy
        =
        let relocate sourcePath =
            if
                String.Equals(
                    Path.GetFullPath sourcePath,
                    Path.GetFullPath sourceRoot,
                    StringComparison.Ordinal
                )
            then
                destinationRoot
            else
                let suffix = Path.GetRelativePath(sourceRoot, sourcePath)
                Path.GetFullPath(suffix, destinationRoot)

        let matches =
            itemElements state.Document
            |> Seq.choose (fun element ->
                declaredPath sourceDirectory element
                |> Option.filter (isUnder sourceRoot)
                |> Option.map (fun path -> element, path))
            |> Seq.toArray

        for element, sourcePath in matches do
            let destinationPath = relocate sourcePath

            let attribute =
                [ "Include"; "Update" ]
                |> Seq.tryPick (fun name ->
                    element.Attribute(MsBuildProjectDocument.name name) |> Option.ofObj)

            match attribute with
            | None -> ()
            | Some attribute when copy ->
                let clone = XElement element

                let cloneAttribute =
                    clone.Attribute(attribute.Name)
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> invalidOp "The project item is malformed.")

                cloneAttribute.Value <- relative destinationDirectory destinationPath
                element.AddAfterSelf clone
            | Some attribute -> attribute.Value <- relative destinationDirectory destinationPath

    let private evaluatedItems (snapshot: ProjectEvaluationSnapshot) sourceRoot destinationRoot =
        let relocate sourcePath =
            if
                String.Equals(
                    Path.GetFullPath sourcePath,
                    Path.GetFullPath sourceRoot,
                    StringComparison.Ordinal
                )
            then
                destinationRoot
            else
                let suffix = Path.GetRelativePath(sourceRoot, sourcePath)
                Path.GetFullPath(suffix, destinationRoot)

        snapshot.Dimensions
        |> Seq.collect _.Items
        |> Seq.choose (fun item ->
            item.ResolvedPath
            |> Option.ofObj
            |> Option.map _.Value
            |> Option.filter (isUnder sourceRoot)
            |> Option.map (fun sourcePath -> item.ItemType, sourcePath, relocate sourcePath))
        |> Seq.filter (fun (itemType, _, _) -> ProjectItemInclusion.itemTypes.Contains itemType)
        |> Seq.distinct
        |> Seq.toArray

    let private removeMembership
        (state: ProjectDocumentState)
        projectDirectory
        (snapshot: ProjectEvaluationSnapshot)
        itemType
        sourcePath
        =
        let includeValue = relative projectDirectory sourcePath

        itemElements state.Document
        |> Seq.filter (fun element ->
            element.Name.LocalName = itemType
            && ([ "Include"; "Update" ]
                |> Seq.exists (fun name ->
                    MsBuildProjectDocument.attribute name element = Some includeValue)))
        |> Seq.toArray
        |> Array.iter MsBuildProjectDocument.removeItem

        if ProjectItemInclusion.defaultItemPolicy snapshot itemType sourcePath then
            MsBuildProjectDocument.appendRemove state.Document itemType includeValue

    let private addMembership
        (state: ProjectDocumentState)
        projectDirectory
        (snapshot: ProjectEvaluationSnapshot)
        itemType
        destinationPath
        =
        ProjectItemInclusion.appendRequestedItem
            state.Document
            snapshot
            itemType
            destinationPath
            (relative projectDirectory destinationPath)
        |> ignore

    let private projectData
        (state: WorkspaceIndex)
        (projects: SolutionProject array)
        (cancellationToken: CancellationToken)
        =
        task {
            let values =
                Dictionary<WorkspaceNodeId, SolutionProject * ProjectEvaluationSnapshot>()

            let mutable failure = None

            for project in projects |> Array.distinctBy _.Node.Id do
                if failure.IsNone then
                    let! outcome = state.ProjectAsync(project.Node.Id, cancellationToken)

                    match outcome with
                    | Failure value -> failure <- Some(WorkspaceRpcResponses.failureError value)
                    | Success(_, _, snapshot) -> values.Add(project.Node.Id, (project, snapshot))

            return
                match failure with
                | Some error -> Error error
                | None -> Ok values
        }

    let physicalPlan
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (target: WorkspaceSemanticContext)
        (sources: WorkspaceSemanticContext array)
        copy
        (rename: string option)
        (cancellationToken: CancellationToken)
        =
        task {
            let selectedDirectory =
                match rename, target.PhysicalPath with
                | Some _, Some path ->
                    Path.GetDirectoryName path.Value
                    |> Option.ofObj
                    |> Option.map WorkspaceArtifactPath.Create
                | _ -> physicalDirectory target

            match selectedDirectory, target.ProjectId with
            | Some destinationDirectory, Some destinationProjectId ->
                let destinationProject = findProject workspace destinationProjectId

                match destinationProject with
                | None -> return invalid "The destination project was not found."
                | Some destinationProject ->
                    let planned =
                        sources
                        |> Array.map (fun source ->
                            match source.PhysicalPath, source.ProjectId with
                            | Some sourcePath, Some sourceProjectId when
                                source.Node.Kind = WorkspaceNodeKind.ProjectFile
                                || source.Node.Kind = WorkspaceNodeKind.ProjectFolder
                                ->
                                match findProject workspace sourceProjectId with
                                | None -> Error "A source project was not found."
                                | Some sourceProject ->
                                    let isDirectory =
                                        source.Node.Kind = WorkspaceNodeKind.ProjectFolder

                                    let name =
                                        rename
                                        |> Option.defaultWith (fun () ->
                                            Path.GetFileName sourcePath.Value
                                            |> Option.ofObj
                                            |> Option.defaultValue source.Node.Name)

                                    let destination =
                                        Path.GetFullPath(
                                            Path.Combine(destinationDirectory.Value, name)
                                        )

                                    Ok
                                        { Context = source
                                          Source = sourcePath.Value
                                          Destination = destination
                                          IsDirectory = isDirectory
                                          SourceProject = sourceProject
                                          DestinationProject = destinationProject }
                            | _ ->
                                Error "Only physical project files and directories are supported.")

                    match
                        planned
                        |> Array.tryPick (function
                            | Error error -> Some error
                            | Ok _ -> None)
                    with
                    | Some message -> return invalid message
                    | None ->
                        let operations = planned |> Array.choose Result.toOption

                        let destinations =
                            operations
                            |> Array.map (fun operation -> Path.GetFullPath operation.Destination)

                        let collision =
                            destinations
                            |> Array.map ArtifactFiles.identity
                            |> Array.distinct
                            |> Array.length
                            <> destinations.Length
                            || operations
                               |> Array.exists (fun operation ->
                                   let caseOnly =
                                       ArtifactFiles.isCaseOnlyRename
                                           operation.Source
                                           operation.Destination

                                   operation.Source = operation.Destination
                                   || (ArtifactFiles.exists operation.Destination && not caseOnly)
                                   || (operation.IsDirectory
                                       && not caseOnly
                                       && isUnder operation.Source operation.Destination))

                        if collision then
                            return
                                invalid
                                    "The batch contains a cycle, overlap, or destination collision."
                        else
                            let projects =
                                operations
                                |> Array.collect (fun operation ->
                                    [| operation.SourceProject; operation.DestinationProject |])

                            let! projectData = projectData state projects cancellationToken

                            match projectData with
                            | Error error -> return Error error
                            | Ok projectData ->
                                let documents =
                                    projects
                                    |> Array.distinctBy _.Node.Id
                                    |> Array.map (fun project ->
                                        project.Node.Id,
                                        documentState project.Path.AbsolutePath.Value)
                                    |> dict

                                for operation in operations do
                                    let sourceProject, sourceSnapshot =
                                        projectData[operation.SourceProject.Node.Id]

                                    let destinationProject, destinationSnapshot =
                                        projectData[operation.DestinationProject.Node.Id]

                                    let sourceDirectory =
                                        ProjectItemInclusion.projectDirectory sourceProject

                                    let destinationProjectDirectory =
                                        ProjectItemInclusion.projectDirectory destinationProject

                                    let sourceDocument = documents[operation.SourceProject.Node.Id]

                                    let destinationDocument =
                                        documents[operation.DestinationProject.Node.Id]

                                    if
                                        operation.SourceProject.Node.Id = operation.DestinationProject.Node.Id
                                    then
                                        moveExplicitItems
                                            sourceDocument
                                            sourceDirectory
                                            operation.Source
                                            sourceDirectory
                                            operation.Destination
                                            copy
                                    else
                                        let items =
                                            evaluatedItems
                                                sourceSnapshot
                                                operation.Source
                                                operation.Destination

                                        for itemType, sourcePath, destinationPath in items do
                                            if not copy then
                                                removeMembership
                                                    sourceDocument
                                                    sourceDirectory
                                                    sourceSnapshot
                                                    itemType
                                                    sourcePath

                                            addMembership
                                                destinationDocument
                                                destinationProjectDirectory
                                                destinationSnapshot
                                                itemType
                                                destinationPath

                                let artifactActions =
                                    operations
                                    |> Array.map (fun operation ->
                                        if copy then
                                            WorkspaceEditAction.Copy(
                                                operation.Source,
                                                operation.Destination
                                            )
                                        elif rename.IsSome then
                                            WorkspaceEditAction.Rename(
                                                operation.Source,
                                                operation.Destination
                                            )
                                        else
                                            WorkspaceEditAction.Move(
                                                operation.Source,
                                                operation.Destination
                                            ))

                                let projectActions =
                                    projects
                                    |> Array.distinctBy _.Node.Id
                                    |> Array.map (fun project ->
                                        WorkspaceEditAction.ReplaceGeneratedDocument(
                                            project.Path.AbsolutePath.Value,
                                            documentBytes documents[project.Node.Id]
                                        ))

                                let actions = Array.append artifactActions projectActions

                                let effects =
                                    operations
                                    |> Array.collect (fun operation ->
                                        [| effect
                                               (if copy then "copy"
                                                elif rename.IsSome then "rename"
                                                else "move")
                                               operation.Source
                                               operation.IsDirectory
                                           effect
                                               (if copy || rename.IsSome then "create" else "move")
                                               operation.Destination
                                               operation.IsDirectory |])

                                return Ok(actions, effects)
            | _ -> return invalid "The selected destination has no eligible physical directory."
        }
