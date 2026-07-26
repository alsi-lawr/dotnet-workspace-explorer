namespace Dotnet.CLI.Plus

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport
open System.Xml.Linq

type internal DeclaredProjectProperty =
    { Name: string
      Scope: WorkspaceArtifactPath
      Condition: string option
      Value: string }

type internal HydratedProject =
    { Snapshot: EvaluationSnapshot
      DeclaredProperties: ImmutableArray<DeclaredProjectProperty> }

module internal ProjectPropertyRegistry =
    let Names =
        Set.ofList
            [ "AssemblyName"
              "RootNamespace"
              "OutputType"
              "TargetFramework"
              "TargetFrameworks"
              "RuntimeIdentifier"
              "RuntimeIdentifiers"
              "LangVersion"
              "TreatWarningsAsErrors"
              "IsPackable"
              "PackageId"
              "Version"
              "SignAssembly"
              "AssemblyOriginatorKeyFile"
              "SelfContained"
              "PublishSingleFile"
              "PublishTrimmed"
              "PublishAot" ]

    let private generated directory path =
        let relative = Path.GetRelativePath(directory, path).Replace('\\', '/')

        relative.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
        || relative.Equals(".generated", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(".generated/", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith("/.generated", StringComparison.OrdinalIgnoreCase)
        || relative.Contains("/.generated/", StringComparison.OrdinalIgnoreCase)

    let private attribute local (element: XElement) =
        element.Attribute(XName.Get local) |> Option.ofObj |> Option.map _.Value

    let eligibleScopes (workspacePath: WorkspaceArtifactPath) (snapshot: EvaluationSnapshot) =
        let workspaceDirectory =
            Path.GetDirectoryName workspacePath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let projectDirectory =
            Path.GetDirectoryName snapshot.ProjectPath.Value
            |> Option.ofObj
            |> Option.defaultValue workspaceDirectory

        snapshot.Imports
        |> Seq.filter (fun path -> File.Exists path.Value && not (generated projectDirectory path.Value))
        |> Seq.distinct
        |> ImmutableArray.CreateRange

    let isEligibleScope
        (workspacePath: WorkspaceArtifactPath)
        (snapshot: EvaluationSnapshot)
        (path: WorkspaceArtifactPath)
        =
        eligibleScopes workspacePath snapshot
        |> Seq.exists (fun candidate -> candidate.Value = path.Value)

    let declarations workspacePath snapshot =
        try
            eligibleScopes workspacePath snapshot
            |> Seq.collect (fun path ->
                let document = XDocument.Load(path.Value, LoadOptions.PreserveWhitespace)

                document.Descendants()
                |> Seq.choose (fun property ->
                    if
                        Names.Contains property.Name.LocalName
                        && (attribute "Condition" property).IsNone
                    then
                        property.Parent
                        |> Option.ofObj
                        |> Option.bind (fun group ->
                            if group.Name = XName.Get "PropertyGroup" then
                                Some(
                                    { Name = property.Name.LocalName
                                      Scope = path
                                      Condition = attribute "Condition" group
                                      Value = property.Value }
                                    : DeclaredProjectProperty
                                )
                            else
                                None)
                    else
                        None))
            |> Seq.distinct
            |> ImmutableArray.CreateRange
            |> Ok
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? System.Xml.XmlException -> Error "Project declarations could not be read."

    let hasImportedProperty workspacePath snapshot propertyName =
        try
            eligibleScopes workspacePath snapshot
            |> Seq.filter (fun path -> path.Value <> snapshot.ProjectPath.Value)
            |> Seq.exists (fun path ->
                let document = XDocument.Load(path.Value, LoadOptions.PreserveWhitespace)

                document.Descendants(XName.Get propertyName)
                |> Seq.exists (fun property ->
                    property.Parent
                    |> Option.ofObj
                    |> Option.exists (fun group -> group.Name = XName.Get "PropertyGroup")))
            |> Ok
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? System.Xml.XmlException -> Error "Project declarations could not be read."

type internal WorkspaceStateServices =
    { OpenAsync: string -> CancellationToken -> Task<WorkspaceOutcome<SolutionWorkspace>>
      EvaluateAsync:
          WorkspaceArtifactPath
              -> WorkspaceArtifactPath
              -> CancellationToken
              -> Task<WorkspaceOutcome<EvaluationSnapshot>>
      InvalidateAsync:
          ImmutableArray<WorkspaceArtifactPath> -> CancellationToken -> Task<WorkspaceOutcome<MsBuildInvalidationKind>>
      RefreshAsync: unit -> Task
      DisposeAsync: unit -> Task }

type internal WorkspaceStateOptions =
    { HydrationLimit: int
      TokenSecret: byte array }

type internal WorkspacePageResult =
    { Revision: int64
      ParentId: NodeId
      Nodes: ImmutableArray<WorkspaceNode>
      NextToken: ContinuationToken option
      Delta: WorkspaceDelta option }

type internal WorkspaceRefreshResult =
    { Revision: int64
      Reset: bool
      Delta: WorkspaceDelta option
      ResetEvent: WorkspaceReset option
      Diagnostics: ImmutableArray<WorkspaceDiagnostic> }

type internal WorkspaceExportSnapshot =
    { Descriptor: WorkspaceDescriptor
      Revision: int64
      Nodes: ImmutableArray<WorkspaceNode> }

[<RequireQualifiedAccess>]
type internal WorkspaceInvalidationResult =
    | None
    | Delta of WorkspaceDelta
    | Reset of WorkspaceReset

[<RequireQualifiedAccess>]
type internal WatchKind =
    | ExactFile
    | RecursiveGlob

type internal WatchSpec =
    { Directory: string
      Filters: ImmutableArray<string>
      IncludeSubdirectories: bool
      Kind: WatchKind }

type internal PlacementKey = PlacementKey of string list

type internal Placement =
    { Key: PlacementKey
      Node: WorkspaceNode
      ParentId: NodeId option
      Index: int }

type internal WorkspaceData =
    { Workspace: SolutionWorkspace
      Hydrated: Map<string, HydratedProject>
      Recency: string list
      Revision: int64
      NeedsRebase: bool }

type internal BodyContribution =
    { Logical: string list
      Content: string list
      Display: string
      Dimension: string }

[<RequireQualifiedAccess>]
module internal ContinuationTokens =
    type Payload =
        { WorkspaceId: string
          ParentId: string
          Offset: int
          Revision: int64 }

    let private writeString (writer: BinaryWriter) (value: string) =
        let bytes = Encoding.UTF8.GetBytes value
        writer.Write bytes.Length
        writer.Write bytes

    let private readString (reader: BinaryReader) =
        let length = reader.ReadInt32()

        if length < 0 || length > 4096 then
            invalidArg "token" "The continuation token contains an invalid string."

        reader.ReadBytes length
        |> fun bytes ->
            if bytes.Length <> length then
                invalidArg "token" "The continuation token is truncated."

            UTF8Encoding(false, true).GetString bytes

    let create (secret: byte array) (payload: Payload) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        writeString writer payload.WorkspaceId
        writeString writer payload.ParentId
        writer.Write payload.Offset
        writer.Write payload.Revision
        writer.Flush()
        let body = stream.ToArray()
        use hmac = new HMACSHA256(secret)
        let signature = hmac.ComputeHash body
        $"{Convert.ToBase64String body}.{Convert.ToBase64String signature}"

    let tryParse (secret: byte array) (value: string) =
        try
            let parts = value.Split('.', StringSplitOptions.None)

            if parts.Length <> 2 then
                None
            else
                let body = Convert.FromBase64String parts[0]
                let supplied = Convert.FromBase64String parts[1]
                use hmac = new HMACSHA256(secret)
                let expected = hmac.ComputeHash body

                if not (CryptographicOperations.FixedTimeEquals(expected, supplied)) then
                    None
                else
                    use stream = new MemoryStream(body, false)
                    use reader = new BinaryReader(stream, Encoding.UTF8, true)

                    let payload =
                        { WorkspaceId = readString reader
                          ParentId = readString reader
                          Offset = reader.ReadInt32()
                          Revision = reader.ReadInt64() }

                    if payload.Offset < 0 || stream.Position <> stream.Length then
                        None
                    else
                        Some payload
        with
        | :? ArgumentException
        | :? EndOfStreamException
        | :? DecoderFallbackException
        | :? FormatException -> None

module internal WorkspaceStatePure =
    let private canonicalBytes (values: seq<string>) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)

        for value in values do
            let bytes = Encoding.UTF8.GetBytes value
            writer.Write bytes.Length
            writer.Write bytes

        writer.Flush()
        stream.ToArray()

    let canonicalHash (values: seq<string>) =
        values |> canonicalBytes |> SHA256.HashData |> Convert.ToHexString

    let private pathValue (path: WorkspaceArtifactPath | null) =
        Option.ofObj path |> Option.map _.Value |> Option.defaultValue String.Empty

    let private dimensionName (dimension: EvaluationDimensionSnapshot) =
        if dimension.TargetFramework.HasValue then
            dimension.TargetFramework.Value.Value
        else
            "outer"

    let private bounded limit (value: string) =
        if value.Length <= limit then
            value
        else
            $"{value[.. limit - 2]}…"

    let private evaluatedContributions (snapshot: EvaluationSnapshot) =
        seq {
            for dimension in snapshot.Dimensions do
                let framework = dimensionName dimension

                for property in dimension.Properties do
                    yield
                        { Logical = [ "evaluated-property"; property.Name ]
                          Content = [ property.Value ]
                          Display = $"Evaluated {property.Name} = {property.Value}"
                          Dimension = framework }

                for item in dimension.Items do
                    let metadata =
                        item.Metadata
                        |> Seq.collect (fun value -> [ value.Name; value.Value ])
                        |> Seq.toList

                    let resolved = pathValue item.ResolvedPath

                    let details =
                        [ if not (String.IsNullOrEmpty resolved) then
                              $"path={resolved}"
                          if item.Metadata.Length > 0 then
                              item.Metadata
                              |> Seq.map (fun value -> $"{value.Name}={value.Value}")
                              |> String.concat ", " ]
                        |> String.concat "; "

                    yield
                        { Logical = [ "item"; item.ItemType; item.EvaluatedInclude ]
                          Content = resolved :: metadata
                          Display =
                            if String.IsNullOrEmpty details then
                                $"{item.ItemType}: {item.EvaluatedInclude}"
                            else
                                $"{item.ItemType}: {item.EvaluatedInclude} ({details})"
                          Dimension = framework }

                for reference in dimension.ProjectReferences do
                    let resolved = pathValue reference.ResolvedPath

                    yield
                        { Logical = [ "project-reference"; reference.Include ]
                          Content = [ resolved ]
                          Display =
                            if String.IsNullOrEmpty resolved then
                                $"Project reference: {reference.Include}"
                            else
                                $"Project reference: {reference.Include} -> {resolved}"
                          Dimension = framework }

                for reference in dimension.References do
                    let resolved = pathValue reference.ResolvedPath

                    yield
                        { Logical = [ "reference"; reference.Include ]
                          Content = [ resolved ]
                          Display =
                            if String.IsNullOrEmpty resolved then
                                $"Reference: {reference.Include}"
                            else
                                $"Reference: {reference.Include} -> {resolved}"
                          Dimension = framework }

                for package in dimension.Packages do
                    let version = Option.ofObj package.Version |> Option.defaultValue String.Empty

                    yield
                        { Logical = [ "package"; package.Id ]
                          Content = [ version ]
                          Display = $"Package: {package.Id} {version}".Trim()
                          Dimension = framework }

                for analyzer in dimension.Analyzers do
                    yield
                        { Logical = [ "analyzer"; analyzer.Value ]
                          Content = [ analyzer.Value ]
                          Display = $"Analyzer: {analyzer.Value}"
                          Dimension = framework }
        }
        |> Seq.toArray

    let private declaredContributions (descriptor: WorkspaceDescriptor) (hydrated: HydratedProject) =
        hydrated.DeclaredProperties
        |> Seq.map (fun property ->
            let condition = property.Condition |> Option.defaultValue "<none>"

            let scope =
                Path.GetRelativePath(
                    Path.GetDirectoryName descriptor.Path.Value
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory()),
                    property.Scope.Value
                )
                |> fun value ->
                    value.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')

            { Logical = [ "declared-property"; property.Name; property.Scope.Value; condition ]
              Content = [ property.Scope.Value; condition; property.Value ]
              Display =
                $"Declared {property.Name} = {bounded 96 property.Value} [scope: {bounded 96 scope}; condition: {bounded 96 condition}]"
              Dimension = "declared" })
        |> Seq.toArray

    let contributions (descriptor: WorkspaceDescriptor) (hydrated: HydratedProject) =
        Seq.append (evaluatedContributions hydrated.Snapshot) (declaredContributions descriptor hydrated)
        |> Seq.toArray

    let projectBodyEntries (descriptor: WorkspaceDescriptor) (project: SolutionProjectProjection) hydrated =
        contributions descriptor hydrated
        |> Seq.groupBy _.Logical
        |> Seq.collect (fun (_, logicalValues) ->
            let variants = logicalValues |> Seq.groupBy _.Content |> Seq.toArray
            let showDimension = variants.Length > 1

            variants
            |> Seq.map (fun (_, values) ->
                let values = values |> Seq.toArray

                let dimensions =
                    values |> Seq.map _.Dimension |> Seq.distinct |> Seq.sort |> String.concat ","

                let representative = values[0]

                let display =
                    if showDimension then
                        $"[{dimensions}] {representative.Display}"
                    else
                        representative.Display

                let boundedDisplay =
                    if display.Length <= 240 then
                        display
                    else
                        $"{display[..199]}… [{canonicalHash [ display ]}]"

                let identity =
                    canonicalHash (Seq.concat [ representative.Logical; representative.Content ])

                let node =
                    WorkspaceNode.Create(
                        descriptor,
                        WorkspaceNodeKind.ProjectItem,
                        NodeSemanticIdentity.Create $"project-body:{project.Node.Identity.Value}:{identity}",
                        boundedDisplay,
                        hydrated.Snapshot.CapabilityProfile
                    )

                let placement =
                    if showDimension then
                        representative.Logical @ [ dimensions; node.NodeId.Value ]
                    else
                        representative.Logical @ [ node.NodeId.Value ]

                placement, node))
        |> Seq.sortBy fst
        |> ImmutableArray.CreateRange

    let projectBody (descriptor: WorkspaceDescriptor) project hydrated =
        projectBodyEntries descriptor project hydrated
        |> Seq.map snd
        |> ImmutableArray.CreateRange

    let snapshotSemanticValues (descriptor: WorkspaceDescriptor) (hydrated: HydratedProject) =
        seq {
            let snapshot = hydrated.Snapshot
            yield snapshot.ProjectPath.Value

            for value in
                contributions descriptor hydrated
                |> Seq.sortBy (fun value -> value.Logical, value.Content, value.Dimension) do
                yield! value.Logical
                yield! value.Content
                yield value.Dimension

            for capability in snapshot.Capabilities do
                yield $"capability:{capability.Value}"

            for path in snapshot.Imports do
                yield $"import:{path.Value}"

            for path in snapshot.WatchInputs do
                yield $"watch:{path.Value}"

            for path in snapshot.GlobRoots do
                yield $"glob:{path.Value}"

            for diagnostic in snapshot.Diagnostics do
                yield $"diagnostic-severity:{int diagnostic.DiagnosticSeverity}"
                yield $"diagnostic-code:{diagnostic.DiagnosticCode.Value}"
                yield $"diagnostic-message:{diagnostic.Message}"
                yield $"diagnostic-retryable:{diagnostic.Retryable}"

                yield
                    $"diagnostic-path:{diagnostic.DiagnosticArtifactPath
                                       |> Option.map _.Value
                                       |> Option.defaultValue String.Empty}"

                yield
                    diagnostic.DiagnosticLocation
                    |> Option.map (fun location -> $"diagnostic-location:{location.Line}:{location.Column}")
                    |> Option.defaultValue "diagnostic-location:"
        }

    let sameSnapshot (descriptor: WorkspaceDescriptor) (left: HydratedProject) (right: HydratedProject) =
        canonicalHash (snapshotSemanticValues descriptor left) = canonicalHash (snapshotSemanticValues descriptor right)

    let private nodeEqual (left: WorkspaceNode) (right: WorkspaceNode) =
        left.NodeId = right.NodeId
        && left.NodeKind = right.NodeKind
        && left.Identity = right.Identity
        && left.Name = right.Name
        && left.Profile = right.Profile
        && left.NodeLoadState = right.NodeLoadState
        && left.AvailableCapabilities.Length = right.AvailableCapabilities.Length
        && Seq.forall2 (=) left.AvailableCapabilities right.AvailableCapabilities

    let private pathIdentity insensitive (path: string) =
        if insensitive then path.ToUpperInvariant() else path

    let placements insensitive (data: WorkspaceData) =
        let root = data.Workspace.RootProjection
        let raw = ResizeArray<PlacementKey * WorkspaceNode * NodeId option>()

        let folderIds =
            Dictionary<string, NodeId>(
                if insensitive then
                    StringComparer.OrdinalIgnoreCase
                else
                    StringComparer.Ordinal
            )

        for folder in root.Folders do
            folderIds[folder.Path] <- folder.Node.NodeId

        let folderParent path =
            path
            |> Option.bind (fun value ->
                match folderIds.TryGetValue value with
                | true, nodeId -> Some nodeId
                | _ -> None)

        for folder in root.Folders do
            raw.Add(PlacementKey [ "folder"; folder.Path ], folder.Node, folderParent folder.ParentPath)

        for item in root.Items do
            raw.Add(
                PlacementKey
                    [ "solution-item"
                      item.FolderPath |> Option.defaultValue String.Empty
                      item.RelativePath ],
                item.Node,
                folderParent item.FolderPath
            )

        for project in root.Projects do
            let key = pathIdentity insensitive project.Path.AbsolutePath.Value

            let node =
                match data.Hydrated.TryFind key with
                | Some hydrated ->
                    WorkspaceNode.CreateWithLoadState(
                        data.Workspace.WorkspaceDescriptor,
                        WorkspaceNodeKind.Project,
                        project.Node.Identity,
                        project.Node.Name,
                        hydrated.Snapshot.CapabilityProfile,
                        WorkspaceNodeLoadState.Hydrated
                    )
                | None -> project.Node

            raw.Add(PlacementKey [ "project"; key ], node, folderParent project.ParentFolderPath)

            match data.Hydrated.TryFind key with
            | Some hydrated ->
                for placement, child in projectBodyEntries data.Workspace.WorkspaceDescriptor project hydrated do
                    raw.Add(PlacementKey("project-body" :: key :: placement), child, Some node.NodeId)
            | None -> ()

        for node in root.BuildTypes do
            raw.Add(PlacementKey [ "configuration"; node.Identity.Value ], node, None)

        for node in root.Platforms do
            raw.Add(PlacementKey [ "platform"; node.Identity.Value ], node, None)

        for dependency in root.Dependencies do
            raw.Add(
                PlacementKey
                    [ "dependency"
                      dependency.ProjectId.Value
                      dependency.DependsOnProjectId.Value ],
                dependency.Node,
                Some dependency.ProjectId
            )

        raw
        |> Seq.groupBy (fun (_, _, parentId) -> parentId |> Option.map _.Value |> Option.defaultValue String.Empty)
        |> Seq.collect (fun (_, siblings) ->
            siblings
            |> Seq.sortBy (fun (key, _, _) -> key)
            |> Seq.mapi (fun index (key, node, parentId) ->
                { Key = key
                  Node = node
                  ParentId = parentId
                  Index = index }))
        |> Seq.sortBy _.Key
        |> Seq.toArray

    let diff workspaceId baseRevision oldPlacements newPlacements =
        let oldByKey = oldPlacements |> Seq.map (fun value -> value.Key, value) |> Map.ofSeq
        let newByKey = newPlacements |> Seq.map (fun value -> value.Key, value) |> Map.ofSeq

        let depths (placements: Placement array) =
            let byId = placements |> Seq.map (fun value -> value.Node.NodeId, value) |> dict
            let values = Dictionary<NodeId, int>()

            let rec depth (visiting: Set<string>) (nodeId: NodeId) =
                match values.TryGetValue nodeId with
                | true, value -> value
                | _ when visiting |> Set.contains nodeId.Value -> 0
                | _ ->
                    let value =
                        match byId.TryGetValue nodeId with
                        | true, placement ->
                            placement.ParentId
                            |> Option.map (fun parentId -> 1 + depth (visiting |> Set.add nodeId.Value) parentId)
                            |> Option.defaultValue 0
                        | _ -> 0

                    values[nodeId] <- value
                    value

            for placement in placements do
                depth Set.empty placement.Node.NodeId |> ignore

            fun nodeId ->
                match values.TryGetValue nodeId with
                | true, value -> value
                | _ -> 0

        let oldDepth = depths oldPlacements
        let newDepth = depths newPlacements

        let removals =
            oldByKey
            |> Seq.choose (fun (KeyValue(key, oldValue)) ->
                if newByKey.ContainsKey key then
                    None
                else
                    Some(key, oldValue))
            |> Seq.sortBy (fun (key, value) ->
                -oldDepth value.Node.NodeId, value.ParentId |> Option.map _.Value, -value.Index, key)
            |> Seq.map (fun (_, value) -> WorkspaceChange.Removed(value.Node.NodeId, value.ParentId, value.Index))

        let replacements, moves, updates =
            oldByKey
            |> Seq.choose (fun (KeyValue(key, oldValue)) ->
                newByKey.TryFind key |> Option.map (fun newValue -> key, oldValue, newValue))
            |> Seq.fold
                (fun (replaceValues, moveValues, updateValues) (key, oldValue, newValue) ->
                    if oldValue.Node.NodeId <> newValue.Node.NodeId then
                        ((key,
                          newValue,
                          WorkspaceChange.Replaced(
                              oldValue.Node.NodeId,
                              newValue.Node,
                              newValue.ParentId,
                              newValue.Index
                          ))
                         :: replaceValues,
                         moveValues,
                         updateValues)
                    else
                        let nextMoves =
                            if oldValue.ParentId <> newValue.ParentId then
                                (key,
                                 newValue,
                                 WorkspaceChange.Moved(
                                     newValue.Node.NodeId,
                                     oldValue.ParentId,
                                     oldValue.Index,
                                     newValue.ParentId,
                                     newValue.Index
                                 ))
                                :: moveValues
                            else
                                moveValues

                        let nextUpdates =
                            if not (nodeEqual oldValue.Node newValue.Node) then
                                (key,
                                 newValue,
                                 WorkspaceChange.Updated(newValue.Node, newValue.ParentId, newValue.Index))
                                :: updateValues
                            else
                                updateValues

                        replaceValues, nextMoves, nextUpdates)
                ([], [], [])

        let ordered values =
            values
            |> Seq.sortBy (fun (key, placement, _) -> newDepth placement.Node.NodeId, key)
            |> Seq.map (fun (_, _, change) -> change)

        let additions =
            newByKey
            |> Seq.choose (fun (KeyValue(key, newValue)) ->
                if oldByKey.ContainsKey key then
                    None
                else
                    Some(key, newValue))
            |> Seq.sortBy (fun (key, value) ->
                newDepth value.Node.NodeId, value.ParentId |> Option.map _.Value, value.Index, key)
            |> Seq.map (fun (_, value) -> WorkspaceChange.Added(value.Node, value.ParentId, value.Index))

        let changes =
            Seq.concat [ removals; ordered replacements; ordered moves; ordered updates; additions ]
            |> ImmutableArray.CreateRange

        if changes.IsEmpty then
            None
        else
            Some
                { WorkspaceId = workspaceId
                  BaseRevision = WorkspaceRevision.Create baseRevision
                  NewRevision = WorkspaceRevision.Create(baseRevision + 1L)
                  Changes = changes
                  Diagnostics = ImmutableArray<WorkspaceDiagnostic>.Empty }

    let omitLazyBodyChanges (oldPlacements: Placement array) (delta: WorkspaceDelta) =
        let oldKinds =
            oldPlacements
            |> Seq.map (fun value -> value.Node.NodeId, value.Node.NodeKind)
            |> dict

        let isBody =
            function
            | WorkspaceChange.Added(node, _, _)
            | WorkspaceChange.Updated(node, _, _)
            | WorkspaceChange.Replaced(_, node, _, _) -> node.NodeKind = WorkspaceNodeKind.ProjectItem
            | WorkspaceChange.Removed(nodeId, _, _) ->
                match oldKinds.TryGetValue nodeId with
                | true, kind -> kind = WorkspaceNodeKind.ProjectItem
                | _ -> false
            | WorkspaceChange.Moved(nodeId, _, _, _, _) ->
                match oldKinds.TryGetValue nodeId with
                | true, kind -> kind = WorkspaceNodeKind.ProjectItem
                | _ -> false

        { delta with
            Changes = delta.Changes |> Seq.filter (isBody >> not) |> ImmutableArray.CreateRange }

    let watchPlan insensitive (data: WorkspaceData) =
        let specs = ResizeArray<WatchSpec>()

        let comparer =
            if insensitive then
                StringComparer.OrdinalIgnoreCase
            else
                StringComparer.Ordinal

        let exact (path: string) =
            let directory = Path.GetDirectoryName path |> Option.ofObj

            directory
            |> Option.iter (fun value ->
                specs.Add
                    { Directory = Path.GetFullPath value
                      Filters =
                        Path.GetFileName path
                        |> Option.ofObj
                        |> Option.defaultValue "*"
                        |> ImmutableArray.Create
                      IncludeSubdirectories = false
                      Kind = WatchKind.ExactFile })

        exact data.Workspace.WorkspaceDescriptor.Path.Value
        exact data.Workspace.BackingPath.Value

        for KeyValue(_, hydrated) in data.Hydrated do
            let snapshot = hydrated.Snapshot
            exact snapshot.ProjectPath.Value

            for path in Seq.append snapshot.Imports snapshot.WatchInputs do
                exact path.Value

            let projectDirectory =
                Path.GetDirectoryName snapshot.ProjectPath.Value |> Option.ofObj

            projectDirectory
            |> Option.iter (fun projectDirectory ->
                let mutable directory = Some(DirectoryInfo projectDirectory)

                while directory.IsSome do
                    let current = directory.Value

                    for name in
                        [ "Directory.Build.props"
                          "Directory.Build.targets"
                          "Directory.Packages.props"
                          "global.json" ] do
                        exact (Path.Combine(current.FullName, name))

                    directory <- current.Parent |> Option.ofObj)

            for root in snapshot.GlobRoots do
                if Directory.Exists root.Value then
                    specs.Add
                        { Directory = root.Value
                          Filters = ImmutableArray.Create "*"
                          IncludeSubdirectories = true
                          Kind = WatchKind.RecursiveGlob }

            for dimension in snapshot.Dimensions do
                for item in dimension.Items do
                    item.ResolvedPath
                    |> Option.ofObj
                    |> Option.iter (fun path ->
                        let projectRoot = Path.GetDirectoryName snapshot.ProjectPath.Value |> Option.ofObj

                        let relative =
                            projectRoot
                            |> Option.map (fun value -> Path.GetRelativePath(value, path.Value))
                            |> Option.defaultValue path.Value

                        if
                            Path.IsPathRooted relative
                            || relative = ".."
                            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                        then
                            exact path.Value)

        specs
        |> Seq.filter (fun value -> Directory.Exists value.Directory && not value.Filters.IsEmpty)
        |> Seq.groupBy (fun value -> pathIdentity insensitive value.Directory, value.IncludeSubdirectories, value.Kind)
        |> Seq.map (fun (_, values) ->
            let first = Seq.head values

            { first with
                Filters =
                    values
                    |> Seq.collect _.Filters
                    |> Seq.distinctBy (fun value -> if insensitive then value.ToUpperInvariant() else value)
                    |> Seq.sortWith (fun left right -> comparer.Compare(left, right))
                    |> ImmutableArray.CreateRange })
        |> Seq.sortWith (fun left right ->
            let directory = comparer.Compare(left.Directory, right.Directory)

            if directory <> 0 then
                directory
            else
                let includeChildren = compare left.IncludeSubdirectories right.IncludeSubdirectories

                if includeChildren <> 0 then
                    includeChildren
                else
                    compare left.Kind right.Kind)
        |> ImmutableArray.CreateRange

