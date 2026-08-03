namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.Collections.Immutable
open System.IO
open Dotnet.WorkspaceExplorer.Workspaces

module internal WorkspaceWatchPlan =
    let private generatedDirectoryNames =
        [ ".agent-workspace"; ".git"; "bin"; "node_modules"; "obj" ]

    let private ancestorInputNames =
        [ "Directory.Build.props"
          "Directory.Build.targets"
          "Directory.Packages.props"
          "global.json" ]

    let private pathIdentity insensitive (path: string) =
        if insensitive then path.ToUpperInvariant() else path

    let ignoresRecursiveHint (comparer: StringComparer) root candidate =
        let relative = Path.GetRelativePath(root, candidate)

        not (Path.IsPathRooted relative)
        && relative <> "."
        && relative <> ".."
        && not (relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        && not (
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )
        && relative.Split(
            [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
            StringSplitOptions.RemoveEmptyEntries
           )
           |> Seq.exists (fun segment ->
               generatedDirectoryNames
               |> Seq.exists (fun generated -> comparer.Equals(segment, generated)))

    let hydrationGuard (projectPath: WorkspaceArtifactPath) =
        let specs = ResizeArray<WorkspaceWatch>()

        let exact (path: string) =
            Path.GetDirectoryName path
            |> Option.ofObj
            |> Option.iter (fun directory ->
                specs.Add
                    { Directory = Path.GetFullPath directory
                      Filters =
                        Path.GetFileName path
                        |> Option.ofObj
                        |> Option.defaultValue "*"
                        |> ImmutableArray.Create
                      Kind = WorkspaceWatchKind.ExactFile })

        exact projectPath.Value

        Path.GetDirectoryName projectPath.Value
        |> Option.ofObj
        |> Option.iter (fun projectDirectory ->
            specs.Add
                { Directory = Path.GetFullPath projectDirectory
                  Filters = ImmutableArray.Create "*"
                  Kind = WorkspaceWatchKind.RecursiveGlob }

            let mutable directory = Some(DirectoryInfo projectDirectory)

            while directory.IsSome do
                let current = directory.Value

                for name in ancestorInputNames do
                    exact (Path.Combine(current.FullName, name))

                directory <- current.Parent |> Option.ofObj)

        specs
        |> Seq.filter (fun value -> Directory.Exists value.Directory && not value.Filters.IsEmpty)
        |> ImmutableArray.CreateRange

    let watchPlan insensitive (data: IndexedWorkspace) =
        let specs = ResizeArray<WorkspaceWatch>()

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
                      Kind = WorkspaceWatchKind.ExactFile })

        exact data.Workspace.Descriptor.Path.Value
        exact data.Workspace.SolutionPath.Value

        for KeyValue(_, hydrated) in data.Hydrated do
            let snapshot = hydrated.Snapshot
            let toolchainRoots = ProjectInputClassification.toolchainRoots snapshot
            exact snapshot.ProjectPath.Value

            for path in Seq.append snapshot.Imports snapshot.WatchInputs do
                if not (ProjectInputClassification.isToolchainPath toolchainRoots path.Value) then
                    exact path.Value

            let projectDirectory =
                Path.GetDirectoryName snapshot.ProjectPath.Value |> Option.ofObj

            projectDirectory
            |> Option.iter (fun projectDirectory ->
                let mutable directory = Some(DirectoryInfo projectDirectory)

                while directory.IsSome do
                    let current = directory.Value

                    for name in ancestorInputNames do
                        exact (Path.Combine(current.FullName, name))

                    directory <- current.Parent |> Option.ofObj)

            for root in snapshot.GlobRoots do
                if
                    not (ProjectInputClassification.isToolchainPath toolchainRoots root.Value)
                    && Directory.Exists root.Value
                then
                    specs.Add
                        { Directory = root.Value
                          Filters = ImmutableArray.Create "*"
                          Kind = WorkspaceWatchKind.RecursiveGlob }

            for dimension in snapshot.Dimensions do
                for item in dimension.Items do
                    item.ResolvedPath
                    |> Option.ofObj
                    |> Option.iter (fun path ->
                        if
                            not (
                                ProjectInputClassification.isToolchainPath
                                    toolchainRoots
                                    path.Value
                            )
                        then
                            let projectRoot =
                                Path.GetDirectoryName snapshot.ProjectPath.Value |> Option.ofObj

                            let relative =
                                projectRoot
                                |> Option.map (fun value ->
                                    Path.GetRelativePath(value, path.Value))
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
        |> Seq.groupBy (fun value -> pathIdentity insensitive value.Directory, value.Kind)
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
                compare left.Kind right.Kind)
        |> ImmutableArray.CreateRange
