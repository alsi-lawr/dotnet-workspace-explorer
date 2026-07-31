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
open FsUnit.Xunit
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
            let testAssemblyPath = typeof<SemanticWorkspaceTreeTests>.Assembly.Location
            let packageRoot = Path.Combine(directory, "packages")

            Directory.CreateDirectory(Path.Combine(packageRoot, "newtonsoft.json", "13.0.3"))
            |> ignore

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
                      item 9 "Custom" "Ignored.custom" []
                      item
                          10
                          "Reference"
                          "TestAssembly"
                          [ EvaluatedMetadata("Aliases", "global")
                            EvaluatedMetadata("Private", "true")
                            EvaluatedMetadata("EmbedInteropTypes", "false")
                            EvaluatedMetadata("SpecificVersion", "true") ]
                      item
                          11
                          "PackageReference"
                          "Newtonsoft.Json"
                          [ EvaluatedMetadata("PrivateAssets", "all")
                            EvaluatedMetadata("IncludeAssets", "runtime")
                            EvaluatedMetadata("ExcludeAssets", "build") ] ]

            let properties =
                ImmutableArray.CreateRange
                    [ EvaluatedProperty("BaseIntermediateOutputPath", "obj/")
                      EvaluatedProperty("IntermediateOutputPath", "custom-intermediate/")
                      EvaluatedProperty("BaseOutputPath", "bin/")
                      EvaluatedProperty("OutputPath", "artifacts/")
                      EvaluatedProperty("NuGetPackageRoot", packageRoot) ]

            let outerDimension items =
                ProjectEvaluationDimension(
                    Nullable(),
                    properties,
                    items,
                    ImmutableArray.Create(
                        EvaluatedReference("Library.csproj", path "Library.csproj")
                    ),
                    ImmutableArray.CreateRange
                        [ EvaluatedReference("System.Xml", null)
                          EvaluatedReference(
                              "TestAssembly",
                              WorkspaceArtifactPath.Create testAssemblyPath
                          ) ],
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
            let root = (roots) |> Seq.exactlyOne
            (root.Node.Kind) |> should equal (WorkspaceNodeKind.Workspace)
            (root.Node.Name) |> should equal ("Semantic")

            let childrenFor (source: IndexedNode array) (parentId: WorkspaceNodeId) =
                source
                |> Array.filter (fun placement -> placement.ParentWorkspaceNodeId = Some parentId)
                |> Array.sortBy _.Index

            let children = childrenFor placements

            let rootChildren = children root.Node.Id

            (rootChildren |> Array.map _.Node.Name)
            |> should equal ([| "src"; "Library"; "VisualBasic" |])

            (rootChildren)
            |> Seq.exists (fun node ->
                node.Node.Kind = WorkspaceNodeKind.Configuration
                || node.Node.Kind = WorkspaceNodeKind.Platform)
            |> should equal false

            let src = rootChildren |> Array.find (fun value -> value.Node.Name = "src")
            let srcChildren = children src.Node.Id

            (srcChildren |> Array.map _.Node.Name)
            |> should equal ([| "Directory.Build.props"; "App" |])

            let appNode =
                srcChildren
                |> Array.find (fun value -> value.Node.Kind = WorkspaceNodeKind.Project)

            let appChildren = children appNode.Node.Id

            (appChildren |> Array.map _.Node.Name)
            |> should equal ([| "Dependencies"; "A"; "B"; "Root.txt" |])

            let folderA = appChildren |> Array.find (fun value -> value.Node.Name = "A")
            let folderB = appChildren |> Array.find (fun value -> value.Node.Name = "B")

            (children folderA.Node.Id |> Array.map _.Node.Name)
            |> should equal ([| "First.fs"; "Third.fs" |])

            (children folderB.Node.Id |> Array.map _.Node.Name)
            |> should equal ([| "Second.fs" |])

            let dependencies =
                appChildren |> Array.find (fun value -> value.Node.Name = "Dependencies")

            (children dependencies.Node.Id |> Array.map _.Node.Name)
            |> should
                equal
                [| "Library"
                   "Newtonsoft.Json (13.0.3)"
                   "Newtonsoft.Json (14.0.1)"
                   "Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests"
                   "System.Xml"
                   "Analyzer.dll" |]

            let packageDependency =
                children dependencies.Node.Id
                |> Array.find (fun value -> value.Node.Name = "Newtonsoft.Json (13.0.3)")

            let expectedPackagePath = Path.Combine(packageRoot, "newtonsoft.json", "13.0.3")

            (children packageDependency.Node.Id |> Array.map _.Node.Name)
            |> should
                equal
                [| "Type: Package"
                   "ID: Newtonsoft.Json"
                   "Version: 13.0.3"
                   $"Path: {expectedPackagePath}"
                   "Private Assets: all"
                   "Include Assets: runtime"
                   "Exclude Assets: build" |]

            let assemblyDependency =
                children dependencies.Node.Id
                |> Array.find (fun value ->
                    value.Node.Name = "Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests")

            let assemblyDetails = children assemblyDependency.Node.Id |> Array.map _.Node.Name

            for expected in
                [ "Type: Assembly"
                  "Resolved: True"
                  $"Path: {testAssemblyPath}"
                  "Identity: Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests"
                  "Culture: neutral"
                  "Strong Name: False"
                  "Aliases: global"
                  "Copy Local: True"
                  "Embed Interop Types: False"
                  "Specific Version: True" ] do
                (assemblyDetails) |> should contain (expected)

            (assemblyDetails
             |> Array.exists (_.StartsWith("Version: ", StringComparison.Ordinal)))
            |> should equal true

            (assemblyDetails
             |> Array.exists (_.StartsWith("Runtime Version: ", StringComparison.Ordinal)))
            |> should equal true

            (placements)
            |> Seq.exists (fun placement ->
                [ "Generated.fs"; "Generated.txt"; "Generated.resources"; "Ignored.custom" ]
                |> List.contains placement.Node.Name)
            |> should equal false

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

            (WorkspaceIndexDiff.diff workspace.Descriptor.Id 0L placements placements
             |> Option.isNone)
            |> should equal true

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

            (fallbackPlacements
             |> Array.filter (fun placement ->
                 placement.ParentWorkspaceNodeId = Some fallbackFolder.Node.Id)
             |> Array.sortBy _.Index
             |> Array.map _.Node.Name)
            |> should equal ([| "Preferred.fs"; "Shared.fs" |])

            let insensitiveItems =
                ImmutableArray.CreateRange
                    [ item 0 "Compile" "src/Api/First.fs" []
                      item 1 "Compile" "SRC/API/FIRST.fs" []
                      item 2 "Compile" "src/API/Second.fs" [] ]

            let insensitiveIndexed = indexed (snapshot insensitiveItems) 0L

            let insensitivePlacements =
                WorkspaceIndexDiff.placements
                    true
                    { insensitiveIndexed with
                        Hydrated =
                            insensitiveIndexed.Hydrated
                            |> Seq.map (fun (KeyValue(key, value)) -> key.ToUpperInvariant(), value)
                            |> Map.ofSeq }

            let insensitiveApp =
                insensitivePlacements
                |> Array.find (fun placement ->
                    placement.Node.Kind = WorkspaceNodeKind.Project && placement.Node.Name = "App")

            let insensitiveSource =
                childrenFor insensitivePlacements insensitiveApp.Node.Id
                |> Array.filter (fun placement ->
                    placement.Node.Kind = WorkspaceNodeKind.ProjectFolder
                    && String.Equals(
                        placement.Node.Name,
                        "src",
                        StringComparison.OrdinalIgnoreCase
                    ))
                |> Seq.exactlyOne

            (insensitiveSource.Node.Name) |> should equal ("src")

            let insensitiveApi =
                childrenFor insensitivePlacements insensitiveSource.Node.Id
                |> Array.filter (fun placement ->
                    placement.Node.Kind = WorkspaceNodeKind.ProjectFolder
                    && String.Equals(
                        placement.Node.Name,
                        "Api",
                        StringComparison.OrdinalIgnoreCase
                    ))
                |> Seq.exactlyOne

            (insensitiveApi.Node.Name) |> should equal ("Api")

            (childrenFor insensitivePlacements insensitiveApi.Node.Id
             |> Array.map _.Node.Name)
            |> should equal ([| "First.fs"; "Second.fs" |])

            let fileReorderedItems =
                outerItems
                |> Seq.map (fun value ->
                    match value.EvaluatedInclude with
                    | "A/First.fs" -> item 2 "Compile" "A/First.fs" []
                    | "A/Third.fs" -> item 0 "Compile" "A/Third.fs" []
                    | _ -> value)
                |> ImmutableArray.CreateRange

            let fileReordered =
                WorkspaceIndexDiff.placements false (indexed (snapshot fileReorderedItems) 0L)

            let fileReorderDelta =
                WorkspaceIndexDiff.diff workspace.Descriptor.Id 0L placements fileReordered
                |> Option.defaultWith (fun () -> failwith "Expected a semantic file-order delta.")

            (fileReorderDelta.Changes.Length) |> should equal (2)

            let nameFor nodeId =
                placements
                |> Array.find (fun placement -> placement.Node.Id = nodeId)
                |> _.Node.Name

            let fileMoves =
                fileReorderDelta.Changes
                |> Seq.choose (function
                    | Moved(nodeId, oldParent, oldIndex, newParent, newIndex) when
                        oldParent = Some folderA.Node.Id && newParent = oldParent
                        ->
                        Some(nameFor nodeId, oldIndex, newIndex)
                    | _ -> None)
                |> Seq.toArray

            (fileMoves) |> should equal ([| "First.fs", 0, 1; "Third.fs", 1, 0 |])

            let fileReorderedFolderA =
                fileReordered
                |> Array.find (fun placement -> placement.Node.Id = folderA.Node.Id)

            (childrenFor fileReordered fileReorderedFolderA.Node.Id |> Array.map _.Node.Name)
            |> should equal ([| "Third.fs"; "First.fs" |])

            let folderReorderedItems =
                outerItems
                |> Seq.map (fun value ->
                    match value.EvaluatedInclude with
                    | "A/First.fs" -> item 1 "Compile" "A/First.fs" []
                    | "B/Second.fs" -> item 0 "Compile" "B/Second.fs" []
                    | "A/Third.fs" -> item 2 "Compile" "A/Third.fs" []
                    | _ -> value)
                |> ImmutableArray.CreateRange

            let folderReordered =
                WorkspaceIndexDiff.placements false (indexed (snapshot folderReorderedItems) 0L)

            let folderReorderDelta =
                WorkspaceIndexDiff.diff workspace.Descriptor.Id 0L placements folderReordered
                |> Option.defaultWith (fun () -> failwith "Expected a semantic folder-order delta.")

            (folderReorderDelta.Changes.Length) |> should equal (2)

            let folderMoves =
                folderReorderDelta.Changes
                |> Seq.choose (function
                    | Moved(nodeId, oldParent, oldIndex, newParent, newIndex) when
                        oldParent = Some appNode.Node.Id && newParent = oldParent
                        ->
                        Some(nameFor nodeId, oldIndex, newIndex)
                    | _ -> None)
                |> Seq.toArray

            (folderMoves) |> should equal ([| "A", 1, 2; "B", 2, 1 |])

            let folderReorderedApp =
                folderReordered
                |> Array.find (fun placement -> placement.Node.Id = appNode.Node.Id)

            (childrenFor folderReordered folderReorderedApp.Node.Id |> Array.map _.Node.Name)
            |> should equal ([| "Dependencies"; "B"; "A"; "Root.txt" |])

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

            (delta.Changes)
            |> Seq.exists (fun change ->
                match change with
                | Added(node, parent, _) ->
                    node.Kind = WorkspaceNodeKind.ProjectFile
                    && node.Name = "Fourth.fs"
                    && parent = Some folderA.Node.Id
                | _ -> false)
            |> should equal true
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
