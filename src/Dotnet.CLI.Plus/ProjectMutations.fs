namespace Dotnet.CLI.Plus

#nowarn "3261"

open System
open System.Collections.Immutable
open System.Globalization
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution
open System.Xml
open System.Xml.Linq

type internal ProjectMutationPlan =
    { Request: MutationPreviewRequest
      Actions: MutationAction array
      Paths: WorkspaceArtifactPath array }

module internal ProjectMutations =
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
        Failure(UnsupportedCapability(WorkspaceCapabilityId.Write, diagnostic "unsupported_capability" message))

    let private missing name message =
        Failure(NotFound(name, diagnostic "not_found" message))

    let private parameter id parameterType required name =
        CommandParameterDescriptor.Create(CommandParameterId.Create id, parameterType, required, name)

    let private command id name parameters =
        CommandDescriptor.Create(
            CommandId.Create id,
            name,
            CommandAccess.Write,
            parameters,
            [ WorkspaceNodeKind.Project ]
        )

    let all =
        ImmutableArray.CreateRange(
            [ command
                  "project.item.add"
                  "Add project item"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "itemType" CommandParameterType.Choice true "Item type"
                    parameter "link" CommandParameterType.Boolean false "Link external item" ]
              command
                  "project.item.new"
                  "Create project item"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "itemType" CommandParameterType.Choice true "Item type"
                    parameter "contents" CommandParameterType.Text false "Contents" ]
              command
                  "project.item.copy"
                  "Copy project item"
                  [ parameter "source" CommandParameterType.Path true "Source"
                    parameter "path" CommandParameterType.Path true "Destination"
                    parameter "itemType" CommandParameterType.Choice true "Item type" ]
              command
                  "project.item.rename"
                  "Rename project item"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "name" CommandParameterType.Text true "Name" ]
              command
                  "project.item.move"
                  "Move project item"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "destination" CommandParameterType.Path true "Destination" ]
              command
                  "project.item.remove"
                  "Remove project item"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
              command
                  "project.item.delete"
                  "Delete project item"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
              command
                  "project.item.set-build-action"
                  "Set project item build action"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "itemType" CommandParameterType.Choice true "Item type" ]
              command
                  "project.item.set-metadata"
                  "Set project item metadata"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "name" CommandParameterType.Choice true "Metadata name"
                    parameter "value" CommandParameterType.Text true "Value" ]
              command
                  "project.property.set"
                  "Set project property"
                  [ parameter "name" CommandParameterType.Choice true "Property name"
                    parameter "value" CommandParameterType.Text true "Value"
                    parameter "scope" CommandParameterType.Path false "Writable project or import file"
                    parameter
                        "condition"
                        CommandParameterType.Text
                        false
                        "Property group condition (empty for unconditional)" ] ]
        )

    let tryDescribe id =
        all |> Seq.tryFind (fun descriptor -> descriptor.CommandId = id)

    let discover (workspace: SolutionWorkspace) targetId =
        if workspace.WorkspaceDescriptor.IsReadOnly then
            ImmutableArray<CommandDescriptor>.Empty
        else
            targetId
            |> Option.bind (fun id ->
                workspace.RootProjection.Projects
                |> Seq.tryFind (fun project -> project.Node.NodeId = id)
                |> Option.map (fun _ -> all))
            |> Option.defaultValue ImmutableArray<CommandDescriptor>.Empty

    let private value (name: string) (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = name then
                Some argument.Value
            else
                None)

    let private requiredPath name arguments =
        match value name arguments with
        | Some(Path path) -> Ok path
        | _ -> Error $"'{name}' is required."

    let private requiredText name arguments =
        match value name arguments with
        | Some(Text text) when not (String.IsNullOrWhiteSpace text) -> Ok text
        | _ -> Error $"'{name}' is required."

    let private optionalText name arguments =
        match value name arguments with
        | None -> Ok None
        | Some(Text text) -> Ok(Some text)
        | _ -> Error $"'{name}' must be text."

    let private requiredChoice name arguments =
        match value name arguments with
        | Some(Choice choice) -> Ok choice.Value
        | _ -> Error $"'{name}' is required."

    let private optionalBoolean name arguments =
        match value name arguments with
        | None -> Ok false
        | Some(Boolean choice) -> Ok choice
        | _ -> Error $"'{name}' must be a boolean."

    let private itemTypes =
        Set.ofList [ "Compile"; "Content"; "None"; "EmbeddedResource" ]

    let private metadataNames =
        Set.ofList
            [ "Link"
              "DependentUpon"
              "Visible"
              "CopyToOutputDirectory"
              "CopyToPublishDirectory"
              "Generator"
              "LastGenOutput"
              "CustomToolNamespace" ]

    let private validateProperty projectDirectory name value =
        if
            not (ProjectPropertyRegistry.Names.Contains name)
            || String.IsNullOrWhiteSpace value
        then
            Error "The property name or value is not in the curated registry."
        elif
            Set.contains
                name
                (Set.ofList
                    [ "TreatWarningsAsErrors"
                      "IsPackable"
                      "SignAssembly"
                      "SelfContained"
                      "PublishSingleFile"
                      "PublishTrimmed"
                      "PublishAot" ])
            && not (
                String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            )
        then
            Error $"'{name}' requires a boolean value."
        elif
            name = "OutputType"
            && not (Set.contains value (Set.ofList [ "Exe"; "Library"; "WinExe"; "Module" ]))
        then
            Error "OutputType must be Exe, Library, WinExe, or Module."
        elif name = "AssemblyOriginatorKeyFile" then
            let keyPath = Path.GetFullPath(value, projectDirectory)
            let relative = Path.GetRelativePath(projectDirectory, keyPath)

            let outside =
                Path.IsPathRooted relative
                || relative = ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}")
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}")

            if Path.IsPathRooted value || outside then
                Error "AssemblyOriginatorKeyFile must stay within the project directory."
            else
                Ok()
        else
            Ok()

    let private projectDirectory (project: SolutionProjectProjection) =
        Path.GetDirectoryName project.Path.AbsolutePath.Value
        |> Option.ofObj
        |> Option.defaultValue (Directory.GetCurrentDirectory())

    let private relativePath directory (path: WorkspaceArtifactPath) =
        Path
            .GetRelativePath(directory, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')

    let private external directory path =
        let relative = Path.GetRelativePath(directory, path)

        Path.IsPathRooted relative
        || relative = ".."
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}")
        || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}")

    let private readDocument path =
        let bytes = File.ReadAllBytes path
        use stream = new MemoryStream(bytes)
        let settings = XmlReaderSettings()
        settings.IgnoreWhitespace <- false
        use reader = XmlReader.Create(stream, settings)
        let document = XDocument.Load(reader, LoadOptions.PreserveWhitespace)

        let declarationEncoding =
            document.Declaration
            |> Option.ofObj
            |> Option.bind (fun declaration ->
                try
                    declaration.Encoding |> Option.ofObj |> Option.map Encoding.GetEncoding
                with :? ArgumentException ->
                    None)

        let bomCandidates: (byte array * Encoding) list =
            [ UTF32Encoding(false, true).GetPreamble(), (UTF32Encoding(false, true) :> Encoding)
              UTF32Encoding(true, true).GetPreamble(), (UTF32Encoding(true, true) :> Encoding)
              Encoding.Unicode.GetPreamble(), Encoding.Unicode
              Encoding.BigEndianUnicode.GetPreamble(), Encoding.BigEndianUnicode
              Encoding.UTF8.GetPreamble(), Encoding.UTF8 ]

        let bomEncoding =
            bomCandidates
            |> Seq.tryPick (fun (preamble, candidate) ->
                if
                    preamble.Length > 0
                    && bytes.Length >= preamble.Length
                    && bytes[.. preamble.Length - 1] = preamble
                then
                    Some candidate
                else
                    None)

        let encoding =
            bomEncoding
            |> Option.orElse declarationEncoding
            |> Option.defaultValue Encoding.UTF8

        let hasPreamble = bomEncoding.IsSome

        let text = encoding.GetString bytes

        let lineEnding =
            if text.Contains("\r\n", StringComparison.Ordinal) then
                "\r\n"
            elif text.Contains('\n') then
                "\n"
            else
                Environment.NewLine

        document, encoding, hasPreamble, lineEnding

    type private EncodingWriter(encoding: Encoding) =
        inherit StringWriter(CultureInfo.InvariantCulture)
        override _.Encoding = encoding

    let private saveDocument (document: XDocument) (encoding: Encoding) hasPreamble lineEnding =
        use writer = new EncodingWriter(encoding)
        let settings = XmlWriterSettings()
        settings.Encoding <- encoding
        settings.Indent <- false
        settings.NewLineHandling <- NewLineHandling.None
        settings.OmitXmlDeclaration <- isNull document.Declaration

        use xml = XmlWriter.Create(writer, settings)
        document.Save(xml)
        xml.Flush()

        let text =
            if lineEnding = "\r\n" then
                writer.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n")
            else
                writer.ToString()

        let contents = encoding.GetBytes text

        if hasPreamble then
            Array.append (encoding.GetPreamble()) contents
        else
            contents

    let private name local = XName.Get local

    let private attribute local (element: XElement) =
        element.Attribute(name local) |> Option.ofObj |> Option.map _.Value

    let private itemGroup (document: XDocument) =
        document.Root.Elements(name "ItemGroup") |> Seq.tryHead

    let private newline (document: XDocument) =
        document.DescendantNodes()
        |> Seq.choose (function
            | :? XText as value when value.Value.Contains("\r\n", StringComparison.Ordinal) -> Some "\r\n"
            | :? XText as value when value.Value.Contains('\n') -> Some "\n"
            | _ -> None)
        |> Seq.tryHead
        |> Option.defaultValue Environment.NewLine

    let private appendItemWith
        (document: XDocument)
        (itemType: string)
        attributeName
        (includeValue: string)
        (metadata: (string * string) list)
        =
        let group =
            itemGroup document
            |> Option.defaultWith (fun () ->
                let group = XElement(name "ItemGroup")
                document.Root.Add(XText($"{newline document}  "), group, XText(newline document))
                group)

        let item = XElement(name itemType, XAttribute(name attributeName, includeValue))

        for metadataName, metadataValue in metadata do
            item.Add(XElement(name metadataName, metadataValue))

        group.Add(XText($"{newline document}    "), item, XText($"{newline document}  "))

    let private appendItem document itemType includeValue metadata =
        appendItemWith document itemType "Include" includeValue metadata

    let private appendUpdate document itemType includeValue metadata =
        appendItemWith document itemType "Update" includeValue metadata

    let private appendRemove (document: XDocument) (itemType: string) (includeValue: string) =
        let exists =
            document.Descendants(name itemType)
            |> Seq.exists (fun item -> attribute "Remove" item = Some includeValue)

        if not exists then
            appendItemWith document itemType "Remove" includeValue []

    let private removeItem (item: XElement) = item.Remove()

    let private evaluatedAs (snapshot: EvaluationSnapshot) (itemType: string) (path: string) =
        snapshot.Dimensions
        |> Seq.collect _.Items
        |> Seq.exists (fun item ->
            item.ItemType = itemType
            && not (isNull item.ResolvedPath)
            && item.ResolvedPath.Value = path)

    let private globMatches (pattern: string) (value: string) =
        let normalize (path: string) =
            path.Trim().Replace('\\', '/')
            |> fun value -> Regex.Replace(value, "/+", "/")
            |> fun value ->
                if value.StartsWith("./", StringComparison.Ordinal) then
                    value[2..]
                else
                    value

        let normalized = normalize pattern

        if
            String.IsNullOrWhiteSpace normalized
            || normalized.Contains("$(", StringComparison.Ordinal)
        then
            false
        else
            let source =
                Regex
                    .Escape(normalized)
                    .Replace("\\*\\*/", "(?:.*/)?")
                    .Replace("\\*\\*", ".*")
                    .Replace("\\*", "[^/]*")
                    .Replace("\\?", "[^/]")

            Regex.IsMatch(normalize value, $"^{source}$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    let private defaultItemType (snapshot: EvaluationSnapshot) (path: string) =
        let projectDirectory =
            Path.GetDirectoryName snapshot.ProjectPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        if external projectDirectory path then
            None
        else
            let relative = relativePath projectDirectory (WorkspaceArtifactPath.Create path)
            let absolute = Path.GetFullPath(path).Replace('\\', '/')
            let extension = Path.GetExtension(path).ToLowerInvariant()

            let enabled (dimension: EvaluationDimensionSnapshot) name =
                dimension.Properties
                |> Seq.filter (fun property -> property.Name = name)
                |> Seq.tryLast
                |> Option.map (fun property ->
                    not (String.Equals(property.Value, "false", StringComparison.OrdinalIgnoreCase)))
                |> Option.defaultValue true

            let uses (dimension: EvaluationDimensionSnapshot) name =
                dimension.Properties
                |> Seq.exists (fun property ->
                    property.Name = name
                    && String.Equals(property.Value, "true", StringComparison.OrdinalIgnoreCase))

            let excluded (names: Set<string>) (dimension: EvaluationDimensionSnapshot) =
                dimension.Properties
                |> Seq.filter (fun property ->
                    names.Contains property.Name)
                |> Seq.groupBy _.Name
                |> Seq.collect (fun (_, properties) ->
                    properties
                    |> Seq.last
                    |> fun property -> property.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                |> Seq.exists (fun pattern ->
                    let pattern = pattern.Trim()

                    if Path.IsPathRooted pattern then
                        globMatches pattern absolute
                    else
                        globMatches pattern relative)

            let ordinaryExcludes =
                Set.ofList
                    [ "DefaultItemExcludes"
                      "DefaultItemExcludesInProjectFolder"
                      "DefaultExcludesInProjectFolder" ]

            let defaultItemExcludes = Set.singleton "DefaultItemExcludes"

            let webContentExcludes =
                Set.union ordinaryExcludes (Set.singleton "DefaultWebContentItemExcludes")

            let inDirectory directory =
                relative.Equals(directory, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase)

            let contentExtension = Set.contains extension (Set.ofList [ ".json"; ".config" ])

            let included (dimension: EvaluationDimensionSnapshot) =
                let defaultItems = enabled dimension "EnableDefaultItems"
                let compileItems = enabled dimension "EnableDefaultCompileItems"
                let embeddedResourceItems = enabled dimension "EnableDefaultEmbeddedResourceItems"
                let noneItems = enabled dimension "EnableDefaultNoneItems"
                let contentItems = enabled dimension "EnableDefaultContentItems"

                let workerJsonOrConfig =
                    uses dimension "UsingMicrosoftNETSdkWorker" && contentExtension

                let webWwwRoot = uses dimension "UsingMicrosoftNETSdkWeb" && inDirectory "wwwroot"

                let webJsonOrConfig = uses dimension "UsingMicrosoftNETSdkWeb" && contentExtension

                let razorFile =
                    uses dimension "UsingMicrosoftNETSdkRazor"
                    && Set.contains extension (Set.ofList [ ".cshtml"; ".razor" ])

                let contentDefault =
                    (workerJsonOrConfig && not (excluded ordinaryExcludes dimension))
                    || (webWwwRoot
                        && if inDirectory "wwwroot/.well-known" then
                               not (excluded defaultItemExcludes dimension)
                           else
                               not (excluded ordinaryExcludes dimension))
                    || (webJsonOrConfig && not (excluded webContentExcludes dimension))
                    || (razorFile && not (excluded webContentExcludes dimension))

                let hasContentDefault =
                    workerJsonOrConfig || webWwwRoot || webJsonOrConfig || razorFile

                if not defaultItems then
                    None
                elif contentItems && hasContentDefault then
                    if contentDefault then Some "Content" else None
                elif excluded ordinaryExcludes dimension then
                    None
                elif compileItems && Set.contains extension (Set.ofList [ ".cs"; ".fs"; ".vb" ]) then
                    Some "Compile"
                elif
                    embeddedResourceItems
                    && Set.contains extension (Set.ofList [ ".resx"; ".resw" ])
                then
                    Some "EmbeddedResource"
                elif noneItems then
                    Some "None"
                else
                    None

            snapshot.Dimensions
            |> Seq.map included
            |> Seq.distinct
            |> Seq.toArray
            |> function
                | [||] -> None
                | [| value |] -> value
                | _ -> raise (ArgumentException "The default item policy conflicts across evaluation dimensions.")

    let private defaultItemPolicy snapshot itemType path =
        defaultItemType snapshot path = Some itemType

    let private appendRequestedItem document snapshot itemType path includeValue =
        match defaultItemType snapshot path with
        | Some defaultType when defaultType = itemType -> false
        | Some defaultType ->
            appendRemove document defaultType includeValue
            appendItem document itemType includeValue []
            true
        | None ->
            appendItem document itemType includeValue []
            true

    let private generated (directory: string) (path: string) =
        let relative = Path.GetRelativePath(directory, path).Replace('\\', '/')

        relative.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
        || relative.Equals(".generated", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(".generated/", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith("/.generated", StringComparison.OrdinalIgnoreCase)
        || relative.Contains("/.generated/", StringComparison.OrdinalIgnoreCase)

    let private effectiveItemTypes (snapshot: EvaluationSnapshot) includeValue path =
        snapshot.Dimensions
        |> Seq.collect _.Items
        |> Seq.filter (fun item ->
            item.EvaluatedInclude = includeValue
            || (not (isNull item.ResolvedPath) && item.ResolvedPath.Value = path))
        |> Seq.map _.ItemType
        |> Seq.filter (fun itemType -> itemTypes.Contains itemType)
        |> Seq.distinct
        |> Seq.toArray

    let private effectiveItemType snapshot includeValue path =
        let types = effectiveItemTypes snapshot includeValue path

        if types.Length <> 1 then
            raise (ArgumentException "The effective item type is ambiguous.")

        types[0]

    let private containsItemGlob (document: XDocument) itemType includeValue =
        document.Descendants(name itemType)
        |> Seq.exists (fun item -> attribute "Include" item = Some includeValue)

    let private request
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (targets: WorkspaceArtifactPath list)
        (intents: MutationIntent list)
        =
        let solutionDirectory =
            Path.GetDirectoryName workspace.BackingPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let externalTargets =
            targets
            |> Seq.filter (fun path -> external solutionDirectory path.Value)
            |> Seq.toArray

        let roots =
            seq {
                yield WorkspaceArtifactPath.Create solutionDirectory

                for target in externalTargets do
                    yield
                        WorkspaceArtifactPath.Create(
                            Path.GetDirectoryName target.Value
                            |> Option.ofObj
                            |> Option.defaultValue solutionDirectory
                        )
            }
            |> Seq.distinct
            |> ImmutableArray.CreateRange

        let values =
            [ yield MutationIntent.Overwrite
              if externalTargets.Length > 0 then
                  yield MutationIntent.AccessExternalPath
              yield! intents ]
            |> ImmutableHashSet.CreateRange

        { CommandId = command.CommandId
          Targets = targets |> Seq.distinct |> ImmutableArray.CreateRange
          Arguments = command.Arguments
          ExpectedRevision = command.ExpectedRevision
          Intents = values
          AuthorizedRoots = roots }

    let private makePlan
        (workspace: SolutionWorkspace)
        (command: CommandMutationRequest)
        (actions: MutationAction list)
        (paths: string list)
        (intents: MutationIntent list)
        =
        let artifacts = paths |> List.map WorkspaceArtifactPath.Create

        Success
            { Request = request workspace command artifacts intents
              Actions = actions |> List.toArray
              Paths = artifacts |> List.toArray }

    let private replaceProject path document encoding preamble lineEnding =
        MutationAction.ReplaceFile(path, saveDocument document encoding preamble lineEnding)

    let plan
        (workspace: SolutionWorkspace)
        (project: SolutionProjectProjection)
        (snapshot: EvaluationSnapshot)
        (command: CommandMutationRequest)
        (_: CancellationToken)
        =
        if snapshot.CapabilityProfile <> WorkspaceCapabilityProfile.Full then
            unsupported "The evaluated project system does not grant project write capability."
        elif command.TargetId <> Some project.Node.NodeId then
            missing "targetId" "The command target was not found."
        else
            match tryDescribe command.CommandId with
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

                    let unwrap =
                        function
                        | Ok value -> value
                        | Error message -> raise (ArgumentException message)

                    let item path = itemPath path |> unwrap
                    let kind () = itemType () |> unwrap

                    let directoryOperand path =
                        Directory.Exists path || Path.EndsInDirectorySeparator path

                    let actions, paths, intents =
                        match command.CommandId.Value with
                        | "project.item.add" ->
                            let source, kind, link =
                                item "path", kind (), optionalBoolean "link" command.Arguments |> unwrap

                            if generated directory source then
                                raise (ArgumentException "Generated items are read-only.")
                            elif not (File.Exists source || Directory.Exists source) then
                                raise (ArgumentException "The item path does not exist.")
                            elif Directory.Exists source then
                                if link || external directory source then
                                    raise (ArgumentException "Linked items must be external files.")

                                let includePath = relativePath directory (WorkspaceArtifactPath.Create source)
                                let includeValue = $"{(includePath.TrimEnd('/'))}/**/*"

                                let files =
                                    Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                                    |> Seq.toArray

                                let allIncluded =
                                    files.Length > 0 && (files |> Array.forall (evaluatedAs snapshot kind))

                                if allIncluded || containsItemGlob document kind includeValue then
                                    [], [], []
                                else
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
                                        (relativePath directory (WorkspaceArtifactPath.Create destination))

                                (if not declared then
                                     [ MutationAction.ReplaceFile(destination, File.ReadAllBytes source) ]
                                 else
                                     [ MutationAction.ReplaceFile(destination, File.ReadAllBytes source)
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
                                    let includeValue = relativePath directory (WorkspaceArtifactPath.Create source)

                                    if link then
                                        appendItem document kind includeValue [ "Link", Path.GetFileName source ]
                                    else
                                        appendRequestedItem document snapshot kind source includeValue |> ignore

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
                                    (relativePath directory (WorkspaceArtifactPath.Create destination))

                            let file =
                                MutationAction.ReplaceFile(
                                    destination,
                                    Encoding.UTF8.GetBytes(
                                        optionalText "contents" command.Arguments
                                        |> unwrap
                                        |> Option.defaultValue String.Empty
                                    )
                                )

                            (if not declared then [ file ] else [ file; updatedDocument () ]),
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
                                    (relativePath directory (WorkspaceArtifactPath.Create destination))

                            (if not declared then
                                 [ MutationAction.ReplaceFile(destination, File.ReadAllBytes source) ]
                             else
                                 [ MutationAction.ReplaceFile(destination, File.ReadAllBytes source)
                                   updatedDocument () ]),
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

                                    if rename.IndexOfAny([| '/'; '\\' |]) >= 0 then
                                        raise (ArgumentException "The name must be one path segment.")

                                    Path.Combine(
                                        Path.GetDirectoryName source |> Option.ofObj |> Option.defaultValue directory,
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
                                raise (ArgumentException "The item source or destination is invalid.")
                            elif
                                not (
                                    String.Equals(
                                        Path.GetExtension source,
                                        Path.GetExtension destination,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                            then
                                raise (ArgumentException "Rename and move require the same file extension.")
                            elif external directory source <> external directory destination then
                                raise (ArgumentException "Rename and move cannot cross the project boundary.")

                            let sourceInclude = relativePath directory (WorkspaceArtifactPath.Create source)

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
                                    |> Option.defaultWith (fun () -> element.Attribute(name "Update"))

                                update.Value <- destinationInclude

                                [ MutationAction.Move(source, destination); updatedDocument () ],
                                [ source; destination; projectPath ],
                                []
                            | None -> [ MutationAction.Move(source, destination) ], [ source; destination ], []
                        | "project.item.remove"
                        | "project.item.delete" ->
                            let path = item "path"

                            if directoryOperand path then
                                raise (ArgumentException "Remove and delete require file operands.")
                            elif generated directory path then
                                raise (ArgumentException "Generated items are read-only.")

                            let includeValue = relativePath directory (WorkspaceArtifactPath.Create path)

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
                                |> Option.defaultWith (fun () -> effectiveItemType snapshot includeValue path)
                                |> fun defaultType -> appendRemove document defaultType includeValue

                            if command.CommandId.Value = "project.item.delete" then
                                if not (File.Exists path) then
                                    raise (ArgumentException "The item path does not exist.")

                                [ updatedDocument (); MutationAction.Trash path ], [ projectPath; path ], []
                            else
                                [ updatedDocument () ], [ projectPath ], []
                        | "project.item.set-build-action" ->
                            let path, kind = item "path", kind ()

                            if generated directory path then
                                raise (ArgumentException "Generated items are read-only.")

                            let includeValue = relativePath directory (WorkspaceArtifactPath.Create path)

                            let existing =
                                document.Descendants()
                                |> Seq.tryFind (fun element ->
                                    attribute "Include" element = Some includeValue
                                    || attribute "Update" element = Some includeValue)

                            let sourceType =
                                existing
                                |> Option.map (fun element -> element.Name.LocalName)
                                |> Option.defaultWith (fun () -> effectiveItemType snapshot includeValue path)

                            if sourceType <> kind then
                                defaultItemType snapshot path
                                |> Option.filter ((<>) kind)
                                |> Option.iter (fun defaultType -> appendRemove document defaultType includeValue)

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

                            if not (metadataNames.Contains metadataName) || generated directory path then
                                raise (ArgumentException "The item metadata request is not writable.")

                            let includeValue = relativePath directory (WorkspaceArtifactPath.Create path)

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
                                        |> Seq.filter (fun item -> attribute "Update" item = Some includeValue)
                                        |> Seq.last
                                    else
                                        appendItem document itemType includeValue []

                                        document.Descendants(name itemType)
                                        |> Seq.filter (fun item -> attribute "Include" item = Some includeValue)
                                        |> Seq.last)

                            match element.Element(name metadataName) with
                            | null -> element.Add(XElement(name metadataName, metadataValue))
                            | metadata -> metadata.Value <- metadataValue

                            [ updatedDocument () ], [ projectPath ], []
                        | "project.property.set" ->
                            let propertyName, propertyValue =
                                requiredChoice "name" command.Arguments |> unwrap,
                                requiredText "value" command.Arguments |> unwrap

                            validateProperty directory propertyName propertyValue |> unwrap

                            let scope =
                                match value "scope" command.Arguments with
                                | None ->
                                    let importedDeclares =
                                        ProjectPropertyRegistry.hasImportedProperty
                                            workspace.BackingPath
                                            snapshot
                                            propertyName
                                        |> Result.defaultWith (fun message -> raise (IOException message))

                                    if importedDeclares then
                                        raise (
                                            ArgumentException
                                                "The property is declared in an import; supply its explicit writable scope and condition (use an empty condition for an unconditional group)."
                                        )

                                    projectPath
                                | Some(Path path) ->
                                    let full = Path.GetFullPath(path.Value, directory)

                                    if
                                        full <> projectPath
                                        && not (
                                            ProjectPropertyRegistry.isEligibleScope
                                                workspace.BackingPath
                                                snapshot
                                                (WorkspaceArtifactPath.Create full)
                                        )
                                    then
                                        raise (
                                            ArgumentException "The writable scope is not an eligible project import."
                                        )

                                    full
                                | _ -> raise (ArgumentException "'scope' must be a path.")

                            let suppliedCondition = optionalText "condition" command.Arguments |> unwrap

                            let condition =
                                suppliedCondition
                                |> Option.bind (fun value -> if value.Length = 0 then None else Some value)

                            let scopeDocument, scopeEncoding, scopePreamble, scopeLineEnding =
                                if scope = projectPath then
                                    document, encoding, preamble, lineEnding
                                else
                                    readDocument scope

                            let matches = scopeDocument.Descendants(name propertyName) |> Seq.toArray

                            if matches |> Seq.exists (fun property -> (attribute "Condition" property).IsSome) then
                                raise (ArgumentException "Property-level conditions are not supported.")

                            let groups = matches |> Seq.map _.Parent |> Seq.distinct |> Seq.toArray

                            if
                                suppliedCondition.IsNone
                                && (groups.Length > 1
                                    || scope <> projectPath
                                    || (groups.Length = 1 && (attribute "Condition" groups[0]).IsSome))
                            then
                                raise (
                                    ArgumentException
                                        "The property scope is ambiguous; supply an explicit condition (or an empty condition for an unconditional group)."
                                )

                            let group =
                                if suppliedCondition.IsNone && groups.Length = 1 then
                                    groups[0]
                                else
                                    scopeDocument.Root.Elements(name "PropertyGroup")
                                    |> Seq.tryFind (fun element -> attribute "Condition" element = condition)
                                    |> Option.defaultWith (fun () ->
                                        let value = XElement(name "PropertyGroup") in

                                        condition
                                        |> Option.iter (fun text -> value.SetAttributeValue(name "Condition", text))

                                        scopeDocument.Root.Add(
                                            XText($"{newline scopeDocument}  "),
                                            value,
                                            XText(newline scopeDocument)
                                        )

                                        value)

                            match group.Element(name propertyName) with
                            | null ->
                                group.Add(
                                    XText($"{newline scopeDocument}    "),
                                    XElement(name propertyName, propertyValue),
                                    XText($"{newline scopeDocument}  ")
                                )
                            | property -> property.Value <- propertyValue

                            [ replaceProject scope scopeDocument scopeEncoding scopePreamble scopeLineEnding ],
                            [ scope ],
                            []
                        | _ -> raise (ArgumentException "The command is not available.")

                    makePlan workspace command actions paths intents
                with
                | :? ArgumentException as error -> invalid "command" error.Message
                | :? IOException -> invalid "project" "The project file could not be read."
                | :? UnauthorizedAccessException -> invalid "project" "The project file could not be read."
                | :? System.Xml.XmlException -> invalid "project" "The project XML is malformed."
