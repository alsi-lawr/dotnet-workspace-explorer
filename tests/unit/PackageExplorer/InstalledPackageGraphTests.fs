namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Immutable
open System.IO
open System.Text.Json
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Xunit

module private InstalledPackageGraphScenario =
    let source = "https://packages.example.test/v3/index.json"

    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-installed-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let write (path: string) (content: string) =
        Path.GetDirectoryName path
        |> Option.ofObj
        |> Option.iter (Directory.CreateDirectory >> ignore)

        File.WriteAllText(path, content)

    let quoted value = JsonSerializer.Serialize value

    let assets projectPath configPath projectReference =
        let project = quoted projectPath
        let config = quoted configPath
        let reference = quoted projectReference

        $"""
{{
  "version": 4,
  "targets": {{
    "net8.0": {{
      "Central.Package/1.2.3": {{ "type": "package", "compile": {{ "lib/net8.0/a.dll": {{}} }} }},
      "Direct.Package/2.0.0": {{
        "type": "package",
        "dependencies": {{ "Transitive.Package": "3.0.0" }},
        "compile": {{ "lib/net8.0/b.dll": {{}} }}
      }},
      "Implicit.Package/8.0.0": {{ "type": "package" }},
      "Transitive.Package/3.0.0": {{ "type": "package" }}
    }},
    "net9.0": {{
      "Central.Package/4.5.6": {{ "type": "package" }},
      "Direct.Package/2.0.0": {{ "type": "package" }}
    }},
    "net9.0/linux-x64": {{
      "Central.Package/4.5.6": {{ "type": "package" }},
      "Direct.Package/2.0.0": {{ "type": "package" }},
      "Runtime.Package/5.0.0": {{ "type": "package" }}
    }}
  }},
  "libraries": {{
    "Central.Package/1.2.3": {{ "sha512": "one", "type": "package", "path": "central/1.2.3", "files": [] }},
    "Central.Package/4.5.6": {{ "sha512": "two", "type": "package", "path": "central/4.5.6", "files": [] }},
    "Direct.Package/2.0.0": {{ "sha512": "three", "type": "package", "path": "direct/2.0.0", "files": [] }},
    "Implicit.Package/8.0.0": {{ "sha512": "four", "type": "package", "path": "implicit/8.0.0", "files": [] }},
    "Transitive.Package/3.0.0": {{ "sha512": "five", "type": "package", "path": "transitive/3.0.0", "files": [] }},
    "Runtime.Package/5.0.0": {{ "sha512": "six", "type": "package", "path": "runtime/5.0.0", "files": [] }}
  }},
  "projectFileDependencyGroups": {{
    "net8.0": [ "Central.Package >= 1.2.3", "Direct.Package >= 2.0.0", "Implicit.Package >= 8.0.0", "Unresolved.Package >= 7.0.0" ],
    "net9.0": [ "Central.Package >= 4.5.6", "Direct.Package >= 2.0.0" ]
  }},
  "packageFolders": {{ "/packages/": {{}} }},
  "project": {{
    "version": "1.0.0",
    "restore": {{
      "projectUniqueName": {project},
      "projectName": "Example",
      "projectPath": {project},
      "packagesPath": "/packages/",
      "outputPath": "obj/",
      "projectStyle": "PackageReference",
      "centralPackageVersionsManagementEnabled": true,
      "configFilePaths": [ {config} ],
      "originalTargetFrameworks": [ "net8.0", "net9.0" ],
      "sources": {{ "{source}": {{}} }},
      "frameworks": {{
        "net8.0": {{
          "framework": "net8.0",
          "targetAlias": "net8.0",
          "projectReferences": {{ {reference}: {{ "projectPath": {reference} }} }}
        }},
        "net9.0": {{ "framework": "net9.0", "targetAlias": "net9.0" }}
      }}
    }},
    "frameworks": {{
      "net8.0": {{
        "targetAlias": "net8.0",
        "dependencies": {{
          "Central.Package": {{ "target": "Package", "version": "[1.2.3, )", "versionCentrallyManaged": true }},
          "Direct.Package": {{ "target": "Package", "version": "[2.0.0, )" }},
          "Implicit.Package": {{ "target": "Package", "version": "[8.0.0, )", "autoReferenced": true }},
          "Unresolved.Package": {{ "target": "Package", "version": "[7.0.0, )" }}
        }},
        "centralPackageVersions": {{ "Central.Package": "1.2.3" }},
        "frameworkReferences": {{ "Microsoft.NETCore.App": {{ "privateAssets": "all" }} }}
      }},
      "net9.0": {{
        "targetAlias": "net9.0",
        "dependencies": {{
          "Central.Package": {{ "target": "Package", "version": "[4.5.6, )", "versionCentrallyManaged": true }},
          "Direct.Package": {{ "target": "Package", "version": "[2.0.0, )" }}
        }},
        "centralPackageVersions": {{ "Central.Package": "4.5.6" }}
      }}
    }}
  }}
}}
"""

    let private array values = ImmutableArray.CreateRange values

    let dimension
        projectPath
        ownerPath
        framework
        runtime
        centralVersion
        memberships
        projectReferences
        =
        let properties =
            [ EvaluatedProperty("TargetFramework", framework)
              if not (String.IsNullOrWhiteSpace runtime) then
                  EvaluatedProperty("RuntimeIdentifiers", runtime) ]
            |> array

        let packageMemberships =
            memberships
            |> List.map (fun (identity, version, condition) ->
                EvaluatedPackageMembership(
                    identity,
                    version |> Option.toObj,
                    WorkspaceArtifactPath.Create projectPath,
                    condition
                ))
            |> array

        let centralVersions =
            [ EvaluatedPackageVersion(
                  "Central.Package",
                  centralVersion,
                  WorkspaceArtifactPath.Create ownerPath,
                  $"'$(TargetFramework)' == '{framework}'"
              ) ]
            |> array

        let references =
            projectReferences
            |> List.map (fun path -> EvaluatedReference(path, WorkspaceArtifactPath.Create path))
            |> array

        ProjectEvaluationDimension(
            Nullable(EvaluatedTargetFramework framework),
            properties,
            ImmutableArray<EvaluatedItem>.Empty,
            references,
            ImmutableArray<EvaluatedReference>.Empty,
            ImmutableArray<EvaluatedPackage>.Empty,
            ImmutableArray<EvaluatedReference>.Empty,
            PackageMemberships = packageMemberships,
            PackageVersions = centralVersions
        )

    let snapshot projectPath ownerPath projectReference =
        let eight =
            dimension
                projectPath
                ownerPath
                "net8.0"
                ""
                "1.2.3"
                [ "Central.Package", None, "'$(TargetFramework)' == 'net8.0'"
                  "Direct.Package", Some "2.0.0", ""
                  "Implicit.Package", Some "8.0.0", ""
                  "Unresolved.Package", Some "7.0.0", "" ]
                [ projectReference ]

        let nine =
            dimension
                projectPath
                ownerPath
                "net9.0"
                "linux-x64"
                "4.5.6"
                [ "Central.Package", None, "'$(TargetFramework)' == 'net9.0'"
                  "Direct.Package", Some "2.0.0", "" ]
                []

        ProjectEvaluationSnapshot(
            WorkspaceArtifactPath.Create projectPath,
            array [ eight; nine ],
            array
                [ WorkspaceArtifactPath.Create projectPath
                  WorkspaceArtifactPath.Create ownerPath ],
            array
                [ WorkspaceArtifactPath.Create projectPath
                  WorkspaceArtifactPath.Create ownerPath ],
            ImmutableArray<WorkspaceArtifactPath>.Empty,
            WorkspaceCapabilityProfile.Full,
            array [ WorkspaceCapabilityId.Read ],
            ImmutableArray<WorkspaceDiagnostic>.Empty
        )

    let configuration configPath mapping =
        { Sources = [ source ]
          ConfigFiles = [ Path.GetFullPath configPath ]
          SourceMappingEnabled = mapping }

    let targetFramework graph =
        match graph.Target with
        | PackageTargetScope.Framework(_, framework) -> framework.Value, None
        | PackageTargetScope.Runtime(_, framework, runtime) -> framework.Value, Some runtime.Value
        | PackageTargetScope.Project _ -> "", None

    let package identity graph =
        graph.Packages |> List.find (fun item -> item.Identity.Value = identity)

