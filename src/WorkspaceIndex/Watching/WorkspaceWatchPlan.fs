namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.Collections.Immutable
open System.IO

module internal WorkspaceWatchPlan =
    let private pathIdentity insensitive (path: string) =
        if insensitive then path.ToUpperInvariant() else path

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
                      IncludeSubdirectories = false
                      Kind = WorkspaceWatchKind.ExactFile })

        exact data.Workspace.Descriptor.Path.Value
        exact data.Workspace.SolutionPath.Value

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
                          Kind = WorkspaceWatchKind.RecursiveGlob }

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
