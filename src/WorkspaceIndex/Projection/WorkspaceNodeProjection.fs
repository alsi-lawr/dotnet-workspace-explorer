namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Text
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Workspaces

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

    let workspaceRoot (descriptor: WorkspaceDescriptor) =
        let name =
            Path.GetFileNameWithoutExtension descriptor.Path.Value
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue descriptor.Path.Value

        WorkspaceNode.Create(
            descriptor,
            WorkspaceNodeKind.Workspace,
            WorkspaceNodeIdentity.Create "workspace-root",
            name,
            if descriptor.IsReadOnly then
                WorkspaceCapabilityProfile.ReadOnly
            else
                WorkspaceCapabilityProfile.Full
        )

    let projectBodyEntries
        insensitive
        (workspace: SolutionWorkspace)
        (project: SolutionProject)
        hydrated
        projectNode
        =
        SemanticProjectProjection.nodes insensitive workspace project hydrated.Snapshot projectNode

    let projectBody insensitive workspace project hydrated projectNode =
        projectBodyEntries insensitive workspace project hydrated projectNode
        |> Seq.map _.PlacementNode
        |> ImmutableArray.CreateRange

    let exportStaticNodes (workspace: SolutionWorkspace) =
        let root = workspace.Contents

        seq {
            yield workspaceRoot workspace.Descriptor
            yield! root.Folders |> Seq.map _.Node
            yield! root.Items |> Seq.map _.Node
        }

    let exportProjectNodes
        insensitive
        (workspace: SolutionWorkspace)
        (project: SolutionProject)
        (hydrated: EvaluatedWorkspaceProject option)
        =
        match hydrated with
        | None -> [| project.Node |]
        | Some value ->
            let header =
                WorkspaceNode.CreateWithLoadState(
                    workspace.Descriptor,
                    WorkspaceNodeKind.Project,
                    project.Node.Identity,
                    project.Node.Name,
                    value.Snapshot.CapabilityProfile,
                    WorkspaceNodeLoadState.Hydrated
                )

            let body = projectBodyEntries insensitive workspace project value header
            let nodes = Array.zeroCreate<WorkspaceNode> (body.Length + 1)
            nodes[0] <- header

            for index in 0 .. body.Length - 1 do
                nodes[index + 1] <- body[index].PlacementNode

            nodes

    let snapshotSemanticValues
        (_descriptor: WorkspaceDescriptor)
        (hydrated: EvaluatedWorkspaceProject)
        =
        seq {
            let snapshot = hydrated.Snapshot
            yield snapshot.ProjectPath.Value

            for dimension in snapshot.Dimensions do
                yield
                    dimension.TargetFramework
                    |> Option.ofNullable
                    |> Option.map _.Value
                    |> Option.defaultValue "outer"

                for property in dimension.Properties do
                    yield $"property:{property.Name}:{property.Value}"

                for item in dimension.Items do
                    yield
                        $"item:{item.Ordinal}:{item.ItemType}:{item.EvaluatedInclude}:"
                        + (item.ResolvedPath
                           |> Option.ofObj
                           |> Option.map _.Value
                           |> Option.defaultValue "")

                    for metadata in item.Metadata do
                        yield $"metadata:{metadata.Name}:{metadata.Value}"

                for reference in dimension.ProjectReferences do
                    yield
                        $"project-reference:{reference.Include}:"
                        + (reference.ResolvedPath
                           |> Option.ofObj
                           |> Option.map _.Value
                           |> Option.defaultValue "")

                for reference in dimension.References do
                    yield
                        $"reference:{reference.Include}:"
                        + (reference.ResolvedPath
                           |> Option.ofObj
                           |> Option.map _.Value
                           |> Option.defaultValue "")

                for package in dimension.Packages do
                    yield $"package:{package.Id}:{Option.ofObj package.Version}"

                for analyzer in dimension.Analyzers do
                    yield
                        $"analyzer:{analyzer.Include}:"
                        + (analyzer.ResolvedPath
                           |> Option.ofObj
                           |> Option.map _.Value
                           |> Option.defaultValue "")

            for capability in snapshot.Capabilities do
                yield $"capability:{capability.Value}"

            for path in snapshot.Imports do
                yield $"import:{path.Value}"

            for path in snapshot.WatchInputs do
                yield $"watch:{path.Value}"

            for path in snapshot.GlobRoots do
                yield $"glob:{path.Value}"

            for diagnostic in snapshot.Diagnostics do
                yield $"diagnostic:{diagnostic.Code.Value}:{diagnostic.Message}"
        }

    let sameSnapshot
        (descriptor: WorkspaceDescriptor)
        (left: EvaluatedWorkspaceProject)
        (right: EvaluatedWorkspaceProject)
        =
        canonicalHash (snapshotSemanticValues descriptor left) = canonicalHash (
            snapshotSemanticValues descriptor right
        )
