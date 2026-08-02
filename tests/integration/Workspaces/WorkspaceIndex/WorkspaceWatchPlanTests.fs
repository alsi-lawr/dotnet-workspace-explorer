namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

open System
open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type WorkspaceWatchPlanTests() =
    [<Fact>]
    member _.``a project hydration guard covers the project tree and ancestor evaluation inputs before evaluation starts``
        ()
        =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "workspace-hydration-watch-guard"

        try
            let projectDirectory = Path.Combine(directory, "src", "Application")
            Directory.CreateDirectory projectDirectory |> ignore
            let project = Path.Combine(projectDirectory, "Application.csproj")
            File.WriteAllText(project, "<Project />")

            let plan = WorkspaceArtifactPath.Create project |> WorkspaceWatchPlan.hydrationGuard

            (plan
             |> Seq.exists (fun watch ->
                 watch.Directory = projectDirectory
                 && watch.IncludeSubdirectories
                 && watch.Filters.Contains "*"))
            |> should equal true

            let watchesExact (path: string) =
                match
                    Path.GetDirectoryName path |> Option.ofObj,
                    Path.GetFileName path |> Option.ofObj
                with
                | Some directory, Some name ->
                    plan
                    |> Seq.exists (fun watch ->
                        watch.Directory = directory
                        && not watch.IncludeSubdirectories
                        && watch.Filters.Contains name)
                | _ -> false

            (watchesExact project) |> should equal true

            for name in
                [ "Directory.Build.props"
                  "Directory.Build.targets"
                  "Directory.Packages.props"
                  "global.json" ] do
                (watchesExact (Path.Combine(directory, name))) |> should equal true
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``recursive project watching ignores generated descendants without ignoring a workspace beneath an ancestor named bin``
        ()
        =
        let comparer =
            if OperatingSystem.IsWindows() then
                StringComparer.OrdinalIgnoreCase
            else
                StringComparer.Ordinal

        let root = Path.Combine(Path.GetTempPath(), "bin", "workspace")

        (WorkspaceWatchPlan.ignoresRecursiveHint
            comparer
            root
            (Path.Combine(root, "src", "Feature.cs")))
        |> should equal false

        for generated in [ ".agent-workspace"; ".git"; "bin"; "node_modules"; "obj" ] do
            (WorkspaceWatchPlan.ignoresRecursiveHint
                comparer
                root
                (Path.Combine(root, "src", generated, "Feature.cs")))
            |> should equal true

        (WorkspaceWatchPlan.ignoresRecursiveHint
            comparer
            root
            (Path.Combine(root, "src", "objective", "Feature.cs")))
        |> should equal false
