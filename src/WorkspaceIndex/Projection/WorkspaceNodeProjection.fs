namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Text
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation

module internal WorkspaceIndexPure =
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

    let private canonicalItemInclude (value: string) = value.Replace('\\', '/')

    let private dimensionName (dimension: ProjectEvaluationDimension) =
        if dimension.TargetFramework.HasValue then
            dimension.TargetFramework.Value.Value
        else
            "outer"

    let private bounded limit (value: string) =
        if value.Length <= limit then
            value
        else
            $"{value[.. limit - 2]}…"

    let private evaluatedContributions (snapshot: ProjectEvaluationSnapshot) =
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
                    let includePath = canonicalItemInclude item.EvaluatedInclude

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
                        { Logical = [ "item"; item.ItemType; includePath ]
                          Content = resolved :: metadata
                          Display =
                            if String.IsNullOrEmpty details then
                                $"{item.ItemType}: {includePath}"
                            else
                                $"{item.ItemType}: {includePath} ({details})"
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
        (hydrated: EvaluatedWorkspaceProject)
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

    let contributions (descriptor: WorkspaceDescriptor) (hydrated: EvaluatedWorkspaceProject) =
        Seq.append
            (evaluatedContributions hydrated.Snapshot)
            (declaredContributions descriptor hydrated)
        |> Seq.toArray

    let projectBodyEntries (descriptor: WorkspaceDescriptor) (project: SolutionProject) hydrated =
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
                        WorkspaceNodeIdentity.Create
                            $"project-body:{project.Node.Identity.Value}:{identity}",
                        boundedDisplay,
                        hydrated.Snapshot.CapabilityProfile
                    )

                let placement =
                    if showDimension then
                        representative.Logical @ [ dimensions; node.Id.Value ]
                    else
                        representative.Logical @ [ node.Id.Value ]

                placement, node))
        |> Seq.sortBy fst
        |> ImmutableArray.CreateRange

    let projectBody (descriptor: WorkspaceDescriptor) project hydrated =
        projectBodyEntries descriptor project hydrated
        |> Seq.map snd
        |> ImmutableArray.CreateRange

    let exportStaticNodes (workspace: SolutionWorkspace) =
        let root = workspace.Contents

        seq {
            yield! root.BuildTypes
            yield! root.Dependencies |> Seq.map _.Node
            yield! root.Folders |> Seq.map _.Node
            yield! root.Platforms
            yield! root.Items |> Seq.map _.Node
        }

    let exportProjectNodes
        (descriptor: WorkspaceDescriptor)
        (project: SolutionProject)
        (hydrated: EvaluatedWorkspaceProject option)
        =
        match hydrated with
        | None -> [| project.Node |]
        | Some value ->
            let header =
                WorkspaceNode.CreateWithLoadState(
                    descriptor,
                    WorkspaceNodeKind.Project,
                    project.Node.Identity,
                    project.Node.Name,
                    value.Snapshot.CapabilityProfile,
                    WorkspaceNodeLoadState.Hydrated
                )

            let body = projectBodyEntries descriptor project value
            let nodes = Array.zeroCreate<WorkspaceNode> (body.Length + 1)
            nodes[0] <- header

            for index in 0 .. body.Length - 1 do
                nodes[index + 1] <- snd body[index]

            nodes

    let snapshotSemanticValues
        (descriptor: WorkspaceDescriptor)
        (hydrated: EvaluatedWorkspaceProject)
        =
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
                yield $"diagnostic-severity:{int diagnostic.Severity}"
                yield $"diagnostic-code:{diagnostic.Code.Value}"
                yield $"diagnostic-message:{diagnostic.Message}"
                yield $"diagnostic-retryable:{diagnostic.Retryable}"

                yield
                    $"diagnostic-path:{diagnostic.ArtifactPath
                                       |> Option.map _.Value
                                       |> Option.defaultValue String.Empty}"

                yield
                    diagnostic.Location
                    |> Option.map (fun location ->
                        $"diagnostic-location:{location.Line}:{location.Column}")
                    |> Option.defaultValue "diagnostic-location:"
        }

    let sameSnapshot
        (descriptor: WorkspaceDescriptor)
        (left: EvaluatedWorkspaceProject)
        (right: EvaluatedWorkspaceProject)
        =
        canonicalHash (snapshotSemanticValues descriptor left) = canonicalHash (
            snapshotSemanticValues descriptor right
        )