[<Sealed>]
type InstalledPackageGraphTests() =
    [<Fact>]
    member _.``installed package graph retains framework dimensions ownership classifications and RID-only differences``
        ()
        =
        let directory = InstalledPackageGraphScenario.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "Example.csproj")
            let owner = Path.Combine(directory, "Directory.Packages.props")
            let reference = Path.Combine(directory, "Referenced.csproj")
            let config = Path.Combine(directory, "NuGet.Config")
            let assets = Path.Combine(directory, "obj", "project.assets.json")

            InstalledPackageGraphScenario.write
                project
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks><RuntimeIdentifiers>linux-x64</RuntimeIdentifiers></PropertyGroup></Project>"

            InstalledPackageGraphScenario.write owner "<Project />"

            InstalledPackageGraphScenario.write
                reference
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"

            InstalledPackageGraphScenario.write config "<configuration />"

            InstalledPackageGraphScenario.write
                assets
                (InstalledPackageGraphScenario.assets project config reference)

            let graphs =
                InstalledPackageGraphs.readSnapshot
                    (InstalledPackageGraphScenario.configuration config false)
                    (InstalledPackageGraphScenario.snapshot project owner reference)
                    assets

            graphs
            |> List.map InstalledPackageGraphScenario.targetFramework
            |> should equal [ "net8.0", None; "net9.0", None; "net9.0", Some "linux-x64" ]

            graphs
            |> List.map _.State
            |> should
                equal
                [ InstalledPackageGraphState.Current
                  InstalledPackageGraphState.Current
                  InstalledPackageGraphState.Current ]

            let eight = graphs[0]

            match (InstalledPackageGraphScenario.package "Central.Package" eight).State with
            | InstalledPackageState.CentrallyManagedDirect(_, resolved, centralOwner) ->
                resolved.Value |> should equal "1.2.3"
                centralOwner |> should equal owner
            | state -> failwithf "Unexpected central package state: %A" state

            (InstalledPackageGraphScenario.package "Direct.Package" eight)
                .Declaration.Value.OwnerFile
            |> should equal project

            (InstalledPackageGraphScenario.package "Transitive.Package" eight).State
            |> should
                equal
                (InstalledPackageState.Transitive(
                    NuGetVersion.create "3.0.0" |> Result.defaultWith (failwithf "%A")
                ))

            match (InstalledPackageGraphScenario.package "Implicit.Package" eight).State with
            | InstalledPackageState.FrameworkProvided resolved ->
                resolved.Value |> should equal "8.0.0"
            | state -> failwithf "Unexpected implicit package state: %A" state

            (InstalledPackageGraphScenario.package "Microsoft.NETCore.App" eight).State
            |> should equal InstalledPackageState.FrameworkProvidedWithoutVersion

            match (InstalledPackageGraphScenario.package "Unresolved.Package" eight).State with
            | InstalledPackageState.UnresolvedDirect _ -> ()
            | state -> failwithf "Unexpected unresolved package state: %A" state

            match (InstalledPackageGraphScenario.package "Central.Package" graphs[1]).State with
            | InstalledPackageState.CentrallyManagedDirect(_, resolved, centralOwner) ->
                resolved.Value |> should equal "4.5.6"
                centralOwner |> should equal owner
            | state -> failwithf "Unexpected net9 central package state: %A" state

            graphs[2].Packages
            |> List.map (_.Identity.Value)
            |> should equal [ "Runtime.Package" ]
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``missing malformed mismatched unverifiable and stale restore evidence remain explicit non-success states``
        ()
        =
        let directory = InstalledPackageGraphScenario.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "Example.csproj")
            let owner = Path.Combine(directory, "Directory.Packages.props")
            let reference = Path.Combine(directory, "Referenced.csproj")
            let config = Path.Combine(directory, "NuGet.Config")
            let assets = Path.Combine(directory, "obj", "project.assets.json")

            InstalledPackageGraphScenario.write
                project
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>"

            InstalledPackageGraphScenario.write owner "<Project />"

            InstalledPackageGraphScenario.write
                reference
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"

            InstalledPackageGraphScenario.write config "<configuration />"
            let snapshot = InstalledPackageGraphScenario.snapshot project owner reference

            let state configuration =
                InstalledPackageGraphs.readSnapshot configuration snapshot assets
                |> List.map _.State
                |> List.distinct
                |> List.exactlyOne

            state (InstalledPackageGraphScenario.configuration config false)
            |> should equal InstalledPackageGraphState.MissingRestoreGraph

            InstalledPackageGraphScenario.write assets "{"

            state (InstalledPackageGraphScenario.configuration config false)
            |> should equal InstalledPackageGraphState.MismatchedRestoreGraph

            InstalledPackageGraphScenario.write
                assets
                (InstalledPackageGraphScenario.assets project config reference)

            state
                { Sources = [ InstalledPackageGraphScenario.source ]
                  ConfigFiles = []
                  SourceMappingEnabled = false }
            |> should equal InstalledPackageGraphState.StaleRestoreGraph

            let emptyConfigAssets =
                InstalledPackageGraphScenario.assets project config reference
                |> fun content ->
                    content.Replace(
                        $"\"configFilePaths\": [ {InstalledPackageGraphScenario.quoted config} ]",
                        "\"configFilePaths\": []"
                    )

            InstalledPackageGraphScenario.write assets emptyConfigAssets

            state
                { Sources = [ InstalledPackageGraphScenario.source ]
                  ConfigFiles = []
                  SourceMappingEnabled = false }
            |> should equal InstalledPackageGraphState.UnverifiablyFreshRestoreGraph

            InstalledPackageGraphScenario.write
                assets
                (InstalledPackageGraphScenario.assets project config reference)

            let mismatched =
                InstalledPackageGraphScenario.snapshot
                    (Path.Combine(directory, "Other.csproj"))
                    owner
                    reference

            InstalledPackageGraphs.readSnapshot
                (InstalledPackageGraphScenario.configuration config false)
                mismatched
                assets
            |> List.map _.State
            |> List.distinct
            |> should equal [ InstalledPackageGraphState.MismatchedRestoreGraph ]
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``changed membership central version project reference framework source mapping or restored target marks existing assets stale``
        ()
        =
        let directory = InstalledPackageGraphScenario.temporaryDirectory ()

        try
            let project = Path.Combine(directory, "Example.csproj")
            let owner = Path.Combine(directory, "Directory.Packages.props")
            let reference = Path.Combine(directory, "Referenced.csproj")
            let config = Path.Combine(directory, "NuGet.Config")
            let assets = Path.Combine(directory, "obj", "project.assets.json")

            InstalledPackageGraphScenario.write
                project
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>"

            InstalledPackageGraphScenario.write owner "<Project />"

            InstalledPackageGraphScenario.write
                reference
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"

            InstalledPackageGraphScenario.write config "<configuration />"

            let originalAssets = InstalledPackageGraphScenario.assets project config reference
            InstalledPackageGraphScenario.write assets originalAssets

            let assertStale snapshot configuration =
                InstalledPackageGraphs.readSnapshot configuration snapshot assets
                |> List.map _.State
                |> List.distinct
                |> should equal [ InstalledPackageGraphState.StaleRestoreGraph ]

            let original = InstalledPackageGraphScenario.snapshot project owner reference
            let changedReference = Path.Combine(directory, "Changed.csproj")

            InstalledPackageGraphScenario.write
                changedReference
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"

            let withChangedReference =
                InstalledPackageGraphScenario.snapshot project owner changedReference

            assertStale
                withChangedReference
                (InstalledPackageGraphScenario.configuration config false)

            let changedInputs = original.Dimensions[0]

            let changedMemberships =
                changedInputs.PackageMemberships
                |> Seq.map (fun membership ->
                    if membership.Id = "Direct.Package" then
                        EvaluatedPackageMembership(
                            membership.Id,
                            "9.0.0",
                            membership.DeclaringPath,
                            membership.Condition
                        )
                    else
                        membership)
                |> ImmutableArray.CreateRange

            let changedDimension =
                ProjectEvaluationDimension(
                    changedInputs.TargetFramework,
                    changedInputs.Properties,
                    changedInputs.Items,
                    changedInputs.ProjectReferences,
                    changedInputs.References,
                    changedInputs.Packages,
                    changedInputs.Analyzers,
                    PackageMemberships = changedMemberships,
                    PackageVersions = changedInputs.PackageVersions
                )

            let changedSnapshot =
                ProjectEvaluationSnapshot(
                    original.ProjectPath,
                    ImmutableArray.CreateRange [ changedDimension; original.Dimensions[1] ],
                    original.Imports,
                    original.WatchInputs,
                    original.GlobRoots,
                    original.CapabilityProfile,
                    original.Capabilities,
                    original.Diagnostics
                )

            assertStale changedSnapshot (InstalledPackageGraphScenario.configuration config false)

            let changedCentralVersions =
                changedInputs.PackageVersions
                |> Seq.map (fun central ->
                    EvaluatedPackageVersion(
                        central.Id,
                        "9.0.0",
                        central.DeclaringPath,
                        central.Condition
                    ))
                |> ImmutableArray.CreateRange

            let changedCentralDimension =
                ProjectEvaluationDimension(
                    changedInputs.TargetFramework,
                    changedInputs.Properties,
                    changedInputs.Items,
                    changedInputs.ProjectReferences,
                    changedInputs.References,
                    changedInputs.Packages,
                    changedInputs.Analyzers,
                    PackageMemberships = changedInputs.PackageMemberships,
                    PackageVersions = changedCentralVersions
                )

            let changedCentralSnapshot =
                ProjectEvaluationSnapshot(
                    original.ProjectPath,
                    ImmutableArray.CreateRange [ changedCentralDimension; original.Dimensions[1] ],
                    original.Imports,
                    original.WatchInputs,
                    original.GlobRoots,
                    original.CapabilityProfile,
                    original.Capabilities,
                    original.Diagnostics
                )

            assertStale
                changedCentralSnapshot
                (InstalledPackageGraphScenario.configuration config false)

            let changedFrameworkDimension =
                InstalledPackageGraphScenario.dimension
                    project
                    owner
                    "net10.0"
                    ""
                    "4.5.6"
                    [ "Central.Package", None, ""; "Direct.Package", Some "2.0.0", "" ]
                    []

            let changedFrameworkSnapshot =
                ProjectEvaluationSnapshot(
                    original.ProjectPath,
                    ImmutableArray.CreateRange [ original.Dimensions[0]; changedFrameworkDimension ],
                    original.Imports,
                    original.WatchInputs,
                    original.GlobRoots,
                    original.CapabilityProfile,
                    original.Capabilities,
                    original.Diagnostics
                )

            assertStale
                changedFrameworkSnapshot
                (InstalledPackageGraphScenario.configuration config false)

            assertStale
                original
                { InstalledPackageGraphScenario.configuration config false with
                    SourceMappingEnabled = true }

            assertStale
                original
                { InstalledPackageGraphScenario.configuration config false with
                    Sources = [ "https://changed.example.test/v3/index.json" ] }

            InstalledPackageGraphScenario.write
                assets
                (originalAssets.Replace("\"net9.0/linux-x64\"", "\"net9.0/win-x64\""))

            InstalledPackageGraphs.readSnapshot
                (InstalledPackageGraphScenario.configuration config false)
                original
                assets
            |> List.map _.State
            |> List.distinct
            |> should equal [ InstalledPackageGraphState.StaleRestoreGraph ]
        finally
            Directory.Delete(directory, true)