type internal WorkspaceState
    private (target: string, services: WorkspaceStateServices, options: WorkspaceStateOptions, initial: WorkspaceData) =
    let gate = new SemaphoreSlim(1, 1)

    let caseSemantics =
        HostFileSystemCaseDetector.DetectFromExistingPath(initial.Workspace.WorkspaceDescriptor.Path.Value)

    let insensitive = caseSemantics = HostFileSystemCaseSemantics.Insensitive

    let pathKey (path: string) =
        let value = Path.GetFullPath path
        if insensitive then value.ToUpperInvariant() else value

    let mutable current = initial
    let mutable disposed = false

    let cancelledError =
        RpcErrors.create "cancelled" "The workspace operation was cancelled." None

    let failureError (failure: WorkspaceFailure) : RpcError =
        if failure.Code = WorkspaceErrorCode.Cancelled then
            cancelledError
        else
            PublicProtocol.failureError failure

    let projectByKey (workspace: SolutionWorkspace) (key: string) =
        workspace.RootProjection.Projects
        |> Seq.tryFind (fun project -> pathKey project.Path.AbsolutePath.Value = key)

    let touch (key: string) (recency: string list) =
        key :: (recency |> List.filter ((<>) key))

    let evaluate
        (workspace: SolutionWorkspace)
        (project: SolutionProjectProjection)
        (cancellationToken: CancellationToken)
        =
        task {
            let! outcome = services.EvaluateAsync project.Path.AbsolutePath workspace.BackingPath cancellationToken

            return
                match outcome with
                | WorkspaceOutcome.Success snapshot ->
                    ProjectPropertyRegistry.declarations workspace.BackingPath snapshot
                    |> Result.map (fun declared ->
                        { Snapshot = snapshot
                          DeclaredProperties = declared })
                    |> Result.mapError (fun message -> RpcErrors.create "project" message None)
                | WorkspaceOutcome.Failure failure -> Error(failureError failure)
        }

    let stageMaterialized
        (source: WorkspaceData)
        (workspace: SolutionWorkspace)
        (cancellationToken: CancellationToken)
        =
        task {
            let mutable result = Ok Map.empty

            for key in source.Recency |> List.rev do
                match result, projectByKey workspace key with
                | Ok values, Some project when not project.IsFilteredOut ->
                    let! evaluated = evaluate workspace project cancellationToken
                    result <- evaluated |> Result.map (fun snapshot -> values.Add(key, snapshot))
                | _ -> ()

            return result
        }

    let applyCandidate (candidate: WorkspaceData) =
        let oldPlacements = WorkspaceStatePure.placements insensitive current
        let newPlacements = WorkspaceStatePure.placements insensitive candidate

        let semanticChanged =
            current.Hydrated.Count <> candidate.Hydrated.Count
            || (current.Hydrated
                |> Seq.exists (fun (KeyValue(key, snapshot)) ->
                    match candidate.Hydrated.TryFind key with
                    | Some next ->
                        not (WorkspaceStatePure.sameSnapshot current.Workspace.WorkspaceDescriptor snapshot next)
                    | None -> true))

        let diagnostics =
            candidate.Hydrated.Values
            |> Seq.collect (fun hydrated -> hydrated.Snapshot.Diagnostics)
            |> Seq.sortBy (fun value -> value.DiagnosticCode.Value, value.Message)
            |> ImmutableArray.CreateRange

        match
            WorkspaceStatePure.diff
                current.Workspace.WorkspaceDescriptor.WorkspaceId
                current.Revision
                oldPlacements
                newPlacements
        with
        | None when not semanticChanged ->
            current <-
                { candidate with
                    Revision = current.Revision }

            None
        | None ->
            let delta =
                { WorkspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId
                  BaseRevision = WorkspaceRevision.Create current.Revision
                  NewRevision = WorkspaceRevision.Create(current.Revision + 1L)
                  Changes = ImmutableArray<WorkspaceChange>.Empty
                  Diagnostics = diagnostics }

            current <-
                { candidate with
                    Revision = delta.NewRevision.Value }

            Some delta
        | Some delta ->
            let withDiagnostics = { delta with Diagnostics = diagnostics }

            current <-
                { candidate with
                    Revision = withDiagnostics.NewRevision.Value }

            Some withDiagnostics

    let resetUnsafe (diagnostic: WorkspaceDiagnostic) =
        task {
            try
                do! services.RefreshAsync()
            with _ ->
                ()

            let resetRevision = current.Revision + 1L

            current <-
                { current with
                    Hydrated = Map.empty
                    Recency = []
                    Revision = resetRevision
                    NeedsRebase = true }

            return
                { WorkspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId
                  Revision = WorkspaceRevision.Create resetRevision
                  Diagnostics = ImmutableArray.Create diagnostic }
        }

    let ensureReadyUnsafe (cancellationToken: CancellationToken) =
        task {
            if not current.NeedsRebase then
                return Ok()
            else
                try
                    do! services.RefreshAsync()
                    cancellationToken.ThrowIfCancellationRequested()
                    let! opened = services.OpenAsync target cancellationToken

                    match opened with
                    | WorkspaceOutcome.Success workspace ->
                        current <-
                            { Workspace = workspace
                              Hydrated = Map.empty
                              Recency = []
                              Revision = current.Revision
                              NeedsRebase = false }

                        return Ok()
                    | WorkspaceOutcome.Failure failure -> return Error(failureError failure)
                with :? OperationCanceledException ->
                    return Error cancelledError
        }

    let uncertainty (code: string) (message: string) =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Warning,
            WorkspaceDiagnosticCode.Create code,
            message,
            true,
            CorrelationId.New()
        )

    member _.Descriptor = current.Workspace.WorkspaceDescriptor
    member _.Revision = current.Revision

    member _.WorkspaceAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let! ready = ensureReadyUnsafe cancellationToken

                return
                    ready
                    |> Result.map (fun () ->
                        let enrichments =
                            current.Workspace.RootProjection.Projects
                            |> Seq.choose (fun project ->
                                let key = pathKey project.Path.AbsolutePath.Value

                                current.Hydrated.TryFind key
                                |> Option.map (fun hydrated ->
                                    { ProjectId = project.Node.NodeId
                                      CapabilityProfile = hydrated.Snapshot.CapabilityProfile }))

                        SolutionProjection.EnrichProjectCapabilities(current.Workspace, enrichments))
            finally
                gate.Release() |> ignore
        }

    member _.ProjectAsync(projectId: NodeId, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let! ready = ensureReadyUnsafe cancellationToken

                match ready with
                | Error error ->
                    return
                        Failure(
                            Internal(
                                WorkspaceDiagnostic.CreateSimple(
                                    WorkspaceDiagnosticSeverity.Error,
                                    WorkspaceDiagnosticCode.Create error.Code,
                                    error.Message,
                                    false,
                                    CorrelationId.New()
                                )
                            )
                        )
                | Ok() ->
                    match
                        current.Workspace.RootProjection.Projects
                        |> Seq.tryFind (fun project -> project.Node.NodeId = projectId && not project.IsFilteredOut)
                    with
                    | None ->
                        return
                            Failure(
                                NotFound(
                                    projectId.Value,
                                    WorkspaceDiagnostic.CreateSimple(
                                        WorkspaceDiagnosticSeverity.Error,
                                        WorkspaceDiagnosticCode.Create "not_found",
                                        "The project target was not found.",
                                        false,
                                        CorrelationId.New()
                                    )
                                )
                            )
                    | Some project ->
                        let! evaluated = evaluate current.Workspace project cancellationToken

                        return
                            evaluated
                            |> Result.map (fun hydrated -> current.Workspace, project, hydrated.Snapshot)
                            |> function
                                | Ok value -> Success value
                                | Error error ->
                                    Failure(
                                        Internal(
                                            WorkspaceDiagnostic.CreateSimple(
                                                WorkspaceDiagnosticSeverity.Error,
                                                WorkspaceDiagnosticCode.Create error.Code,
                                                error.Message,
                                                false,
                                                CorrelationId.New()
                                            )
                                        )
                                    )
            finally
                gate.Release() |> ignore
        }

    member _.PathComparer =
        if insensitive then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    member _.RootAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let! ready = ensureReadyUnsafe cancellationToken

                return
                    ready
                    |> Result.map (fun () ->
                        let nodes =
                            WorkspaceStatePure.placements insensitive current
                            |> Seq.filter (fun value -> value.ParentId.IsNone)
                            |> Seq.sortBy _.Index
                            |> Seq.map _.Node
                            |> ImmutableArray.CreateRange

                        current.Revision, nodes)
            finally
                gate.Release() |> ignore
        }

    member _.ChildrenAsync
        (
            parentIdText: string,
            requestedPageSize: int option,
            negotiatedPageSize: int,
            continuation: string option,
            cancellationToken: CancellationToken
        ) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                let! ready = ensureReadyUnsafe cancellationToken

                match ready with
                | Error error -> return Error error
                | Ok() ->
                    let offset =
                        match continuation with
                        | None -> Ok 0
                        | Some value ->
                            match ContinuationTokens.tryParse options.TokenSecret value with
                            | Some payload when payload.Revision <> current.Revision ->
                                Error(PublicProtocol.workspaceConflict current.Revision)
                            | Some payload when
                                payload.WorkspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId.Value
                                && payload.ParentId = parentIdText
                                ->
                                Ok payload.Offset
                            | _ -> Error(RpcErrors.invalidParams "The continuation token is invalid.")

                    match offset with
                    | Error error -> return Error error
                    | Ok pageOffset ->
                        let before = WorkspaceStatePure.placements insensitive current

                        match before |> Array.tryFind (fun value -> value.Node.NodeId.Value = parentIdText) with
                        | None -> return Error(RpcErrors.invalidParams "The requested workspace parent does not exist.")
                        | Some parent ->
                            let project =
                                current.Workspace.RootProjection.Projects
                                |> Seq.tryFind (fun value -> value.Node.NodeId = parent.Node.NodeId)

                            let hydrate () =
                                task {
                                    match project with
                                    | Some value when not value.IsFilteredOut ->
                                        let key = pathKey value.Path.AbsolutePath.Value

                                        match current.Hydrated.TryFind key with
                                        | Some _ ->
                                            current <-
                                                { current with
                                                    Recency = touch key current.Recency }

                                            return Ok None
                                        | None ->
                                            let! evaluated = evaluate current.Workspace value cancellationToken

                                            match evaluated with
                                            | Error error -> return Error error
                                            | Ok snapshot ->
                                                if cancellationToken.IsCancellationRequested then
                                                    return Error cancelledError
                                                else
                                                    let hydrated = current.Hydrated.Add(key, snapshot)
                                                    let recency = touch key current.Recency

                                                    let evicted =
                                                        if hydrated.Count > options.HydrationLimit then
                                                            Some(List.last recency)
                                                        else
                                                            None

                                                    let! invalidation =
                                                        match
                                                            evicted |> Option.bind (projectByKey current.Workspace)
                                                        with
                                                        | Some evictedProject ->
                                                            task {
                                                                let! outcome =
                                                                    services.InvalidateAsync
                                                                        (ImmutableArray.Create<WorkspaceArtifactPath>
                                                                            evictedProject.Path.AbsolutePath)
                                                                        cancellationToken

                                                                return
                                                                    match outcome with
                                                                    | WorkspaceOutcome.Success _ when
                                                                        cancellationToken.IsCancellationRequested
                                                                        ->
                                                                        Error cancelledError
                                                                    | WorkspaceOutcome.Success _ -> Ok()
                                                                    | WorkspaceOutcome.Failure failure ->
                                                                        Error(failureError failure)
                                                            }
                                                        | None -> Task.FromResult(Ok())

                                                    match invalidation with
                                                    | Error error -> return Error error
                                                    | Ok() ->
                                                        let candidate =
                                                            { current with
                                                                Hydrated =
                                                                    evicted
                                                                    |> Option.map hydrated.Remove
                                                                    |> Option.defaultValue hydrated
                                                                Recency =
                                                                    evicted
                                                                    |> Option.map (fun item ->
                                                                        recency |> List.filter ((<>) item))
                                                                    |> Option.defaultValue recency }

                                                        return
                                                            Ok(
                                                                applyCandidate candidate
                                                                |> Option.map (
                                                                    WorkspaceStatePure.omitLazyBodyChanges before
                                                                )
                                                            )
                                    | _ -> return Ok None
                                }

                            let! hydrated = hydrate ()

                            match hydrated with
                            | Error error -> return Error error
                            | Ok delta ->
                                let placements = WorkspaceStatePure.placements insensitive current

                                let actualParent =
                                    placements |> Array.find (fun value -> value.Node.NodeId.Value = parentIdText)

                                let children =
                                    placements
                                    |> Seq.filter (fun value -> value.ParentId = Some actualParent.Node.NodeId)
                                    |> Seq.sortBy _.Index
                                    |> Seq.toArray

                                let pageSize =
                                    requestedPageSize
                                    |> Option.defaultValue 256
                                    |> min 4096
                                    |> min negotiatedPageSize

                                let page =
                                    children
                                    |> Array.skip (min pageOffset children.Length)
                                    |> Array.truncate pageSize

                                let next =
                                    if pageOffset + page.Length < children.Length then
                                        ContinuationTokens.create
                                            options.TokenSecret
                                            { WorkspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId.Value
                                              ParentId = actualParent.Node.NodeId.Value
                                              Offset = pageOffset + page.Length
                                              Revision = current.Revision }
                                        |> ContinuationToken.Create
                                        |> Some
                                    else
                                        None

                                return
                                    Ok
                                        { Revision = current.Revision
                                          ParentId = actualParent.Node.NodeId
                                          Nodes = page |> Seq.map _.Node |> ImmutableArray.CreateRange
                                          NextToken = next
                                          Delta = delta }
            finally
                gate.Release() |> ignore
        }

    member _.RefreshAsync(expectedRevision: int64 option, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                match expectedRevision with
                | Some expected when expected <> current.Revision ->
                    return Error(PublicProtocol.workspaceConflict current.Revision)
                | _ ->
                    try
                        do! services.RefreshAsync()
                        cancellationToken.ThrowIfCancellationRequested()
                        let! opened = services.OpenAsync target cancellationToken

                        match opened with
                        | WorkspaceOutcome.Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                            return Error cancelledError
                        | WorkspaceOutcome.Failure failure ->
                            let! reset = resetUnsafe failure.Diagnostic

                            return
                                Ok
                                    { Revision = reset.Revision.Value
                                      Reset = true
                                      Delta = None
                                      ResetEvent = Some reset
                                      Diagnostics = reset.Diagnostics }
                        | WorkspaceOutcome.Success workspace ->
                            let! hydrated = stageMaterialized current workspace cancellationToken

                            match hydrated with
                            | Error error when error.Code = "cancelled" -> return Error error
                            | Error _ ->
                                let! reset =
                                    resetUnsafe (
                                        uncertainty
                                            "workspace.refresh_unverified"
                                            "The workspace refresh could not be verified."
                                    )

                                return
                                    Ok
                                        { Revision = reset.Revision.Value
                                          Reset = true
                                          Delta = None
                                          ResetEvent = Some reset
                                          Diagnostics = reset.Diagnostics }
                            | Ok values ->
                                let recency = current.Recency |> List.filter values.ContainsKey

                                let delta =
                                    applyCandidate
                                        { Workspace = workspace
                                          Hydrated = values
                                          Recency = recency
                                          Revision = current.Revision
                                          NeedsRebase = false }

                                return
                                    Ok
                                        { Revision = current.Revision
                                          Reset = false
                                          Delta = delta
                                          ResetEvent = None
                                          Diagnostics = ImmutableArray<WorkspaceDiagnostic>.Empty }
                    with :? OperationCanceledException ->
                        return Error cancelledError
            finally
                gate.Release() |> ignore
        }

    member _.InvalidateAsync(paths: ImmutableArray<WorkspaceArtifactPath>, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                if current.NeedsRebase then
                    return WorkspaceInvalidationResult.None
                else
                    let! invalidated = services.InvalidateAsync paths cancellationToken

                    match invalidated with
                    | WorkspaceOutcome.Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                        return WorkspaceInvalidationResult.None
                    | WorkspaceOutcome.Failure failure ->
                        let! reset = resetUnsafe failure.Diagnostic
                        return WorkspaceInvalidationResult.Reset reset
                    | WorkspaceOutcome.Success MsBuildInvalidationKind.ToolsetSelection ->
                        let! reset =
                            resetUnsafe (
                                uncertainty
                                    "workspace.toolset_changed"
                                    "The selected SDK changed; request a fresh workspace graph."
                            )

                        return WorkspaceInvalidationResult.Reset reset
                    | WorkspaceOutcome.Success kind ->
                        let touchesSolution =
                            paths
                            |> Seq.exists (fun path ->
                                pathKey path.Value = pathKey current.Workspace.WorkspaceDescriptor.Path.Value
                                || pathKey path.Value = pathKey current.Workspace.BackingPath.Value)

                        if kind = MsBuildInvalidationKind.None && not touchesSolution then
                            return WorkspaceInvalidationResult.None
                        else
                            let! opened = services.OpenAsync target cancellationToken

                            match opened with
                            | WorkspaceOutcome.Failure failure when failure.Code = WorkspaceErrorCode.Cancelled ->
                                return WorkspaceInvalidationResult.None
                            | WorkspaceOutcome.Failure failure ->
                                let! reset = resetUnsafe failure.Diagnostic
                                return WorkspaceInvalidationResult.Reset reset
                            | WorkspaceOutcome.Success workspace ->
                                let! hydrated = stageMaterialized current workspace cancellationToken

                                match hydrated with
                                | Error error when error.Code = "cancelled" -> return WorkspaceInvalidationResult.None
                                | Error _ ->
                                    let! reset =
                                        resetUnsafe (
                                            uncertainty
                                                "workspace.watch_unverified"
                                                "The workspace change could not be verified."
                                        )

                                    return WorkspaceInvalidationResult.Reset reset
                                | Ok values ->
                                    let recency = current.Recency |> List.filter values.ContainsKey

                                    let delta =
                                        applyCandidate
                                            { Workspace = workspace
                                              Hydrated = values
                                              Recency = recency
                                              Revision = current.Revision
                                              NeedsRebase = false }

                                    return
                                        delta
                                        |> Option.map WorkspaceInvalidationResult.Delta
                                        |> Option.defaultValue WorkspaceInvalidationResult.None
            finally
                gate.Release() |> ignore
        }

    member this.InvalidateFromTransactionAsync
        (paths: seq<WorkspaceArtifactPath>, cancellationToken: CancellationToken)
        =
        task {
            let! invalidated = this.InvalidateAsync(ImmutableArray.CreateRange paths, cancellationToken)

            match invalidated with
            | WorkspaceInvalidationResult.Delta _
            | WorkspaceInvalidationResult.Reset _ -> return invalidated
            | WorkspaceInvalidationResult.None ->
                do! gate.WaitAsync cancellationToken

                try
                    if current.NeedsRebase then
                        return WorkspaceInvalidationResult.None
                    else
                        let nextRevision = current.Revision + 1L

                        current <- { current with Revision = nextRevision }

                        return
                            WorkspaceInvalidationResult.Delta
                                { WorkspaceId = current.Workspace.WorkspaceDescriptor.WorkspaceId
                                  BaseRevision = WorkspaceRevision.Create(current.Revision - 1L)
                                  NewRevision = WorkspaceRevision.Create nextRevision
                                  Changes = ImmutableArray<WorkspaceChange>.Empty
                                  Diagnostics = ImmutableArray<WorkspaceDiagnostic>.Empty }
                finally
                    gate.Release() |> ignore
        }

    member _.ResetAsync(diagnostic: WorkspaceDiagnostic, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                return! resetUnsafe diagnostic
            finally
                gate.Release() |> ignore
        }

    member _.ExportAsync(expectedRevision: int64, cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                if current.NeedsRebase || current.Revision <> expectedRevision then
                    return Error(PublicProtocol.workspaceConflict current.Revision)
                else
                    let mutable hydrated = Map.empty
                    let mutable failure = None

                    for project in current.Workspace.RootProjection.Projects do
                        if failure.IsNone && not project.IsFilteredOut then
                            if not (File.Exists project.Path.AbsolutePath.Value) then
                                failure <-
                                    Some(
                                        RpcErrors.create
                                            "not_found"
                                            $"Project '{project.Path.AbsolutePath.Value}' was not found."
                                            None
                                    )
                            else
                                let! evaluated = evaluate current.Workspace project cancellationToken

                                match evaluated with
                                | Ok snapshot ->
                                    hydrated <- hydrated.Add(pathKey project.Path.AbsolutePath.Value, snapshot)
                                | Error error -> failure <- Some error

                    match failure with
                    | Some error -> return Error error
                    | None ->
                        cancellationToken.ThrowIfCancellationRequested()

                        let exportData =
                            { current with
                                Hydrated = hydrated
                                Recency = hydrated |> Seq.map (fun (KeyValue(key, _)) -> key) |> Seq.toList }

                        return
                            Ok
                                { Descriptor = current.Workspace.WorkspaceDescriptor
                                  Revision = current.Revision
                                  Nodes =
                                    WorkspaceStatePure.placements insensitive exportData
                                    |> Seq.map _.Node
                                    |> ImmutableArray.CreateRange }
            finally
                gate.Release() |> ignore
        }

    member _.WatchPlanAsync(cancellationToken: CancellationToken) =
        task {
            do! gate.WaitAsync cancellationToken

            try
                return WorkspaceStatePure.watchPlan insensitive current
            finally
                gate.Release() |> ignore
        }

    member _.DisposeAsync() =
        task {
            do! gate.WaitAsync()

            try
                if not disposed then
                    disposed <- true
                    do! services.DisposeAsync()
            finally
                gate.Release() |> ignore
        }

    static member Create(target, workspace, services, options) =
        if
            options.HydrationLimit <= 0
            || isNull (box options.TokenSecret)
            || options.TokenSecret.Length < 16
        then
            invalidArg (nameof options) "Workspace options require a positive hydration limit and a token secret."

        WorkspaceState(
            target,
            services,
            options,
            { Workspace = workspace
              Hydrated = Map.empty
              Recency = []
              Revision = workspace.WorkspaceDescriptor.WorkspaceRevision.Value
              NeedsRebase = false }
        )

    static member CreateProduction(target, workspace) =
        let evaluator = new MsBuildEvaluationClient()

        let services =
            { OpenAsync = fun path cancellationToken -> SolutionStore.OpenAsync(path, cancellationToken)
              EvaluateAsync =
                fun project workspace cancellationToken ->
                    evaluator.EvaluateAsync(project, workspace, cancellationToken)
              InvalidateAsync = fun paths cancellationToken -> evaluator.InvalidateAsync(paths, cancellationToken)
              RefreshAsync = evaluator.RefreshAsync
              DisposeAsync = fun () -> evaluator.DisposeAsync().AsTask() }

        WorkspaceState.Create(
            target,
            workspace,
            services,
            { HydrationLimit = 32
              TokenSecret = RandomNumberGenerator.GetBytes 32 }
        )
