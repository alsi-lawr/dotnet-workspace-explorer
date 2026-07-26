namespace Dotnet.CLI.Plus

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Text
open System.Xml.Linq
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.MSBuild
open Dotnet.CLI.Plus.Solution

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
        |> Seq.filter (fun path ->
            File.Exists path.Value && not (generated projectDirectory path.Value))
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
        | :? Xml.XmlException -> Error "Project declarations could not be read."

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
        | :? Xml.XmlException -> Error "Project declarations could not be read."

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

    let private declaredContributions
        (descriptor: WorkspaceDescriptor)
        (hydrated: HydratedProject)
        =
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
                    value
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/')

            let propertyValue = bounded 96 property.Value
            let scopeValue = bounded 96 scope
            let conditionValue = bounded 96 condition

            { Logical = [ "declared-property"; property.Name; property.Scope.Value; condition ]
              Content = [ property.Scope.Value; condition; property.Value ]
              Display =
                $"Declared {property.Name} = {propertyValue} "
                + $"[scope: {scopeValue}; condition: {conditionValue}]"
              Dimension = "declared" })
        |> Seq.toArray

    let contributions (descriptor: WorkspaceDescriptor) (hydrated: HydratedProject) =
        Seq.append
            (evaluatedContributions hydrated.Snapshot)
            (declaredContributions descriptor hydrated)
        |> Seq.toArray

    let projectBodyEntries
        (descriptor: WorkspaceDescriptor)
        (project: SolutionProjectProjection)
        hydrated
        =
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
                        NodeSemanticIdentity.Create
                            $"project-body:{project.Node.Identity.Value}:{identity}",
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
                    |> Option.map (fun location ->
                        $"diagnostic-location:{location.Line}:{location.Column}")
                    |> Option.defaultValue "diagnostic-location:"
        }

    let sameSnapshot
        (descriptor: WorkspaceDescriptor)
        (left: HydratedProject)
        (right: HydratedProject)
        =
        canonicalHash (snapshotSemanticValues descriptor left) = canonicalHash (
            snapshotSemanticValues descriptor right
        )

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
            raw.Add(
                PlacementKey [ "folder"; folder.Path ],
                folder.Node,
                folderParent folder.ParentPath
            )

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
                for placement, child in
                    projectBodyEntries data.Workspace.WorkspaceDescriptor project hydrated do
                    raw.Add(
                        PlacementKey("project-body" :: key :: placement),
                        child,
                        Some node.NodeId
                    )
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
        |> Seq.groupBy (fun (_, _, parentId) ->
            parentId |> Option.map _.Value |> Option.defaultValue String.Empty)
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
                            |> Option.map (fun parentId ->
                                1 + depth (visiting |> Set.add nodeId.Value) parentId)
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
                -oldDepth value.Node.NodeId,
                value.ParentId |> Option.map _.Value,
                -value.Index,
                key)
            |> Seq.map (fun (_, value) -> Removed(value.Node.NodeId, value.ParentId, value.Index))

        let replacements, moves, updates =
            oldByKey
            |> Seq.choose (fun (KeyValue(key, oldValue)) ->
                newByKey.TryFind key |> Option.map (fun newValue -> key, oldValue, newValue))
            |> Seq.fold
                (fun (replaceValues, moveValues, updateValues) (key, oldValue, newValue) ->
                    if oldValue.Node.NodeId <> newValue.Node.NodeId then
                        (key,
                         newValue,
                         Replaced(
                             oldValue.Node.NodeId,
                             newValue.Node,
                             newValue.ParentId,
                             newValue.Index
                         ))
                        :: replaceValues,
                        moveValues,
                        updateValues
                    else
                        let nextMoves =
                            if oldValue.ParentId <> newValue.ParentId then
                                (key,
                                 newValue,
                                 Moved(
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
                                 Updated(newValue.Node, newValue.ParentId, newValue.Index))
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
            |> Seq.map (fun (_, value) -> Added(value.Node, value.ParentId, value.Index))

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
            | Added(node, _, _)
            | Updated(node, _, _)
            | Replaced(_, node, _, _) -> node.NodeKind = WorkspaceNodeKind.ProjectItem
            | Removed(nodeId, _, _) ->
                match oldKinds.TryGetValue nodeId with
                | true, kind -> kind = WorkspaceNodeKind.ProjectItem
                | _ -> false
            | Moved(nodeId, _, _, _, _) ->
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
                        let projectRoot =
                            Path.GetDirectoryName snapshot.ProjectPath.Value |> Option.ofObj

                        let relative =
                            projectRoot
                            |> Option.map (fun value -> Path.GetRelativePath(value, path.Value))
                            |> Option.defaultValue path.Value

                        if
                            Path.IsPathRooted relative
                            || relative = ".."
                            || relative.StartsWith(
                                $"..{Path.DirectorySeparatorChar}",
                                StringComparison.Ordinal
                            )
                            || relative.StartsWith(
                                $"..{Path.AltDirectorySeparatorChar}",
                                StringComparison.Ordinal
                            )
                        then
                            exact path.Value)

        specs
        |> Seq.filter (fun value -> Directory.Exists value.Directory && not value.Filters.IsEmpty)
        |> Seq.groupBy (fun value ->
            pathIdentity insensitive value.Directory, value.IncludeSubdirectories, value.Kind)
        |> Seq.map (fun (_, values) ->
            let first = Seq.head values

            { first with
                Filters =
                    values
                    |> Seq.collect _.Filters
                    |> Seq.distinctBy (fun value ->
                        if insensitive then value.ToUpperInvariant() else value)
                    |> Seq.sortWith (fun left right -> comparer.Compare(left, right))
                    |> ImmutableArray.CreateRange })
        |> Seq.sortWith (fun left right ->
            let directory = comparer.Compare(left.Directory, right.Directory)

            if directory <> 0 then
                directory
            else
                let includeChildren =
                    compare left.IncludeSubdirectories right.IncludeSubdirectories

                if includeChildren <> 0 then
                    includeChildren
                else
                    compare left.Kind right.Kind)
        |> ImmutableArray.CreateRange
