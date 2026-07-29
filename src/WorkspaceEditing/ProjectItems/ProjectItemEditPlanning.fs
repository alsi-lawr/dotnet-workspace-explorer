namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"

open System
open System.IO
open System.Text
open System.Threading
open System.Xml.Linq
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Solutions

module internal ProjectItemEditPlanning =
    open ProjectEditPlanning
    open ProjectItemInclusion
    open MsBuildProjectDocument

    let plan
        (workspace: SolutionWorkspace)
        (project: SolutionProject)
        (snapshot: ProjectEvaluationSnapshot)
        (command: CommandMutationRequest)
        (_: CancellationToken)
        =
        if snapshot.CapabilityProfile <> WorkspaceCapabilityProfile.Full then
            unsupported "The evaluated project system does not grant project write capability."
        elif command.TargetWorkspaceNodeId <> Some project.Node.Id then
            missing "targetNodeId" "The command target was not found."
        else
            match ProjectItemCommands.tryDescribe command.CommandId with
            | None -> missing "commandId" "The command is not available."
            | Some _ ->
                try
                    let projectPath = project.Path.AbsolutePath.Value
                    let directory = projectDirectory project

                    let document, encoding, preamble, lineEnding = readDocument projectPath

                    let itemPath name =
                        requiredPath name command.Arguments
                        |> Result.map (fun path -> Path.GetFullPath(path.Value, directory))

                    let itemType () =
                        requiredChoice "itemType" command.Arguments
                        |> Result.bind (fun value ->
                            if itemTypes.Contains value then
                                Ok value
                            else
                                Error "The item type is not supported.")

                    let updatedDocument () =
                        replaceProject projectPath document encoding preamble lineEnding

                    let item path = itemPath path |> unwrap
                    let kind () = itemType () |> unwrap

                    let directoryOperand path =
                        Directory.Exists path || Path.EndsInDirectorySeparator path

                    let actions, paths, intents =
                        match command.CommandId.Value with
                        | "project.item.add" ->
                            let source, kind, link =
                                item "path",
                                kind (),
                                optionalBoolean "link" command.Arguments |> unwrap

                            if generated directory source then
                                raise (ArgumentException "Generated items are read-only.")
                            elif not (File.Exists source || Directory.Exists source) then
                                raise (ArgumentException "The item path does not exist.")
                            elif Directory.Exists source then
                                if link || external directory source then
                                    raise (ArgumentException "Linked items must be external files.")

                                let includePath =
                                    relativePath directory (WorkspaceArtifactPath.Create source)

                                let includeValue = $"{includePath.TrimEnd '/'}/**/*"

                                let files =
                                    Directory.EnumerateFiles(
                                        source,
                                        "*",
                                        SearchOption.AllDirectories
                                    )
                                    |> Seq.toArray

                                let allIncluded =
                                    files.Length > 0
                                    && files |> Array.forall (evaluatedAs snapshot kind)

                                if allIncluded || containsItemGlob document kind includeValue then
                                    [], [], []
                                else
                                    itemTypes
                                    |> Set.remove kind
                                    |> Seq.iter (fun itemType ->
                                        appendRemove document itemType includeValue)

                                    appendItem document kind includeValue []
                                    [ updatedDocument () ], [ projectPath ], []
                            elif external directory source && not link then
                                let destination = Path.Combine(directory, Path.GetFileName source)

                                if File.Exists destination || Directory.Exists destination then
                                    raise (ArgumentException "The destination item already exists.")

                                let declared =
                                    appendRequestedItem
                                        document
                                        snapshot
                                        kind
                                        destination
                                        (relativePath
                                            directory
                                            (WorkspaceArtifactPath.Create destination))

                                (if not declared then
                                     [ WorkspaceEditAction.ReplaceFile(
                                           destination,
                                           File.ReadAllBytes source
                                       ) ]
                                 else
                                     [ WorkspaceEditAction.ReplaceFile(
                                           destination,
                                           File.ReadAllBytes source
                                       )
                                       updatedDocument () ]),
                                (if not declared then
                                     [ destination; source ]
                                 else
                                     [ destination; projectPath; source ]),
                                []
                            else
                                if link && not (external directory source) then
                                    raise (ArgumentException "Linked items must be external files.")

                                if not (evaluatedAs snapshot kind source) || link then
                                    let includeValue =
                                        relativePath directory (WorkspaceArtifactPath.Create source)

                                    if link then
                                        appendItem
                                            document
                                            kind
                                            includeValue
                                            [ "Link", Path.GetFileName source ]
                                    else
                                        appendRequestedItem
                                            document
                                            snapshot
                                            kind
                                            source
                                            includeValue
                                        |> ignore

                                if evaluatedAs snapshot kind source && not link then
                                    [], [], []
                                else
                                    [ updatedDocument () ], [ projectPath; source ], []
                        | "project.item.new" ->
                            let destination, kind = item "path", kind ()

                            if directoryOperand destination then
                                raise (ArgumentException "New project items must be files.")
                            elif generated directory destination then
                                raise (ArgumentException "Generated items are read-only.")
                            elif File.Exists destination || Directory.Exists destination then
                                raise (ArgumentException "The destination item already exists.")


                            let declared =
                                appendRequestedItem
                                    document
                                    snapshot
                                    kind
                                    destination
                                    (relativePath
                                        directory
                                        (WorkspaceArtifactPath.Create destination))

                            let file =
                                WorkspaceEditAction.ReplaceFile(
                                    destination,
                                    Encoding.UTF8.GetBytes(
                                        optionalText "contents" command.Arguments
                                        |> unwrap
                                        |> Option.defaultValue String.Empty
                                    )
                                )

                            (if not declared then
                                 [ file ]
                             else
                                 [ file; updatedDocument () ]),
                            (if not declared then
                                 [ destination ]
                             else
                                 [ destination; projectPath ]),
                            []
                        | "project.item.copy" ->
                            let source, destination, kind = item "source", item "path", kind ()

                            if directoryOperand source || directoryOperand destination then
                                raise (ArgumentException "Copied project items must be files.")
                            elif generated directory source || generated directory destination then
                                raise (ArgumentException "Generated items are read-only.")
                            elif not (File.Exists source) then
                                raise (ArgumentException "The source item does not exist.")

                            if File.Exists destination || Directory.Exists destination then
                                raise (ArgumentException "The destination item already exists.")

                            let declared =
                                appendRequestedItem
                                    document
                                    snapshot
                                    kind
                                    destination
                                    (relativePath
                                        directory
                                        (WorkspaceArtifactPath.Create destination))

                            let copiedFile =
                                WorkspaceEditAction.ReplaceFile(
                                    destination,
                                    File.ReadAllBytes source
                                )

                            (if not declared then
                                 [ copiedFile ]
                             else
                                 [ copiedFile; updatedDocument () ]),
                            (if not declared then
                                 [ destination; source ]
                             else
                                 [ destination; projectPath; source ]),
                            []
                        | "project.item.rename"
                        | "project.item.move" ->
                            let source = item "path"

                            let destination =
                                if command.CommandId.Value = "project.item.rename" then
                                    let rename = requiredText "name" command.Arguments |> unwrap

                                    if rename.IndexOfAny [| '/'; '\\' |] >= 0 then
                                        raise (
                                            ArgumentException "The name must be one path segment."
                                        )

                                    Path.Combine(
                                        Path.GetDirectoryName source
                                        |> Option.ofObj
                                        |> Option.defaultValue directory,
                                        rename
                                    )
                                else
                                    item "destination"

                            if directoryOperand source || directoryOperand destination then
                                raise (ArgumentException "Rename and move require file operands.")
                            elif generated directory source || generated directory destination then
                                raise (ArgumentException "Generated items are read-only.")
                            elif
                                not (File.Exists source)
                                || File.Exists destination
                                || Directory.Exists destination
                            then
                                raise (
                                    ArgumentException "The item source or destination is invalid."
                                )
                            elif
                                not (
                                    String.Equals(
                                        Path.GetExtension source,
                                        Path.GetExtension destination,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                            then
                                raise (
                                    ArgumentException
                                        "Rename and move require the same file extension."
                                )
                            elif external directory source <> external directory destination then
                                raise (
                                    ArgumentException
                                        "Rename and move cannot cross the project boundary."
                                )

                            let sourceInclude =
                                relativePath directory (WorkspaceArtifactPath.Create source)

                            let destinationInclude =
                                relativePath directory (WorkspaceArtifactPath.Create destination)

                            match
                                document.Descendants()
                                |> Seq.tryFind (fun element ->
                                    attribute "Include" element = Some sourceInclude
                                    || attribute "Update" element = Some sourceInclude)
                            with
                            | Some element ->
                                let update =
                                    element.Attribute(name "Include")
                                    |> Option.ofObj
                                    |> Option.defaultWith (fun () ->
                                        element.Attribute(name "Update"))

                                update.Value <- destinationInclude

                                [ WorkspaceEditAction.Move(source, destination)
                                  updatedDocument () ],
                                [ source; destination; projectPath ],
                                []
                            | None ->
                                [ WorkspaceEditAction.Move(source, destination) ],
                                [ source; destination ],
                                []
                        | "project.item.remove"
                        | "project.item.delete" ->
                            let path = item "path"

                            if directoryOperand path then
                                raise (ArgumentException "Remove and delete require file operands.")
                            elif generated directory path then
                                raise (ArgumentException "Generated items are read-only.")

                            let includeValue =
                                relativePath directory (WorkspaceArtifactPath.Create path)

                            match
                                document.Descendants()
                                |> Seq.tryFind (fun element ->
                                    attribute "Include" element = Some includeValue
                                    || attribute "Update" element = Some includeValue)
                            with
                            | Some element ->
                                let itemType = element.Name.LocalName
                                removeItem element

                                defaultItemType snapshot path
                                |> Option.defaultValue itemType
                                |> fun defaultType -> appendRemove document defaultType includeValue
                            | None ->
                                defaultItemType snapshot path
                                |> Option.defaultWith (fun () ->
                                    effectiveItemType snapshot includeValue path)
                                |> fun defaultType -> appendRemove document defaultType includeValue

                            if command.CommandId.Value = "project.item.delete" then
                                if not (File.Exists path) then
                                    raise (ArgumentException "The item path does not exist.")

                                [ updatedDocument (); WorkspaceEditAction.Trash path ],
                                [ projectPath; path ],
                                []
                            else
                                [ updatedDocument () ], [ projectPath ], []
                        | "project.item.set-build-action" ->
                            let path, kind = item "path", kind ()

                            if generated directory path then
                                raise (ArgumentException "Generated items are read-only.")

                            let includeValue =
                                relativePath directory (WorkspaceArtifactPath.Create path)

                            let existing =
                                document.Descendants()
                                |> Seq.tryFind (fun element ->
                                    attribute "Include" element = Some includeValue
                                    || attribute "Update" element = Some includeValue)

                            let sourceType =
                                existing
                                |> Option.map (fun element -> element.Name.LocalName)
                                |> Option.defaultWith (fun () ->
                                    effectiveItemType snapshot includeValue path)

                            if sourceType <> kind then
                                defaultItemType snapshot path
                                |> Option.filter ((<>) kind)
                                |> Option.iter (fun defaultType ->
                                    appendRemove document defaultType includeValue)

                                match existing with
                                | Some element ->
                                    element.Name <- name kind

                                    match element.Attribute(name "Update") |> Option.ofObj with
                                    | Some update ->
                                        update.Remove()
                                        element.SetAttributeValue(name "Include", includeValue)
                                    | None -> ()
                                | None -> appendItem document kind includeValue []

                            [ updatedDocument () ], [ projectPath ], []
                        | "project.item.set-metadata" ->
                            let path, metadataName, metadataValue =
                                item "path",
                                requiredChoice "name" command.Arguments |> unwrap,
                                requiredText "value" command.Arguments |> unwrap

                            if
                                not (metadataNames.Contains metadataName)
                                || generated directory path
                            then
                                raise (
                                    ArgumentException "The item metadata request is not writable."
                                )

                            let includeValue =
                                relativePath directory (WorkspaceArtifactPath.Create path)

                            let element =
                                document.Descendants()
                                |> Seq.tryFind (fun element ->
                                    attribute "Include" element = Some includeValue
                                    || attribute "Update" element = Some includeValue)
                                |> Option.defaultWith (fun () ->
                                    let itemType = effectiveItemType snapshot includeValue path

                                    if defaultItemPolicy snapshot itemType path then
                                        appendUpdate document itemType includeValue []

                                        document.Descendants(name itemType)
                                        |> Seq.filter (fun item ->
                                            attribute "Update" item = Some includeValue)
                                        |> Seq.last
                                    else
                                        appendItem document itemType includeValue []

                                        document.Descendants(name itemType)
                                        |> Seq.filter (fun item ->
                                            attribute "Include" item = Some includeValue)
                                        |> Seq.last)

                            match element.Element(name metadataName) with
                            | null -> element.Add(XElement(name metadataName, metadataValue))
                            | metadata -> metadata.Value <- metadataValue

                            [ updatedDocument () ], [ projectPath ], []
                        | _ -> raise (ArgumentException "The command is not available.")

                    makePlan workspace command actions paths intents
                with
                | :? ArgumentException as error -> invalid "command" error.Message
                | :? IOException -> invalid "project" "The project file could not be read."
                | :? UnauthorizedAccessException ->
                    invalid "project" "The project file could not be read."
                | :? Xml.XmlException -> invalid "project" "The project XML is malformed."
