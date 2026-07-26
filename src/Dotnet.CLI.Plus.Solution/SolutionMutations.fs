namespace Dotnet.CLI.Plus.Solution

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Microsoft.VisualStudio.SolutionPersistence
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Microsoft.VisualStudio.SolutionPersistence.Serializer.SlnV12
open Microsoft.VisualStudio.SolutionPersistence.Serializer.Xml

type SolutionMutationPlan =
    { Request: MutationPreviewRequest
      Contents: byte array
      BackingPath: WorkspaceArtifactPath
      FileRename: SolutionFileRename option }

and SolutionFileRename =
    { Source: WorkspaceArtifactPath
      Destination: WorkspaceArtifactPath }

module private SolutionMutations =
    let diagnostic code message =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            false,
            CorrelationId.New()
        )

    let invalid name message =
        Failure(InvalidInput(name, diagnostic "invalid_input" message))

    let missing name message =
        Failure(NotFound(name, diagnostic "not_found" message))

    let internalFailure message =
        Failure(Internal(diagnostic "internal_error" message))

    let parameter id parameterType required name =
        CommandParameterDescriptor.Create(
            CommandParameterId.Create id,
            parameterType,
            required,
            name
        )

    let command id name parameters targets =
        CommandDescriptor.Create(
            CommandId.Create id,
            name,
            CommandAccess.Write,
            parameters,
            targets
        )

    let catalog =
        ImmutableArray.CreateRange
            [ command
                  "solution.folder.add"
                  "Add solution folder"
                  [ parameter "name" CommandParameterType.Text true "Name" ]
                  [ WorkspaceNodeKind.Workspace; WorkspaceNodeKind.SolutionFolder ]
              command
                  "solution.folder.import-directory"
                  "Import directory as solution folder"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
                  [ WorkspaceNodeKind.Workspace ]
              command
                  "solution.folder.remove"
                  "Remove solution folder"
                  [ parameter "recursive" CommandParameterType.Boolean false "Recursive" ]
                  [ WorkspaceNodeKind.SolutionFolder ]
              command
                  "solution.item.add"
                  "Add solution item"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
                  [ WorkspaceNodeKind.SolutionFolder ]
              command
                  "solution.item.remove"
                  "Remove solution item"
                  []
                  [ WorkspaceNodeKind.SolutionItem ]
              command
                  "solution.project.add"
                  "Add project"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
                  [ WorkspaceNodeKind.Workspace; WorkspaceNodeKind.SolutionFolder ]
              command "solution.project.remove" "Remove project" [] [ WorkspaceNodeKind.Project ]
              command
                  "solution.project.rename"
                  "Rename project"
                  [ parameter "name" CommandParameterType.Text true "Name" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.project.move"
                  "Move project"
                  [ parameter "folder" CommandParameterType.NodeId false "Folder" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.project.update-path"
                  "Update project path"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.build-type.add"
                  "Add build type"
                  [ parameter "name" CommandParameterType.Text true "Name" ]
                  [ WorkspaceNodeKind.Workspace ]
              command
                  "solution.build-type.remove"
                  "Remove build type"
                  []
                  [ WorkspaceNodeKind.Configuration ]
              command
                  "solution.platform.add"
                  "Add platform"
                  [ parameter "name" CommandParameterType.Text true "Name" ]
                  [ WorkspaceNodeKind.Workspace ]
              command "solution.platform.remove" "Remove platform" [] [ WorkspaceNodeKind.Platform ]
              command
                  "solution.project-configuration.set"
                  "Set project configuration"
                  [ parameter
                        "solutionBuildType"
                        CommandParameterType.Text
                        true
                        "Solution build type"
                    parameter "solutionPlatform" CommandParameterType.Text true "Solution platform"
                    parameter "projectBuildType" CommandParameterType.Text true "Project build type"
                    parameter "projectPlatform" CommandParameterType.Text true "Project platform"
                    parameter "builds" CommandParameterType.Boolean true "Builds"
                    parameter "deploys" CommandParameterType.Boolean true "Deploys" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.project-configuration.remove"
                  "Remove project configuration"
                  [ parameter
                        "solutionBuildType"
                        CommandParameterType.Text
                        true
                        "Solution build type"
                    parameter "solutionPlatform" CommandParameterType.Text true "Solution platform" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.dependency.add"
                  "Add solution dependency"
                  [ parameter "dependency" CommandParameterType.NodeId true "Dependency" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.dependency.remove"
                  "Remove solution dependency"
                  [ parameter "dependency" CommandParameterType.NodeId true "Dependency" ]
                  [ WorkspaceNodeKind.Project ] ]


    let descriptor (commandId: CommandId) =
        catalog |> Seq.tryFind (fun candidate -> candidate.CommandId = commandId)

    let argument argumentId (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun candidate ->
            if candidate.ParameterId.Value = argumentId then
                Some candidate.Value
            else
                None)

    let requiredText name arguments =
        match argument name arguments with
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let requiredPath name arguments =
        match argument name arguments with
        | Some(Path value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let optionalBoolean name defaultValue arguments =
        match argument name arguments with
        | None -> Ok defaultValue
        | Some(Boolean value) -> Ok value
        | _ -> Error $"'{name}' must be a boolean."

    let requiredBoolean name arguments =
        match argument name arguments with
        | Some(Boolean value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let requiredNode name arguments =
        match argument name arguments with
        | Some(Node value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let optionalNode name arguments =
        match argument name arguments with
        | None -> Ok None
        | Some(Node value) -> Ok(Some value)
        | _ -> Error $"'{name}' must be a node ID."

    let validateArguments (descriptor: CommandDescriptor) (arguments: CommandArguments) =
        let invalidArgument =
            arguments.Values
            |> Seq.tryPick (fun value ->
                match
                    descriptor.ParameterDescriptors
                    |> Seq.tryFind (fun expected -> expected.ParameterId = value.ParameterId)
                with
                | None -> Some $"Unknown argument '{value.ParameterId.Value}'."
                | Some expected ->
                    let valid =
                        match expected.ParameterType, value.Value with
                        | CommandParameterType.Text, Text text ->
                            not (String.IsNullOrWhiteSpace text)
                        | CommandParameterType.Path, Path _
                        | CommandParameterType.Boolean, Boolean _
                        | CommandParameterType.Integer, Integer _
                        | CommandParameterType.NodeId, Node _
                        | CommandParameterType.Choice, Choice _ -> true
                        | _ -> false

                    if valid then
                        None
                    else
                        Some $"Argument '{value.ParameterId.Value}' has the wrong type or value.")

        match invalidArgument with
        | Some error -> Error error
        | None ->
            descriptor.ParameterDescriptors
            |> Seq.tryFind (fun expected ->
                expected.Required
                && arguments.Values
                   |> Seq.exists (fun value -> value.ParameterId = expected.ParameterId)
                   |> not)
            |> Option.map (fun missing -> Error $"'{missing.ParameterId.Value}' is required.")
            |> Option.defaultValue (Ok())

    let relativePath (solutionDirectory: string) (path: WorkspaceArtifactPath) =
        let absolute = Path.GetFullPath path.Value
        let relative = Path.GetRelativePath(solutionDirectory, absolute)
        relative, absolute

    let external (relative: string) =
        relative = ".."
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)

    let artifactExists path =
        let file = FileInfo path :> FileSystemInfo
        let directory = DirectoryInfo path :> FileSystemInfo

        File.Exists path
        || Directory.Exists path
        || not (isNull file.LinkTarget)
        || not (isNull directory.LinkTarget)

    let folderPath (parent: string option) (name: string) =
        if
            String.IsNullOrWhiteSpace name
            || name.IndexOfAny [| '/'; '\\' |] >= 0
            || name = "."
            || name = ".."
        then
            Error "Folder names must be a single non-empty path segment."
        else
            let parentPath = parent |> Option.defaultValue "/"
            Ok $"{parentPath}{name}/"

    let comparer (workspace: SolutionWorkspace) =
        match HostFileSystemCaseDetector.DetectFromExistingPath workspace.BackingPath.Value with
        | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase
        | _ -> StringComparer.Ordinal

    let samePath (comparison: StringComparer) (left: string) (right: string) =
        comparison.Compare(left, right) = 0

    let findFolder (workspace: SolutionWorkspace) (nodeId: NodeId) =
        workspace.RootProjection.Folders
        |> Seq.tryFind (fun folder -> folder.Node.NodeId = nodeId)

    let findProject (workspace: SolutionWorkspace) (nodeId: NodeId) =
        workspace.RootProjection.Projects
        |> Seq.tryFind (fun project -> project.Node.NodeId = nodeId)

    let findItem (workspace: SolutionWorkspace) (nodeId: NodeId) =
        workspace.RootProjection.Items
        |> Seq.tryFind (fun item -> item.Node.NodeId = nodeId)

    let findConfiguration (workspace: SolutionWorkspace) (nodeId: NodeId) =
        workspace.RootProjection.BuildTypes
        |> Seq.tryFind (fun node -> node.NodeId = nodeId)

    let findPlatform (workspace: SolutionWorkspace) (nodeId: NodeId) =
        workspace.RootProjection.Platforms
        |> Seq.tryFind (fun node -> node.NodeId = nodeId)

    let modelFolder (model: SolutionModel) (path: string) = model.FindFolder path |> Option.ofObj
    let modelProject (model: SolutionModel) (path: string) = model.FindProject path |> Option.ofObj

    let saveToMemory
        (backingPath: string)
        (serializer: ISolutionSerializer)
        (model: SolutionModel)
        cancellationToken
        =
        task {
            use stream = new MemoryStream()

            match Path.GetExtension(backingPath).ToLowerInvariant() with
            | ".sln" ->
                let single = serializer :?> ISolutionSingleFileSerializer<SlnV12SerializerSettings>
                do! single.SaveAsync(stream, model, cancellationToken)
            | ".slnx" ->
                let single = serializer :?> ISolutionSingleFileSerializer<SlnxSerializerSettings>
                do! single.SaveAsync(stream, model, cancellationToken)
            | _ -> invalidArg (nameof backingPath) "Only .sln and .slnx files can be saved."

            return stream.ToArray()
        }

    let mutationRequest
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (arguments: CommandArguments)
        (externalPaths: string list)
        (transactionPaths: string list)
        (additionalIntents: MutationIntent list)
        =
        let solutionDirectory =
            Path.GetDirectoryName workspace.BackingPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let externalRoots =
            externalPaths
            |> Seq.map (fun path ->
                Path.GetDirectoryName path
                |> Option.ofObj
                |> Option.defaultValue solutionDirectory
                |> WorkspaceArtifactPath.Create)
            |> ImmutableArray.CreateRange

        let targets =
            seq {
                yield workspace.BackingPath
                yield! externalPaths |> Seq.map WorkspaceArtifactPath.Create
                yield! transactionPaths |> Seq.map WorkspaceArtifactPath.Create
            }
            |> Seq.distinct
            |> ImmutableArray.CreateRange

        let intents =
            let values =
                [ yield MutationIntent.Overwrite
                  if not (Seq.isEmpty externalPaths) then
                      yield MutationIntent.AccessExternalPath
                  yield! additionalIntents ]

            ImmutableHashSet.CreateRange values

        { CommandId = command.CommandId
          Targets = targets
          Arguments = arguments
          ExpectedRevision = command.ExpectedRevision
          Intents = intents
          AuthorizedRoots =
            seq {
                yield WorkspaceArtifactPath.Create solutionDirectory
                yield! externalRoots
            }
            |> Seq.distinct
            |> ImmutableArray.CreateRange }

    let isEmptyFolder (workspace: SolutionWorkspace) (comparison: StringComparer) (path: string) =
        let childFolder =
            workspace.RootProjection.Folders
            |> Seq.exists (fun folder ->
                folder.ParentPath
                |> Option.exists (fun parent -> samePath comparison parent path))

        let childProject =
            workspace.RootProjection.Projects
            |> Seq.exists (fun project ->
                project.ParentFolderPath
                |> Option.exists (fun parent -> samePath comparison parent path))

        let item =
            workspace.RootProjection.Items
            |> Seq.exists (fun item ->
                item.FolderPath |> Option.exists (fun parent -> samePath comparison parent path))

        not childFolder && not childProject && not item

    let updateRules
        (project: SolutionProjectModel)
        solutionBuildType
        solutionPlatform
        projectBuildType
        projectPlatform
        builds
        deploys
        =
        let retained =
            project.ProjectConfigurationRules
            |> Option.ofObj
            |> Option.map (fun rules -> rules :> seq<ConfigurationRule>)
            |> Option.defaultValue Seq.empty
            |> Seq.filter (fun rule ->
                not (
                    rule.SolutionBuildType = solutionBuildType
                    && rule.SolutionPlatform = solutionPlatform
                ))
            |> Seq.toList

        let rules =
            [ ConfigurationRule(
                  BuildDimension.BuildType,
                  solutionBuildType,
                  solutionPlatform,
                  projectBuildType
              )
              ConfigurationRule(
                  BuildDimension.Platform,
                  solutionBuildType,
                  solutionPlatform,
                  projectPlatform
              )
              ConfigurationRule(
                  BuildDimension.Build,
                  solutionBuildType,
                  solutionPlatform,
                  string builds
              )
              ConfigurationRule(
                  BuildDimension.Deploy,
                  solutionBuildType,
                  solutionPlatform,
                  string deploys
              ) ]

        project.ProjectConfigurationRules <- List<ConfigurationRule>(retained @ rules)

    let removeRules (project: SolutionProjectModel) solutionBuildType solutionPlatform =
        let remaining =
            project.ProjectConfigurationRules
            |> Option.ofObj
            |> Option.map (fun rules -> rules :> seq<ConfigurationRule>)
            |> Option.defaultValue Seq.empty
            |> Seq.filter (fun rule ->
                rule.SolutionBuildType <> solutionBuildType
                || rule.SolutionPlatform <> solutionPlatform)
            |> Seq.toList

        let originalCount =
            project.ProjectConfigurationRules
            |> Option.ofObj
            |> Option.map _.Count
            |> Option.defaultValue 0

        if remaining.Length = originalCount then
            false
        else
            project.ProjectConfigurationRules <- List<ConfigurationRule> remaining
            true

[<AbstractClass; Sealed>]
type SolutionMutationCatalog private () =
    static member All = SolutionMutations.catalog

[<AbstractClass; Sealed>]
type SolutionPersistenceMutator private () =
    static member TryDescribe(commandId: CommandId) = SolutionMutations.descriptor commandId

    static member Discover(workspace: SolutionWorkspace, targetId: NodeId option) =
        if workspace.WorkspaceDescriptor.IsReadOnly then
            ImmutableArray<CommandDescriptor>.Empty
        else
            match targetId with
            | Some id ->
                workspace.RootProjection.Nodes
                |> Seq.tryFind (fun node -> node.NodeId = id)
                |> Option.map (fun node ->
                    SolutionMutations.catalog
                    |> Seq.filter (fun command ->
                        command.ApplicableTargetKinds.Contains node.NodeKind
                        && node.Supports command.RequiredCapability)
                    |> ImmutableArray.CreateRange)
                |> Option.defaultValue ImmutableArray<CommandDescriptor>.Empty
            | None ->
                SolutionMutations.catalog
                |> Seq.filter (fun command ->
                    command.ApplicableTargetKinds.Contains WorkspaceNodeKind.Workspace)
                |> ImmutableArray.CreateRange

    static member PlanAsync
        (
            workspace: SolutionWorkspace,
            command: CommandMutationRequest,
            cancellationToken: CancellationToken
        ) : Task<WorkspaceOutcome<SolutionMutationPlan>> =
        task {
            if workspace.WorkspaceDescriptor.IsReadOnly then
                return
                    Failure(
                        UnsupportedCapability(
                            WorkspaceCapabilityId.Write,
                            SolutionMutations.diagnostic
                                "unsupported_capability"
                                "The selected .slnf workspace is read-only."
                        )
                    )
            else
                match SolutionMutations.descriptor command.CommandId with
                | None ->
                    return
                        SolutionMutations.missing
                            command.CommandId.Value
                            "The command is not available."
                | Some descriptor ->
                    let targetNode =
                        match command.TargetId with
                        | None -> Some None
                        | Some id ->
                            workspace.RootProjection.Nodes
                            |> Seq.tryFind (fun node -> node.NodeId = id)
                            |> Option.map Some

                    match
                        SolutionMutations.validateArguments descriptor command.Arguments, targetNode
                    with
                    | Error message, _ -> return SolutionMutations.invalid "arguments" message
                    | _, None ->
                        return
                            SolutionMutations.missing "targetId" "The command target was not found."
                    | Ok(), Some None when
                        not (descriptor.ApplicableTargetKinds.Contains WorkspaceNodeKind.Workspace)
                        ->
                        return
                            SolutionMutations.missing
                                "targetId"
                                "The command target was not found or is not applicable."
                    | Ok(), Some(Some node) when
                        not (descriptor.ApplicableTargetKinds.Contains node.NodeKind)
                        ->
                        return
                            SolutionMutations.missing
                                "targetId"
                                "The command target was not found or is not applicable."
                    | Ok(), Some(Some node) when not (node.Supports descriptor.RequiredCapability) ->
                        return
                            Failure(
                                UnsupportedCapability(
                                    descriptor.RequiredCapability,
                                    SolutionMutations.diagnostic
                                        "unsupported_capability"
                                        "The command target does not support the required capability."
                                )
                            )
                    | Ok(), Some _ ->
                        try
                            let backingPath = workspace.BackingPath.Value

                            let solutionDirectory =
                                Path.GetDirectoryName backingPath
                                |> Option.ofObj
                                |> Option.defaultValue (Directory.GetCurrentDirectory())

                            let comparison = SolutionMutations.comparer workspace

                            match
                                SolutionSerializers.GetSerializerByMoniker backingPath
                                |> Option.ofObj
                            with
                            | None ->
                                return
                                    SolutionMutations.invalid
                                        "solution"
                                        "Expected a .sln or .slnx solution file."
                            | Some serializer ->
                                let! model = serializer.OpenAsync(backingPath, cancellationToken)
                                let mutable externalPaths = []
                                let mutable transactionPaths = []
                                let mutable additionalIntents = []
                                let mutable fileRename = None

                                let targetFolder () : Result<string option, string> =
                                    match command.TargetId with
                                    | None -> Ok None
                                    | Some target ->
                                        SolutionMutations.findFolder workspace target
                                        |> Option.map (fun folder -> Ok(Some folder.Path))
                                        |> Option.defaultValue (
                                            Error "The target folder was not found."
                                        )

                                let targetProject () : Result<SolutionProjectProjection, string> =
                                    match
                                        command.TargetId
                                        |> Option.bind (SolutionMutations.findProject workspace)
                                    with
                                    | Some project -> Ok project
                                    | None -> Error "The target project was not found."

                                let targetFolderModel () : Result<SolutionFolderModel, string> =
                                    targetFolder ()
                                    |> Result.bind (function
                                        | Some path ->
                                            SolutionMutations.modelFolder model path
                                            |> Option.map Ok
                                            |> Option.defaultValue (
                                                Error "The target folder was not found."
                                            )
                                        | None -> Error "A solution folder target is required.")

                                let targetProjectModel () : Result<SolutionProjectModel, string> =
                                    targetProject ()
                                    |> Result.bind (fun (projection: SolutionProjectProjection) ->
                                        SolutionMutations.modelProject
                                            model
                                            projection.Path.SolutionRelativePath
                                        |> Option.map Ok
                                        |> Option.defaultValue (
                                            Error "The target project was not found."
                                        ))

                                let apply =
                                    match command.CommandId.Value with
                                    | "solution.folder.add" ->
                                        targetFolder ()
                                        |> Result.bind (fun parent ->
                                            SolutionMutations.requiredText "name" command.Arguments
                                            |> Result.bind (SolutionMutations.folderPath parent)
                                            |> Result.bind (fun path ->
                                                if
                                                    model.SolutionFolders
                                                    |> Seq.exists (fun folder ->
                                                        SolutionMutations.samePath
                                                            comparison
                                                            folder.Path
                                                            path)
                                                then
                                                    Error
                                                        "A solution folder with that name already exists."
                                                else
                                                    model.AddFolder path |> ignore
                                                    Ok()))
                                    | "solution.folder.import-directory" ->
                                        SolutionMutations.requiredPath "path" command.Arguments
                                        |> Result.bind (fun path ->
                                            let relative, absolute =
                                                SolutionMutations.relativePath
                                                    solutionDirectory
                                                    path

                                            if
                                                SolutionMutations.external relative
                                                || relative = "."
                                                || not (Directory.Exists absolute)
                                            then
                                                Error
                                                    "The directory must exist inside the solution directory."
                                            else
                                                let segments =
                                                    relative.Split(
                                                        [| Path.DirectorySeparatorChar
                                                           Path.AltDirectorySeparatorChar |],
                                                        StringSplitOptions.RemoveEmptyEntries
                                                    )

                                                let folder = $"/{String.Join('/', segments)}/"

                                                if
                                                    model.SolutionFolders
                                                    |> Seq.exists (fun value ->
                                                        SolutionMutations.samePath
                                                            comparison
                                                            value.Path
                                                            folder)
                                                then
                                                    Error
                                                        "The imported solution folder already exists."
                                                else
                                                    model.AddFolder folder |> ignore
                                                    Ok())
                                    | "solution.folder.remove" ->
                                        match
                                            command.TargetId
                                            |> Option.bind (SolutionMutations.findFolder workspace)
                                        with
                                        | None -> Error "The target folder was not found."
                                        | Some folder ->
                                            SolutionMutations.optionalBoolean
                                                "recursive"
                                                false
                                                command.Arguments
                                            |> Result.bind (fun recursive ->
                                                if
                                                    not recursive
                                                    && not (
                                                        SolutionMutations.isEmptyFolder
                                                            workspace
                                                            comparison
                                                            folder.Path
                                                    )
                                                then
                                                    Error
                                                        "The solution folder is not empty; recursive must be true."
                                                else
                                                    if recursive then
                                                        additionalIntents <-
                                                            [ MutationIntent.RecursiveDelete ]

                                                    SolutionMutations.modelFolder model folder.Path
                                                    |> Option.map (fun value ->
                                                        model.RemoveFolder value |> ignore
                                                        Ok())
                                                    |> Option.defaultValue (
                                                        Error "The target folder was not found."
                                                    ))
                                    | "solution.item.add" ->
                                        targetFolderModel ()
                                        |> Result.bind (fun folder ->
                                            SolutionMutations.requiredPath "path" command.Arguments
                                            |> Result.bind (fun path ->
                                                let relative, absolute =
                                                    SolutionMutations.relativePath
                                                        solutionDirectory
                                                        path

                                                if SolutionMutations.external relative then
                                                    externalPaths <- [ absolute ]

                                                if
                                                    folder.Files
                                                    |> Seq.exists (fun item ->
                                                        SolutionMutations.samePath
                                                            comparison
                                                            item
                                                            relative)
                                                then
                                                    Error "The solution item already exists."
                                                else
                                                    folder.AddFile relative
                                                    Ok()))
                                    | "solution.item.remove" ->
                                        match
                                            command.TargetId
                                            |> Option.bind (SolutionMutations.findItem workspace)
                                        with
                                        | None -> Error "The target solution item was not found."
                                        | Some(item: SolutionItemProjection) ->
                                            item.FolderPath
                                            |> Option.bind (SolutionMutations.modelFolder model)
                                            |> Option.filter (fun folder ->
                                                folder.RemoveFile item.RelativePath)
                                            |> Option.map (fun _ -> Ok())
                                            |> Option.defaultValue (
                                                Error "The target solution item was not found."
                                            )
                                    | "solution.project.add" ->
                                        targetFolder ()
                                        |> Result.bind (fun parent ->
                                            SolutionMutations.requiredPath "path" command.Arguments
                                            |> Result.bind (fun path ->
                                                let relative, absolute =
                                                    SolutionMutations.relativePath
                                                        solutionDirectory
                                                        path

                                                if not (File.Exists absolute) then
                                                    Error "The project file was not found."
                                                elif
                                                    model.SolutionProjects
                                                    |> Seq.exists (fun project ->
                                                        SolutionMutations.samePath
                                                            comparison
                                                            project.FilePath
                                                            relative)
                                                then
                                                    Error
                                                        "The project already exists in the solution."
                                                else
                                                    if SolutionMutations.external relative then
                                                        externalPaths <- [ absolute ]

                                                    transactionPaths <- [ absolute ]

                                                    let folder =
                                                        parent
                                                        |> Option.bind (
                                                            SolutionMutations.modelFolder model
                                                        )
                                                        |> Option.toObj

                                                    model.AddProject(relative, null, folder)
                                                    |> ignore

                                                    Ok()))
                                    | "solution.project.remove" ->
                                        targetProjectModel ()
                                        |> Result.map (fun project ->
                                            model.RemoveProject project |> ignore)
                                    | "solution.project.rename" ->
                                        targetProject ()
                                        |> Result.bind (fun projection ->
                                            targetProjectModel ()
                                            |> Result.bind (fun project ->
                                                SolutionMutations.requiredText
                                                    "name"
                                                    command.Arguments
                                                |> Result.bind (fun name ->
                                                    if
                                                        name.IndexOfAny(
                                                            Path.GetInvalidFileNameChars()
                                                        )
                                                        >= 0
                                                        || name.IndexOfAny
                                                            [| Path.DirectorySeparatorChar
                                                               Path.AltDirectorySeparatorChar |]

                                                           >= 0
                                                        || name = "."
                                                        || name = ".."
                                                    then
                                                        Error
                                                            "The project name must be a single non-empty filename stem."
                                                    else
                                                        let source =
                                                            projection.Path.AbsolutePath.Value

                                                        let extension = Path.GetExtension source

                                                        let destination =
                                                            Path.Combine(
                                                                Path.GetDirectoryName source
                                                                |> Option.ofObj
                                                                |> Option.defaultValue
                                                                    solutionDirectory,
                                                                $"{name}{extension}"
                                                            )

                                                        let caseOnly =
                                                            not (
                                                                String.Equals(
                                                                    source,
                                                                    destination,
                                                                    StringComparison.Ordinal
                                                                )
                                                            )
                                                            && String.Equals(
                                                                source,
                                                                destination,
                                                                StringComparison.OrdinalIgnoreCase
                                                            )
                                                            && HostFileSystemCaseDetector.DetectFromExistingPath
                                                                source = HostFileSystemCaseSemantics.Insensitive

                                                        if not (File.Exists source) then
                                                            Error
                                                                "The project file to rename was not found."
                                                        elif
                                                            String.Equals(
                                                                source,
                                                                destination,
                                                                StringComparison.Ordinal
                                                            )
                                                        then
                                                            Error
                                                                "The project already has that name."
                                                        elif
                                                            SolutionMutations.artifactExists
                                                                destination
                                                            && not caseOnly
                                                        then
                                                            Error
                                                                "The project rename destination already exists."
                                                        else
                                                            let relative =
                                                                Path.GetRelativePath(
                                                                    solutionDirectory,
                                                                    destination
                                                                )

                                                            project.FilePath <- relative

                                                            transactionPaths <-
                                                                [ source; destination ]

                                                            if
                                                                SolutionMutations.external
                                                                    relative
                                                            then
                                                                externalPaths <-
                                                                    [ source; destination ]

                                                            fileRename <-
                                                                Some
                                                                    { Source =
                                                                        WorkspaceArtifactPath.Create
                                                                            source
                                                                      Destination =
                                                                        WorkspaceArtifactPath.Create
                                                                            destination }

                                                            Ok())))
                                    | "solution.project.move" ->
                                        targetProjectModel ()
                                        |> Result.bind (fun project ->
                                            SolutionMutations.optionalNode
                                                "folder"
                                                command.Arguments
                                            |> Result.bind (function
                                                | None ->
                                                    project.MoveToFolder null
                                                    Ok()
                                                | Some id ->
                                                    SolutionMutations.findFolder workspace id
                                                    |> Option.bind (fun folder ->
                                                        SolutionMutations.modelFolder
                                                            model
                                                            folder.Path)
                                                    |> Option.map (fun folder ->
                                                        project.MoveToFolder folder
                                                        Ok())
                                                    |> Option.defaultValue (
                                                        Error
                                                            "The destination folder was not found."
                                                    )))
                                    | "solution.project.update-path" ->
                                        targetProjectModel ()
                                        |> Result.bind (fun project ->
                                            SolutionMutations.requiredPath "path" command.Arguments
                                            |> Result.bind (fun path ->
                                                let relative, absolute =
                                                    SolutionMutations.relativePath
                                                        solutionDirectory
                                                        path

                                                if not (File.Exists absolute) then
                                                    Error "The project file was not found."
                                                elif
                                                    model.SolutionProjects
                                                    |> Seq.exists (fun value ->
                                                        not (
                                                            Object.ReferenceEquals(
                                                                value,
                                                                project
                                                            )
                                                        )
                                                        && SolutionMutations.samePath
                                                            comparison
                                                            value.FilePath
                                                            relative)
                                                then
                                                    Error
                                                        "The project path already exists in the solution."
                                                else
                                                    if SolutionMutations.external relative then
                                                        externalPaths <- [ absolute ]

                                                    transactionPaths <- [ absolute ]
                                                    project.FilePath <- relative
                                                    Ok()))
                                    | "solution.build-type.add" ->
                                        SolutionMutations.requiredText "name" command.Arguments
                                        |> Result.bind (fun name ->
                                            if
                                                model.BuildTypes
                                                |> Seq.exists (fun value ->
                                                    SolutionMutations.samePath
                                                        comparison
                                                        value
                                                        name)
                                            then
                                                Error "The build type already exists."
                                            else
                                                model.AddBuildType name
                                                Ok())
                                    | "solution.build-type.remove" ->
                                        match
                                            command.TargetId
                                            |> Option.bind (
                                                SolutionMutations.findConfiguration workspace
                                            )
                                        with
                                        | Some node when model.RemoveBuildType node.Name -> Ok()
                                        | _ -> Error "The build type was not found."
                                    | "solution.platform.add" ->
                                        SolutionMutations.requiredText "name" command.Arguments
                                        |> Result.bind (fun name ->
                                            if
                                                model.Platforms
                                                |> Seq.exists (fun value ->
                                                    SolutionMutations.samePath
                                                        comparison
                                                        value
                                                        name)
                                            then
                                                Error "The platform already exists."
                                            else
                                                model.AddPlatform name
                                                Ok())
                                    | "solution.platform.remove" ->
                                        match
                                            command.TargetId
                                            |> Option.bind (
                                                SolutionMutations.findPlatform workspace
                                            )
                                        with
                                        | Some node when model.RemovePlatform node.Name -> Ok()
                                        | _ -> Error "The platform was not found."
                                    | "solution.project-configuration.set" ->
                                        targetProjectModel ()
                                        |> Result.bind (fun project ->
                                            match
                                                SolutionMutations.requiredText
                                                    "solutionBuildType"
                                                    command.Arguments,
                                                SolutionMutations.requiredText
                                                    "solutionPlatform"
                                                    command.Arguments,
                                                SolutionMutations.requiredText
                                                    "projectBuildType"
                                                    command.Arguments,
                                                SolutionMutations.requiredText
                                                    "projectPlatform"
                                                    command.Arguments,
                                                SolutionMutations.requiredBoolean
                                                    "builds"
                                                    command.Arguments,
                                                SolutionMutations.requiredBoolean
                                                    "deploys"
                                                    command.Arguments
                                            with
                                            | Ok buildType,
                                              Ok platform,
                                              Ok projectBuildType,
                                              Ok projectPlatform,
                                              Ok builds,
                                              Ok deploys when
                                                model.BuildTypes
                                                |> Seq.exists (fun value ->
                                                    SolutionMutations.samePath
                                                        comparison
                                                        value
                                                        buildType)
                                                && model.Platforms
                                                   |> Seq.exists (fun value ->
                                                       SolutionMutations.samePath
                                                           comparison
                                                           value
                                                           platform)
                                                ->
                                                SolutionMutations.updateRules
                                                    project
                                                    buildType
                                                    platform
                                                    projectBuildType
                                                    projectPlatform
                                                    builds
                                                    deploys

                                                Ok()
                                            | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _ ->
                                                Error "The solution configuration does not exist."
                                            | Error error, _, _, _, _, _
                                            | _, Error error, _, _, _, _
                                            | _, _, Error error, _, _, _
                                            | _, _, _, Error error, _, _
                                            | _, _, _, _, Error error, _
                                            | _, _, _, _, _, Error error -> Error error)
                                    | "solution.project-configuration.remove" ->
                                        targetProjectModel ()
                                        |> Result.bind (fun project ->
                                            match
                                                SolutionMutations.requiredText
                                                    "solutionBuildType"
                                                    command.Arguments,
                                                SolutionMutations.requiredText
                                                    "solutionPlatform"
                                                    command.Arguments
                                            with
                                            | Ok buildType, Ok platform when
                                                model.BuildTypes
                                                |> Seq.exists (fun value ->
                                                    SolutionMutations.samePath
                                                        comparison
                                                        value
                                                        buildType)
                                                && model.Platforms
                                                   |> Seq.exists (fun value ->
                                                       SolutionMutations.samePath
                                                           comparison
                                                           value
                                                           platform)
                                                ->
                                                if
                                                    SolutionMutations.removeRules
                                                        project
                                                        buildType
                                                        platform
                                                then
                                                    Ok()
                                                else
                                                    Error
                                                        "The project configuration was not found."
                                            | Ok _, Ok _ ->
                                                Error "The solution configuration does not exist."
                                            | Error error, _
                                            | _, Error error -> Error error)
                                    | "solution.dependency.add" ->
                                        targetProjectModel ()
                                        |> Result.bind (fun project ->
                                            SolutionMutations.requiredNode
                                                "dependency"
                                                command.Arguments
                                            |> Result.bind (fun dependencyId ->
                                                SolutionMutations.findProject
                                                    workspace
                                                    dependencyId
                                                |> Option.bind (fun dependency ->
                                                    SolutionMutations.modelProject
                                                        model
                                                        dependency.Path.SolutionRelativePath)
                                                |> Option.map (fun dependency ->
                                                    if
                                                        Object.ReferenceEquals(
                                                            project,
                                                            dependency
                                                        )
                                                        || project.Dependencies
                                                           |> Option.ofObj
                                                           |> Option.map (fun values ->
                                                               values
                                                               :> seq<SolutionProjectModel>)
                                                           |> Option.defaultValue Seq.empty
                                                           |> Seq.exists (fun value ->
                                                               Object.ReferenceEquals(
                                                                   value,
                                                                   dependency
                                                               ))
                                                    then
                                                        Error
                                                            "The solution dependency already exists."
                                                    else
                                                        project.AddDependency dependency
                                                        Ok())
                                                |> Option.defaultValue (
                                                    Error "The dependency project was not found."
                                                )))
                                    | "solution.dependency.remove" ->
                                        targetProjectModel ()
                                        |> Result.bind (fun project ->
                                            SolutionMutations.requiredNode
                                                "dependency"
                                                command.Arguments
                                            |> Result.bind (fun dependencyId ->
                                                SolutionMutations.findProject
                                                    workspace
                                                    dependencyId
                                                |> Option.bind (fun dependency ->
                                                    SolutionMutations.modelProject
                                                        model
                                                        dependency.Path.SolutionRelativePath)
                                                |> Option.map (fun dependency ->
                                                    if project.RemoveDependency dependency then
                                                        Ok()
                                                    else
                                                        Error
                                                            "The solution dependency was not found.")
                                                |> Option.defaultValue (
                                                    Error "The dependency project was not found."
                                                )))
                                    | _ -> Error "The command is not available."

                                match apply with
                                | Error message ->
                                    return SolutionMutations.invalid "command" message
                                | Ok() ->
                                    let! contents =
                                        SolutionMutations.saveToMemory
                                            backingPath
                                            serializer
                                            model
                                            cancellationToken

                                    return
                                        Success
                                            { Request =
                                                SolutionMutations.mutationRequest
                                                    workspace
                                                    command
                                                    command.Arguments
                                                    externalPaths
                                                    transactionPaths
                                                    additionalIntents
                                              Contents = contents
                                              BackingPath = workspace.BackingPath
                                              FileRename = fileRename }
                        with
                        | :? OperationCanceledException ->
                            return
                                Failure(
                                    Cancelled(
                                        OperationId.New(),
                                        SolutionMutations.diagnostic
                                            "cancelled"
                                            "The solution operation was cancelled."
                                    )
                                )
                        | :? SolutionException ->
                            return
                                SolutionMutations.invalid
                                    "solution"
                                    "The solution file is malformed."
                        | :? IOException ->
                            return
                                SolutionMutations.internalFailure
                                    "The solution could not be read or serialized."
                        | :? UnauthorizedAccessException ->
                            return
                                SolutionMutations.internalFailure
                                    "The solution could not be read or serialized."
                        | :? ArgumentException as error ->
                            return SolutionMutations.invalid "command" error.Message
        }
