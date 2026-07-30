namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Immutable
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces
open Xunit

[<Collection("Workspace scenarios")>]
type SemanticWorkspaceTreeTests() =
    [<Fact>]
    member _.``should project the semantic solution and evaluated project hierarchy``() =
        let directory = WorkspaceRpcScenario.temporaryDirectory "semantic-tree"

        try
            let solution = Path.Combine(directory, "Semantic.slnx")
            let appPath = Path.Combine(directory, "App.fsproj")
            let libraryPath = Path.Combine(directory, "Library.csproj")
            let visualBasicPath = Path.Combine(directory, "VisualBasic.vbproj")
            let solutionItem = Path.Combine(directory, "Directory.Build.props")
            let model = SolutionModel()
            let folder = model.AddFolder "/src/"
            folder.AddFile(Path.GetFileName solutionItem)
            let appModel = model.AddProject("App.fsproj", "App", folder)
            let libraryModel = model.AddProject("Library.csproj", "Library", null)
            model.AddProject("VisualBasic.vbproj", "VisualBasic", null) |> ignore
            appModel.AddDependency libraryModel
            model.AddBuildType "Debug"
            model.AddPlatform "Any CPU"

            for path in [ appPath; libraryPath; visualBasicPath ] do
                WorkspaceRpcScenario.writeProject path

            for relative in
                [ "A/First.fs"
                  "A/Third.fs"
                  "B/Second.fs"
                  "Library/Nested/Library.cs"
                  "VisualBasic/Nested/VisualBasic.vb" ] do
                let file = Path.Combine(directory, relative)
                Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                File.WriteAllText(file, String.Empty)

            File.WriteAllText(solutionItem, "<Project />")
            WorkspaceRpcScenario.save solution model

            let workspace =
                match SolutionWorkspaceReader.OpenAsync(solution).Result with
                | Success value -> value
                | Failure failure -> failwithf "Could not open semantic fixture: %A" failure

            let app =
                workspace.Contents.Projects
                |> Seq.find (fun project -> project.Node.Name = "App")

            let path relative =
                WorkspaceArtifactPath.Create(Path.Combine(directory, relative))

            let item ordinal itemType relative metadata =
                EvaluatedItem(
                    itemType,
                    relative,
                    path relative,
                    ImmutableArray.CreateRange metadata,
                    ordinal
                )

            let outerItems =
                ImmutableArray.CreateRange
                    [ item 0 "Compile" "A/First.fs" []
                      item 1 "Compile" "B/Second.fs" []
                      item 2 "Compile" "A/Third.fs" []
                      item 3 "Content" "Root.txt" []
                      item 4 "Compile" "Generated.fs" [ EvaluatedMetadata("AutoGen", "true") ]
                      item 5 "Compile" "obj/Generated.fs" []
                      item 6 "None" "artifacts/Generated.txt" []
                      item 7 "EmbeddedResource" "bin/Generated.resources" []
                      item 8 "Compile" "custom-intermediate/Generated.fs" []
                      item 9 "Custom" "Ignored.custom" [] ]

            let properties =
                ImmutableArray.CreateRange
                    [ EvaluatedProperty("BaseIntermediateOutputPath", "obj/")
                      EvaluatedProperty("IntermediateOutputPath", "custom-intermediate/")
                      EvaluatedProperty("BaseOutputPath", "bin/")
                      EvaluatedProperty("OutputPath", "artifacts/") ]

            let outerDimension items =
                ProjectEvaluationDimension(
                    Nullable(),
                    properties,
                    items,
                    ImmutableArray.Create(
                        EvaluatedReference("Library.csproj", path "Library.csproj")
                    ),
                    ImmutableArray.Create(EvaluatedReference("System.Xml", null)),
                    ImmutableArray.CreateRange
                        [ EvaluatedPackage("Newtonsoft.Json", "13.0.3")
                          EvaluatedPackage("Newtonsoft.Json", "14.0.1") ],
                    ImmutableArray.Create(EvaluatedReference("Analyzer.dll", null))
                )

            let framework =
                ProjectEvaluationDimension(
                    Nullable(EvaluatedTargetFramework("net10.0")),
                    properties,
                    ImmutableArray.CreateRange
                        [ item 0 "Compile" "A/First.fs" []
                          item 1 "Compile" "B/Second.fs" []
                          item 2 "Compile" "A/Third.fs" [] ],
                    ImmutableArray.Create(
                        EvaluatedReference("Library.csproj", path "Library.csproj")
                    ),
                    ImmutableArray.Create(EvaluatedReference("System.Xml", null)),
                    ImmutableArray.Create(EvaluatedPackage("newtonsoft.json", "13.0.3")),
                    ImmutableArray.Create(EvaluatedReference("Analyzer.dll", null))
                )

            let snapshot items =
                ProjectEvaluationSnapshot(
                    path "App.fsproj",
                    ImmutableArray.Create(outerDimension items, framework),
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    WorkspaceCapabilityProfile.Full,
                    ImmutableArray.Create(WorkspaceCapabilityId.Read, WorkspaceCapabilityId.Write),
                    ImmutableArray<WorkspaceDiagnostic>.Empty
                )

            let leafSnapshot projectFile relative =
                let dimension =
                    ProjectEvaluationDimension(
                        Nullable(),
                        ImmutableArray<EvaluatedProperty>.Empty,
                        ImmutableArray.Create(item 0 "Compile" relative []),
                        ImmutableArray<EvaluatedReference>.Empty,
                        ImmutableArray<EvaluatedReference>.Empty,
                        ImmutableArray<EvaluatedPackage>.Empty,
                        ImmutableArray<EvaluatedReference>.Empty
                    )

                ProjectEvaluationSnapshot(
                    path projectFile,
                    ImmutableArray.Create dimension,
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    WorkspaceCapabilityProfile.Full,
                    ImmutableArray.Create(WorkspaceCapabilityId.Read, WorkspaceCapabilityId.Write),
                    ImmutableArray<WorkspaceDiagnostic>.Empty
                )

            let indexed snapshot revision =
                { Workspace = workspace
                  Hydrated =
                    Map.ofList
                        [ appPath,
                          { Snapshot = snapshot
                            DeclaredProperties = ImmutableArray<ExploredProjectProperty>.Empty }
                          libraryPath,
                          { Snapshot = leafSnapshot "Library.csproj" "Library/Nested/Library.cs"
                            DeclaredProperties = ImmutableArray<ExploredProjectProperty>.Empty }
                          visualBasicPath,
                          { Snapshot =
                              leafSnapshot "VisualBasic.vbproj" "VisualBasic/Nested/VisualBasic.vb"
                            DeclaredProperties = ImmutableArray<ExploredProjectProperty>.Empty } ]
                  Recency = [ appPath ]
                  Revision = revision
                  NeedsRebase = false }

            let placements =
                WorkspaceIndexDiff.placements false (indexed (snapshot outerItems) 0L)

            let roots = placements |> Array.filter _.ParentWorkspaceNodeId.IsNone
            let root = Assert.Single roots
            Assert.Equal(WorkspaceNodeKind.Workspace, root.Node.Kind)
            Assert.Equal("Semantic", root.Node.Name)

            let children parentId =
                placements
                |> Array.filter (fun placement -> placement.ParentWorkspaceNodeId = Some parentId)
                |> Array.sortBy _.Index

            let rootChildren = children root.Node.Id

            Assert.Equal<string array>(
                [| "src"; "Library"; "VisualBasic" |],
                rootChildren |> Array.map _.Node.Name
            )

            Assert.DoesNotContain(
                rootChildren,
                fun node ->
                    node.Node.Kind = WorkspaceNodeKind.Configuration
                    || node.Node.Kind = WorkspaceNodeKind.Platform
            )

            let src = rootChildren |> Array.find (fun value -> value.Node.Name = "src")
            let srcChildren = children src.Node.Id

            Assert.Equal<string array>(
                [| "Directory.Build.props"; "App" |],
                srcChildren |> Array.map _.Node.Name
            )

            let appNode =
                srcChildren
                |> Array.find (fun value -> value.Node.Kind = WorkspaceNodeKind.Project)

            let appChildren = children appNode.Node.Id

            Assert.Equal<string array>(
                [| "Dependencies"; "A"; "B"; "Root.txt" |],
                appChildren |> Array.map _.Node.Name
            )

            let folderA = appChildren |> Array.find (fun value -> value.Node.Name = "A")
            let folderB = appChildren |> Array.find (fun value -> value.Node.Name = "B")

            Assert.Equal<string array>(
                [| "First.fs"; "Third.fs" |],
                children folderA.Node.Id |> Array.map _.Node.Name
            )

            Assert.Equal<string array>(
                [| "Second.fs" |],
                children folderB.Node.Id |> Array.map _.Node.Name
            )

            let dependencies =
                appChildren |> Array.find (fun value -> value.Node.Name = "Dependencies")

            Assert.Equal<string array>(
                [| "Library"
                   "Newtonsoft.Json (13.0.3)"
                   "Newtonsoft.Json (14.0.1)"
                   "System.Xml"
                   "Analyzer.dll" |],
                children dependencies.Node.Id |> Array.map _.Node.Name
            )

            Assert.DoesNotContain(
                placements,
                fun placement ->
                    [ "Generated.fs"; "Generated.txt"; "Generated.resources"; "Ignored.custom" ]
                    |> List.contains placement.Node.Name
            )

            let exported =
                let hydrated = (indexed (snapshot outerItems) 0L).Hydrated

                seq {
                    yield! WorkspaceIndexPure.exportStaticNodes workspace

                    for project in workspace.Contents.Projects do
                        let projectSnapshot =
                            hydrated |> Map.tryFind project.Path.AbsolutePath.Value

                        yield!
                            WorkspaceIndexPure.exportProjectNodes
                                false
                                workspace
                                project
                                projectSnapshot
                }
                |> Seq.map (fun node -> node.Id.Value, int node.Kind)
                |> Set.ofSeq

            let indexedNodes =
                placements
                |> Seq.map (fun value -> value.Node.Id.Value, int value.Node.Kind)
                |> Set.ofSeq

            if indexedNodes <> exported then
                failwith "Flat export did not contain the indexed semantic node set."

            Assert.True(
                WorkspaceIndexDiff.diff workspace.Descriptor.Id 0L placements placements
                |> Option.isNone,
                "A no-op projection should preserve semantic IDs and ordering."
            )

            let targetFrameworkDimension targetFramework items =
                ProjectEvaluationDimension(
                    Nullable(EvaluatedTargetFramework(targetFramework)),
                    properties,
                    items,
                    ImmutableArray<EvaluatedReference>.Empty,
                    ImmutableArray<EvaluatedReference>.Empty,
                    ImmutableArray<EvaluatedPackage>.Empty,
                    ImmutableArray<EvaluatedReference>.Empty
                )

            let fallbackSnapshot =
                ProjectEvaluationSnapshot(
                    path "App.fsproj",
                    ImmutableArray.Create(
                        targetFrameworkDimension
                            "net9.0"
                            (ImmutableArray.Create(item 0 "Compile" "A/Shared.fs" [])),
                        targetFrameworkDimension
                            "net8.0"
                            (ImmutableArray.CreateRange
                                [ item 1 "Compile" "A/Preferred.fs" []
                                  item 2 "Compile" "A/Shared.fs" [] ])
                    ),
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    ImmutableArray<WorkspaceArtifactPath>.Empty,
                    WorkspaceCapabilityProfile.Full,
                    ImmutableArray.Create(WorkspaceCapabilityId.Read, WorkspaceCapabilityId.Write),
                    ImmutableArray<WorkspaceDiagnostic>.Empty
                )

            let fallbackPlacements =
                WorkspaceIndexDiff.placements false (indexed fallbackSnapshot 0L)

            let fallbackApp =
                fallbackPlacements
                |> Array.find (fun placement ->
                    placement.Node.Kind = WorkspaceNodeKind.Project && placement.Node.Name = "App")

            let fallbackFolder =
                fallbackPlacements
                |> Array.find (fun placement ->
                    placement.ParentWorkspaceNodeId = Some fallbackApp.Node.Id
                    && placement.Node.Name = "A")

            Assert.Equal<string array>(
                [| "Preferred.fs"; "Shared.fs" |],
                fallbackPlacements
                |> Array.filter (fun placement ->
                    placement.ParentWorkspaceNodeId = Some fallbackFolder.Node.Id)
                |> Array.sortBy _.Index
                |> Array.map _.Node.Name
            )

            let changedItems =
                outerItems
                |> Seq.map (fun value ->
                    if value.EvaluatedInclude = "A/Third.fs" then
                        item value.Ordinal "Compile" "A/Fourth.fs" []
                    else
                        value)
                |> ImmutableArray.CreateRange

            let changed =
                WorkspaceIndexDiff.placements false (indexed (snapshot changedItems) 0L)

            let delta =
                WorkspaceIndexDiff.diff workspace.Descriptor.Id 0L placements changed
                |> Option.defaultWith (fun () -> failwith "Expected a semantic file delta.")

            Assert.Contains(
                delta.Changes,
                fun change ->
                    match change with
                    | Added(node, parent, _) ->
                        node.Kind = WorkspaceNodeKind.ProjectFile
                        && node.Name = "Fourth.fs"
                        && parent = Some folderA.Node.Id
                    | _ -> false
            )
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
