namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Immutable
open System.IO
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Xunit

module private PackageOperationPreviewScenario =
    type EvaluationShape =
        { Central: bool
          MembershipOwner: string option
          CentralOwner: string option
          MembershipConditions: string list
          CentralVersions: (string * string) list
          TransitivePinning: bool }

    let array values = ImmutableArray.CreateRange values

    let package value =
        PackageId.create value |> Result.defaultWith (failwithf "%A")

    let version value =
        NuGetVersion.create value |> Result.defaultWith (failwithf "%A")

    let range value =
        NuGetVersionRange.create value |> Result.defaultWith (failwithf "%A")

    let source value =
        PackageSourceId.create value |> Result.defaultWith (failwithf "%A")

    let project value =
        PackageProjectId.create value |> Result.defaultWith (failwithf "%A")

    let framework value =
        TargetFramework.create value |> Result.defaultWith (failwithf "%A")

    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-operation-preview-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let write (path: string) (content: string) =
        Path.GetDirectoryName path
        |> Option.ofObj
        |> Option.iter (Directory.CreateDirectory >> ignore)

        File.WriteAllText(path, content)

    let directShape =
        { Central = false
          MembershipOwner = None
          CentralOwner = None
          MembershipConditions = []
          CentralVersions = []
          TransitivePinning = false }

    let centralShape owner =
        { directShape with
            Central = true
            CentralOwner = Some owner }

    let dimension
        (projectPath: string)
        (frameworkName: string)
        (identity: PackageId)
        (shape: EvaluationShape)
        =
        let properties =
            [ EvaluatedProperty("TargetFramework", frameworkName)
              EvaluatedProperty(
                  "ManagePackageVersionsCentrally",
                  if shape.Central then "true" else "false"
              )
              EvaluatedProperty(
                  "CentralPackageTransitivePinningEnabled",
                  if shape.TransitivePinning then "true" else "false"
              ) ]
            |> array

        let memberships =
            shape.MembershipConditions
            |> List.map (fun condition ->
                EvaluatedPackageMembership(
                    identity.Value,
                    "1.0.0",
                    WorkspaceArtifactPath.Create(
                        shape.MembershipOwner |> Option.defaultValue projectPath
                    ),
                    condition
                ))
            |> array

        let versions =
            shape.CentralVersions
            |> List.map (fun (declaredVersion, condition) ->
                EvaluatedPackageVersion(
                    identity.Value,
                    declaredVersion,
                    WorkspaceArtifactPath.Create(
                        shape.CentralOwner
                        |> Option.defaultValue (
                            Path.Combine(
                                Path.GetDirectoryName projectPath,
                                "Directory.Packages.props"
                            )
                        )
                    ),
                    condition
                ))
            |> array

        ProjectEvaluationDimension(
            Nullable(EvaluatedTargetFramework frameworkName),
            properties,
            ImmutableArray<EvaluatedItem>.Empty,
            ImmutableArray<EvaluatedReference>.Empty,
            ImmutableArray<EvaluatedReference>.Empty,
            ImmutableArray<EvaluatedPackage>.Empty,
            ImmutableArray<EvaluatedReference>.Empty,
            PackageMemberships = memberships,
            PackageVersions = versions
        )

    let snapshot (projectPath: string) (dimensions: ProjectEvaluationDimension list) =
        ProjectEvaluationSnapshot(
            WorkspaceArtifactPath.Create projectPath,
            array dimensions,
            array [ WorkspaceArtifactPath.Create projectPath ],
            array [ WorkspaceArtifactPath.Create projectPath ],
            ImmutableArray<WorkspaceArtifactPath>.Empty,
            WorkspaceCapabilityProfile.Full,
            array [ WorkspaceCapabilityId.Read ],
            ImmutableArray<WorkspaceDiagnostic>.Empty
        )

    let target (projectPath: string) (frameworkName: string) =
        PackageTargetScope.Framework(project projectPath, framework frameworkName)

    let declaration (projectPath: string) =
        Some
            { OwnerFile = projectPath
              Condition = "" }

    let targetProjectPath (target: PackageTargetScope) =
        match target with
        | PackageTargetScope.Project project
        | PackageTargetScope.Framework(project, _)
        | PackageTargetScope.Runtime(project, _, _) -> project.Value

    let installed
        (target: PackageTargetScope)
        (identity: PackageId)
        (state: InstalledPackageState)
        =
        { Identity = identity
          Target = target
          State = state
          Declaration =
            match state with
            | InstalledPackageState.Transitive _
            | InstalledPackageState.FrameworkProvided _
            | InstalledPackageState.FrameworkProvidedWithoutVersion -> None
            | _ -> declaration (targetProjectPath target) }

    let graph (target: PackageTargetScope) (packages: InstalledPackage list) =
        { Target = target
          State = InstalledPackageGraphState.Current
          Packages = packages }

    let details (identity: PackageId) (selectedVersion: NuGetVersion) (sourceId: PackageSourceId) =
        let dependency = package "Example.Dependency", range "[1.0.0, 2.0.0)"
        let advisory = Uri "https://advisories.example.test/one"

        { Summary =
            { Identity = identity
              Version = selectedVersion
              Description = Some "description"
              Summary = Some "summary"
              Tags = []
              Authors = []
              Owners = []
              Source = sourceId }
          Versions = [ selectedVersion ]
          Authors = []
          ProjectUrl = None
          License = Some "MIT"
          LicenseUrl = None
          ReadmeUrl = None
          DependencyGroups = Map [ None, [ dependency ] ]
          Deprecation = PackageDeprecation.Deprecated(NonEmptyList.singleton "Legacy", None)
          Vulnerabilities =
            [ { Severity = PackageVulnerabilitySeverity.High
                Advisory = advisory } ] }

    let evidence
        (root: string)
        (evaluations: ProjectEvaluationSnapshot list)
        (graphs: InstalledPackageGraph list)
        (details: PackageDetails option)
        (mapping: PackageSourceMappingPolicy)
        (fingerprints: Map<string, string>)
        =
        { WorkspaceRoot = root
          Evaluations = evaluations
          Installed = graphs
          Details =
            details
            |> Option.map (fun value -> Map [ value.Summary.Version, value ])
            |> Option.defaultValue Map.empty
          SourceMapping = mapping
          WorkspaceRevision = "42"
          FileFingerprints = fingerprints }

    let request
        (root: string)
        (operation: RequestedPackageOperation)
        (targets: PackageTargetScope list)
        (browseSource: PackageSourceId option)
        (fingerprints: Map<string, string>)
        =
        { Id = PackageRequestId.newId ()
          Target = PackageWorkspaceTarget.directory root |> Result.defaultWith (failwithf "%A")
          Value =
            { Operation = operation
              Targets =
                targets
                |> NonEmptyList.tryCreate
                |> Option.defaultWith (fun () -> failwith "Expected targets")
              BrowseSource = browseSource
              Precondition =
                { WorkspaceRevision = "42"
                  FileFingerprints = fingerprints } } }

    let preview
        (evidence: PackageOperationPreviewEvidence)
        (request: PackageRequest<PackageOperationRequest>)
        =
        PackageOperationPreviews.create (fun _ -> async { return Ok evidence }) request
        |> Async.RunSynchronously

    let success =
        function
        | Ok value -> value
        | Error failure ->
            failwithf "%s: %s" (PackageFailure.code failure) (PackageFailure.message failure)

    let failure =
        function
        | Error value -> value
        | Ok _ -> failwith "Expected package preview failure."

    let targets (preview: PackagePreview) =
        PackagePreview.targets preview |> NonEmptyList.toList

[<Sealed>]
type PackageOperationPreviewTests() =
    [<Fact>]
    member _.``install update and uninstall previews retain direct and central owners metadata policy restore and browse source semantics``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let one = PackageOperationPreviewScenario.version "1.0.0"
            let two = PackageOperationPreviewScenario.version "2.0.0"
            let browse = PackageOperationPreviewScenario.source "browse"
            let apply = PackageOperationPreviewScenario.source "apply"
            let directProject = Path.Combine(root, "Direct.csproj")
            let centralProject = Path.Combine(root, "Central.fsproj")
            let centralOwner = Path.Combine(root, "Directory.Packages.props")

            for path in [ directProject; centralProject; centralOwner ] do
                PackageOperationPreviewScenario.write path "original"

            let directTarget = PackageOperationPreviewScenario.target directProject "net10.0"
            let centralTarget = PackageOperationPreviewScenario.target centralProject "net10.0"

            let directSnapshot =
                PackageOperationPreviewScenario.snapshot
                    directProject
                    [ PackageOperationPreviewScenario.dimension
                          directProject
                          "net10.0"
                          identity
                          PackageOperationPreviewScenario.directShape ]

            let centralSnapshot =
                PackageOperationPreviewScenario.snapshot
                    centralProject
                    [ PackageOperationPreviewScenario.dimension
                          centralProject
                          "net10.0"
                          identity
                          (PackageOperationPreviewScenario.centralShape centralOwner) ]

            let fingerprints =
                Map
                    [ directProject, "direct-hash"
                      centralProject, "central-project-hash"
                      centralOwner, "central-owner-hash" ]

            let metadata = PackageOperationPreviewScenario.details identity two browse

            let installEvidence =
                PackageOperationPreviewScenario.evidence
                    root
                    [ directSnapshot ]
                    [ PackageOperationPreviewScenario.graph directTarget [] ]
                    (Some metadata)
                    (PackageSourceMappingPolicy.Allowed [ apply ])
                    fingerprints

            let install =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.InstallVersion(identity, two))
                    [ directTarget ]
                    (Some browse)
                    fingerprints
                |> PackageOperationPreviewScenario.preview installEvidence
                |> PackageOperationPreviewScenario.success
                |> PackageOperationPreviewScenario.targets
                |> List.exactlyOne

            PackageTargetPreview.change install
            |> should equal (PackageTargetChange.Install(None, ProposedPackageState.Direct two))

            PackageTargetPreview.ownerFiles install
            |> NonEmptyList.toList
            |> should equal [ directProject ]

            (PackageTargetPreview.impact install).Restore
            |> should
                equal
                (PackageRestoreImpact.RequiredWithUnknownOutcome PackageGraphFreshness.Current)

            (PackageTargetPreview.impact install).SourceMapping
            |> should
                equal
                (PackageSourceMappingImpact.BrowseSourceDoesNotConstrainApply(browse, [ apply ]))

            match (PackageTargetPreview.impact install).Metadata with
            | PackageMetadataImpact.Known(dependencies, deprecation, vulnerabilities, license) ->
                dependencies
                |> List.map (fst >> _.Value)
                |> should equal [ "Example.Dependency" ]

                deprecation |> should not' (equal PackageDeprecation.NotDeprecated)

                vulnerabilities
                |> List.map _.Severity
                |> should equal [ PackageVulnerabilitySeverity.High ]

                license |> should equal (Some "MIT")
            | PackageMetadataImpact.Unknown -> failwith "Expected known package metadata."

            let centralInstalled =
                PackageOperationPreviewScenario.installed
                    centralTarget
                    identity
                    (InstalledPackageState.CentrallyManagedDirect(
                        PackageVersionSelection.Exact one,
                        one,
                        centralOwner
                    ))

            let updateEvidence =
                PackageOperationPreviewScenario.evidence
                    root
                    [ centralSnapshot ]
                    [ PackageOperationPreviewScenario.graph centralTarget [ centralInstalled ] ]
                    (Some metadata)
                    (PackageSourceMappingPolicy.Allowed [ apply ])
                    fingerprints

            let update =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.UpdateLatest identity)
                    [ centralTarget ]
                    None
                    fingerprints
                |> PackageOperationPreviewScenario.preview updateEvidence
                |> PackageOperationPreviewScenario.success
                |> PackageOperationPreviewScenario.targets
                |> List.exactlyOne

            PackageTargetPreview.change update
            |> should
                equal
                (PackageTargetChange.Update(
                    centralInstalled.State,
                    ProposedPackageState.CentrallyManaged(two, centralOwner)
                ))

            PackageTargetPreview.ownerFiles update
            |> NonEmptyList.toList
            |> should equal [ centralOwner ]

            let directInstalled =
                PackageOperationPreviewScenario.installed
                    directTarget
                    identity
                    (InstalledPackageState.Direct(PackageVersionSelection.Exact one, one))

            let uninstallEvidence =
                { installEvidence with
                    Installed =
                        [ PackageOperationPreviewScenario.graph directTarget [ directInstalled ] ]
                    Details = Map.empty }

            let uninstall =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.Uninstall identity)
                    [ directTarget ]
                    None
                    fingerprints
                |> PackageOperationPreviewScenario.preview uninstallEvidence
                |> PackageOperationPreviewScenario.success
                |> PackageOperationPreviewScenario.targets
                |> List.exactlyOne

            PackageTargetPreview.change uninstall
            |> should equal (PackageTargetChange.Uninstall directInstalled.State)

            (PackageTargetPreview.impact uninstall).Metadata
            |> should equal PackageMetadataImpact.Unknown
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``project targets expand into deterministic framework and project ordering across a multi-project preview``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let selected = PackageOperationPreviewScenario.version "3.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"
            let beta = Path.Combine(root, "Beta.csproj")
            let alpha = Path.Combine(root, "Alpha.csproj")

            for project in [ beta; alpha ] do
                PackageOperationPreviewScenario.write project "original"

            let snapshots =
                [ for project in [ beta; alpha ] do
                      yield
                          PackageOperationPreviewScenario.snapshot
                              project
                              [ PackageOperationPreviewScenario.dimension
                                    project
                                    "net9.0"
                                    identity
                                    PackageOperationPreviewScenario.directShape
                                PackageOperationPreviewScenario.dimension
                                    project
                                    "net10.0"
                                    identity
                                    PackageOperationPreviewScenario.directShape ] ]

            let graphs =
                [ for project in [ beta; alpha ] do
                      for framework in [ "net9.0"; "net10.0" ] do
                          yield
                              PackageOperationPreviewScenario.graph
                                  (PackageOperationPreviewScenario.target project framework)
                                  [] ]

            let fingerprints = Map [ alpha, "alpha"; beta, "beta" ]

            let evidence =
                PackageOperationPreviewScenario.evidence
                    root
                    snapshots
                    graphs
                    (Some(PackageOperationPreviewScenario.details identity selected feed))
                    (PackageSourceMappingPolicy.Allowed [ feed ])
                    fingerprints

            let projectTargets =
                [ PackageTargetScope.Project(PackageOperationPreviewScenario.project beta)
                  PackageTargetScope.Project(PackageOperationPreviewScenario.project alpha) ]

            let request =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.InstallLatest identity)
                    projectTargets
                    None
                    fingerprints

            let first =
                PackageOperationPreviewScenario.preview evidence request
                |> PackageOperationPreviewScenario.success

            let second =
                PackageOperationPreviewScenario.preview
                    { evidence with
                        Installed = List.rev evidence.Installed
                        Evaluations = List.rev evidence.Evaluations }
                    request
                |> PackageOperationPreviewScenario.success

            PackageOperationPreviewScenario.targets first
            |> List.map PackageTargetPreview.target
            |> should
                equal
                [ PackageOperationPreviewScenario.target alpha "net10.0"
                  PackageOperationPreviewScenario.target alpha "net9.0"
                  PackageOperationPreviewScenario.target beta "net10.0"
                  PackageOperationPreviewScenario.target beta "net9.0" ]

            first |> should equal second

            PackagePreview.ownerFiles first
            |> NonEmptyList.toList
            |> should equal [ alpha; beta ]
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``consolidation previews classify targets already on below above and unusable for one explicit destination``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let destination = PackageOperationPreviewScenario.version "2.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"

            let versions =
                [ "Already.csproj", Some "2.0.0"
                  "Below.csproj", Some "1.0.0"
                  "Above.csproj", Some "3.0.0"
                  "Unusable.csproj", None ]

            let projects = versions |> List.map (fun (name, _) -> Path.Combine(root, name))

            projects
            |> List.iter (fun path -> PackageOperationPreviewScenario.write path "original")

            let targets =
                projects
                |> List.map (fun project ->
                    PackageOperationPreviewScenario.target project "net10.0")

            let snapshots =
                projects
                |> List.map (fun project ->
                    PackageOperationPreviewScenario.snapshot
                        project
                        [ PackageOperationPreviewScenario.dimension
                              project
                              "net10.0"
                              identity
                              PackageOperationPreviewScenario.directShape ])

            let graphs =
                List.zip targets versions
                |> List.map (fun (target, (_, current)) ->
                    let packages =
                        current
                        |> Option.map (fun value ->
                            let version = PackageOperationPreviewScenario.version value

                            PackageOperationPreviewScenario.installed
                                target
                                identity
                                (InstalledPackageState.Direct(
                                    PackageVersionSelection.Exact version,
                                    version
                                )))
                        |> Option.toList

                    PackageOperationPreviewScenario.graph target packages)

            let fingerprints = projects |> List.map (fun path -> path, path) |> Map

            let evidence =
                PackageOperationPreviewScenario.evidence
                    root
                    snapshots
                    graphs
                    (Some(PackageOperationPreviewScenario.details identity destination feed))
                    (PackageSourceMappingPolicy.Allowed [ feed ])
                    fingerprints

            let preview =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.ConsolidateVersion(identity, destination))
                    targets
                    None
                    fingerprints
                |> PackageOperationPreviewScenario.preview evidence
                |> PackageOperationPreviewScenario.success

            PackageOperationPreviewScenario.targets preview
            |> List.map (fun target ->
                Path.GetFileName(
                    (PackageOperationPreviewScenario.targetProjectPath (
                        PackageTargetPreview.target target
                    ))
                ),
                PackageTargetPreview.change target)
            |> should
                equal
                [ "Above.csproj",
                  PackageTargetChange.Consolidate(
                      Some(
                          InstalledPackageState.Direct(
                              PackageVersionSelection.Exact(
                                  PackageOperationPreviewScenario.version "3.0.0"
                              ),
                              PackageOperationPreviewScenario.version "3.0.0"
                          )
                      ),
                      PackageConsolidationPosition.AboveDestination,
                      Some(ProposedPackageState.Direct destination)
                  )
                  "Already.csproj",
                  PackageTargetChange.Consolidate(
                      Some(
                          InstalledPackageState.Direct(
                              PackageVersionSelection.Exact destination,
                              destination
                          )
                      ),
                      PackageConsolidationPosition.AlreadyOnDestination,
                      None
                  )
                  "Below.csproj",
                  PackageTargetChange.Consolidate(
                      Some(
                          InstalledPackageState.Direct(
                              PackageVersionSelection.Exact(
                                  PackageOperationPreviewScenario.version "1.0.0"
                              ),
                              PackageOperationPreviewScenario.version "1.0.0"
                          )
                      ),
                      PackageConsolidationPosition.BelowDestination,
                      Some(ProposedPackageState.Direct destination)
                  )
                  "Unusable.csproj",
                  PackageTargetChange.Consolidate(None, PackageConsolidationPosition.Unusable, None) ]
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``known mapping conflicts block preview while unknown transitive mapping remains explicit and never becomes apply allowed``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let selected = PackageOperationPreviewScenario.version "2.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"
            let project = Path.Combine(root, "Example.csproj")
            PackageOperationPreviewScenario.write project "original"
            let target = PackageOperationPreviewScenario.target project "net10.0"
            let fingerprints = Map [ project, "hash" ]

            let snapshot =
                PackageOperationPreviewScenario.snapshot
                    project
                    [ PackageOperationPreviewScenario.dimension
                          project
                          "net10.0"
                          identity
                          PackageOperationPreviewScenario.directShape ]

            let baseline =
                PackageOperationPreviewScenario.evidence
                    root
                    [ snapshot ]
                    [ PackageOperationPreviewScenario.graph target [] ]
                    (Some(PackageOperationPreviewScenario.details identity selected feed))
                    (PackageSourceMappingPolicy.Allowed [ feed ])
                    fingerprints

            let request =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.InstallVersion(identity, selected))
                    [ target ]
                    None
                    fingerprints

            let conflict =
                PackageOperationPreviewScenario.preview
                    { baseline with
                        SourceMapping = PackageSourceMappingPolicy.KnownConflict(identity, []) }
                    request
                |> PackageOperationPreviewScenario.failure

            PackageFailure.kind conflict |> should equal PackageFailureKind.Unsupported
            PackageFailure.message conflict |> should haveSubstring "source mapping"

            let unknown =
                PackageOperationPreviewScenario.preview
                    { baseline with
                        SourceMapping =
                            PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence
                                [ feed ] }
                    request
                |> PackageOperationPreviewScenario.success
                |> PackageOperationPreviewScenario.targets
                |> List.exactlyOne

            (PackageTargetPreview.impact unknown).SourceMapping
            |> should
                equal
                (PackageSourceMappingImpact.UnknownTransitiveConsequences([ feed ], None))
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``stale restore revision and owner fingerprints fail before a package preview can be confirmed``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let selected = PackageOperationPreviewScenario.version "2.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"
            let project = Path.Combine(root, "Example.csproj")
            PackageOperationPreviewScenario.write project "original"
            let target = PackageOperationPreviewScenario.target project "net10.0"
            let fingerprints = Map [ project, "hash" ]

            let snapshot =
                PackageOperationPreviewScenario.snapshot
                    project
                    [ PackageOperationPreviewScenario.dimension
                          project
                          "net10.0"
                          identity
                          PackageOperationPreviewScenario.directShape ]

            let evidence =
                PackageOperationPreviewScenario.evidence
                    root
                    [ snapshot ]
                    [ PackageOperationPreviewScenario.graph target [] ]
                    (Some(PackageOperationPreviewScenario.details identity selected feed))
                    (PackageSourceMappingPolicy.Allowed [ feed ])
                    fingerprints

            let request =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.InstallVersion(identity, selected))
                    [ target ]
                    None
                    fingerprints

            let changedRevision =
                PackageOperationPreviewScenario.preview
                    { evidence with
                        WorkspaceRevision = "43" }
                    request
                |> PackageOperationPreviewScenario.failure

            PackageFailure.kind changedRevision
            |> should equal PackageFailureKind.StaleState

            let changedFingerprint =
                PackageOperationPreviewScenario.preview
                    { evidence with
                        FileFingerprints = Map [ project, "changed" ] }
                    request
                |> PackageOperationPreviewScenario.failure

            PackageFailure.kind changedFingerprint
            |> should equal PackageFailureKind.StaleState

            let staleGraph =
                PackageOperationPreviewScenario.preview
                    { evidence with
                        Installed =
                            [ { PackageOperationPreviewScenario.graph target [] with
                                  State = InstalledPackageGraphState.StaleRestoreGraph } ] }
                    request
                |> PackageOperationPreviewScenario.failure

            PackageFailure.kind staleGraph |> should equal PackageFailureKind.StaleState
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``imported nested conflicting conditional and transitive-pinning ownership return stable unsupported reasons``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let selected = PackageOperationPreviewScenario.version "2.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"
            let project = Path.Combine(root, "src", "Example.csproj")
            let imported = Path.Combine(root, "Directory.Build.props")
            let nested = Path.Combine(root, "src", "Directory.Packages.props")

            for path in [ project; imported; nested ] do
                PackageOperationPreviewScenario.write path "original"

            let target = PackageOperationPreviewScenario.target project "net10.0"

            let fingerprints =
                Map [ project, "project"; imported, "imported"; nested, "nested" ]

            let request operation =
                PackageOperationPreviewScenario.request root operation [ target ] None fingerprints

            let failureFor shape packages operation =
                let snapshot =
                    PackageOperationPreviewScenario.snapshot
                        project
                        [ PackageOperationPreviewScenario.dimension project "net10.0" identity shape ]

                let evidence =
                    PackageOperationPreviewScenario.evidence
                        root
                        [ snapshot ]
                        [ PackageOperationPreviewScenario.graph target packages ]
                        (Some(PackageOperationPreviewScenario.details identity selected feed))
                        (PackageSourceMappingPolicy.Allowed [ feed ])
                        fingerprints

                PackageOperationPreviewScenario.preview evidence (request operation)
                |> PackageOperationPreviewScenario.failure
                |> PackageFailure.message

            let importedShape =
                { PackageOperationPreviewScenario.directShape with
                    MembershipOwner = Some imported
                    MembershipConditions = [ "" ] }

            failureFor
                importedShape
                []
                (RequestedPackageOperation.InstallVersion(identity, selected))
            |> should haveSubstring "imported"

            let nestedShape =
                { PackageOperationPreviewScenario.centralShape nested with
                    CentralVersions = [ "1.0.0", "" ] }

            failureFor nestedShape [] (RequestedPackageOperation.InstallVersion(identity, selected))
            |> should haveSubstring "nested"

            let conflictingShape =
                { PackageOperationPreviewScenario.centralShape (
                      Path.Combine(root, "Directory.Packages.props")
                  ) with
                    CentralVersions = [ "1.0.0", ""; "2.0.0", "" ] }

            PackageOperationPreviewScenario.write
                (Path.Combine(root, "Directory.Packages.props"))
                "original"

            failureFor
                conflictingShape
                []
                (RequestedPackageOperation.InstallVersion(identity, selected))
            |> should haveSubstring "conflicting"

            let conditionalShape =
                { PackageOperationPreviewScenario.directShape with
                    MembershipConditions = [ "'$(TargetFramework)' == 'net10.0'" ] }

            failureFor
                conditionalShape
                []
                (RequestedPackageOperation.InstallVersion(identity, selected))
            |> should haveSubstring "Conditional"

            let pinnedShape =
                { PackageOperationPreviewScenario.centralShape (
                      Path.Combine(root, "Directory.Packages.props")
                  ) with
                    TransitivePinning = true }

            failureFor pinnedShape [] (RequestedPackageOperation.InstallVersion(identity, selected))
            |> should haveSubstring "transitive pinning"

            let transitive =
                PackageOperationPreviewScenario.installed
                    target
                    identity
                    (InstalledPackageState.Transitive(
                        PackageOperationPreviewScenario.version "1.0.0"
                    ))

            failureFor
                PackageOperationPreviewScenario.directShape
                [ transitive ]
                (RequestedPackageOperation.UpdateVersion(identity, selected))
            |> should haveSubstring "transitive pinning"
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``symbolic-link external and framework-provided ownership fail before preview with stable reasons``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()
        let externalRoot = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let selected = PackageOperationPreviewScenario.version "2.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"
            let actual = Path.Combine(root, "Actual.csproj")
            let link = Path.Combine(root, "Linked.csproj")
            let external = Path.Combine(externalRoot, "External.csproj")

            for path in [ actual; external ] do
                PackageOperationPreviewScenario.write path "original"

            File.CreateSymbolicLink(link, actual) |> ignore

            let failure project state =
                let target = PackageOperationPreviewScenario.target project "net10.0"

                let snapshot =
                    PackageOperationPreviewScenario.snapshot
                        project
                        [ PackageOperationPreviewScenario.dimension
                              project
                              "net10.0"
                              identity
                              PackageOperationPreviewScenario.directShape ]

                let package =
                    state |> Option.map (PackageOperationPreviewScenario.installed target identity)

                let fingerprints = Map [ project, "hash" ]

                let evidence =
                    PackageOperationPreviewScenario.evidence
                        root
                        [ snapshot ]
                        [ PackageOperationPreviewScenario.graph target (Option.toList package) ]
                        (Some(PackageOperationPreviewScenario.details identity selected feed))
                        (PackageSourceMappingPolicy.Allowed [ feed ])
                        fingerprints

                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.InstallVersion(identity, selected))
                    [ target ]
                    None
                    fingerprints
                |> PackageOperationPreviewScenario.preview evidence
                |> PackageOperationPreviewScenario.failure
                |> PackageFailure.message

            failure link None |> should haveSubstring "symbolic link"
            failure external None |> should haveSubstring "outside the workspace"

            failure
                actual
                (Some(
                    InstalledPackageState.FrameworkProvided(
                        PackageOperationPreviewScenario.version "10.0.0"
                    )
                ))
            |> should haveSubstring "Framework-provided"
        finally
            Directory.Delete(root, true)
            Directory.Delete(externalRoot, true)

    [<Fact>]
    member _.``preview is byte-for-byte read-only across project central assets restore outputs and subprocess state``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let selected = PackageOperationPreviewScenario.version "2.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"
            let project = Path.Combine(root, "Example.csproj")
            let central = Path.Combine(root, "Directory.Packages.props")
            let assets = Path.Combine(root, "obj", "project.assets.json")
            let restoreOutput = Path.Combine(root, "obj", "project.nuget.cache")

            let files =
                [ project, "<Project />"
                  central, "<Project />"
                  assets, "{ \"version\": 4 }"
                  restoreOutput, "restore-cache" ]

            files
            |> List.iter (fun (path, content) -> PackageOperationPreviewScenario.write path content)

            let before = files |> List.map (fun (path, _) -> path, File.ReadAllBytes path)
            let target = PackageOperationPreviewScenario.target project "net10.0"

            let snapshot =
                PackageOperationPreviewScenario.snapshot
                    project
                    [ PackageOperationPreviewScenario.dimension
                          project
                          "net10.0"
                          identity
                          (PackageOperationPreviewScenario.centralShape central) ]

            let fingerprints = Map [ project, "project"; central, "central" ]

            let evidence =
                PackageOperationPreviewScenario.evidence
                    root
                    [ snapshot ]
                    [ PackageOperationPreviewScenario.graph target [] ]
                    (Some(PackageOperationPreviewScenario.details identity selected feed))
                    (PackageSourceMappingPolicy.Allowed [ feed ])
                    fingerprints

            let request =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.InstallVersion(identity, selected))
                    [ target ]
                    None
                    fingerprints

            let previewPort =
                PackageOperationPreviews.create (fun _ -> async { return Ok evidence })

            previewPort request
            |> Async.RunSynchronously
            |> PackageOperationPreviewScenario.success
            |> PackagePreview.workspaceRevision
            |> should equal "42"


            before
            |> List.iter (fun (path, bytes) -> File.ReadAllBytes path |> should equal bytes)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``latest and uninstall metadata must match package versions while compatible dependency groups reduce to the selected framework``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let other = PackageOperationPreviewScenario.package "Other.Package"
            let one = PackageOperationPreviewScenario.version "1.0.0"
            let two = PackageOperationPreviewScenario.version "2.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"
            let project = Path.Combine(root, "Example.csproj")
            PackageOperationPreviewScenario.write project "original"
            let target = PackageOperationPreviewScenario.target project "net10.0"
            let fingerprints = Map [ project, "hash" ]

            let snapshot =
                PackageOperationPreviewScenario.snapshot
                    project
                    [ PackageOperationPreviewScenario.dimension
                          project
                          "net10.0"
                          identity
                          PackageOperationPreviewScenario.directShape ]

            let graph = PackageOperationPreviewScenario.graph target []
            let wrongDetails = PackageOperationPreviewScenario.details other two feed

            let baseline =
                PackageOperationPreviewScenario.evidence
                    root
                    [ snapshot ]
                    [ graph ]
                    None
                    (PackageSourceMappingPolicy.Allowed [ feed ])
                    fingerprints

            let latestFailure =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.InstallLatest identity)
                    [ target ]
                    None
                    fingerprints
                |> PackageOperationPreviewScenario.preview
                    { baseline with
                        Details = Map [ two, wrongDetails ] }
                |> PackageOperationPreviewScenario.failure

            PackageFailure.kind latestFailure |> should equal PackageFailureKind.Unsupported

            let installed =
                PackageOperationPreviewScenario.installed
                    target
                    identity
                    (InstalledPackageState.Direct(PackageVersionSelection.Exact one, one))

            let compatible =
                { PackageOperationPreviewScenario.details identity one feed with
                    DependencyGroups =
                        Map
                            [ Some(PackageOperationPreviewScenario.framework "netstandard2.0"),
                              [ PackageOperationPreviewScenario.package "Compatible.Dependency",
                                PackageOperationPreviewScenario.range "[1.0.0, )" ] ] }

            let uninstall =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.Uninstall identity)
                    [ target ]
                    None
                    fingerprints
                |> PackageOperationPreviewScenario.preview
                    { baseline with
                        Installed = [ PackageOperationPreviewScenario.graph target [ installed ] ]
                        Details = Map [ one, compatible ] }
                |> PackageOperationPreviewScenario.success
                |> PackageOperationPreviewScenario.targets
                |> List.exactlyOne

            match (PackageTargetPreview.impact uninstall).Metadata with
            | PackageMetadataImpact.Known(dependencies, _, _, _) ->
                dependencies
                |> List.map (fst >> _.Value)
                |> should equal [ "Compatible.Dependency" ]
            | PackageMetadataImpact.Unknown -> failwith "Expected compatible metadata."

            let mismatched =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.Uninstall identity)
                    [ target ]
                    None
                    fingerprints
                |> PackageOperationPreviewScenario.preview
                    { baseline with
                        Installed = [ PackageOperationPreviewScenario.graph target [ installed ] ]
                        Details =
                            Map [ two, PackageOperationPreviewScenario.details identity two feed ] }
                |> PackageOperationPreviewScenario.success
                |> PackageOperationPreviewScenario.targets
                |> List.exactlyOne

            (PackageTargetPreview.impact mismatched).Metadata
            |> should equal PackageMetadataImpact.Unknown
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``transitive mapping conflict and unverifiable graph remain unknown while unresolved direct declarations block install``
        ()
        =
        let root = PackageOperationPreviewScenario.temporaryDirectory ()

        try
            let identity = PackageOperationPreviewScenario.package "Example.Package"
            let transitive = PackageOperationPreviewScenario.package "Transitive.Package"
            let selected = PackageOperationPreviewScenario.version "2.0.0"
            let feed = PackageOperationPreviewScenario.source "feed"
            let project = Path.Combine(root, "Example.csproj")
            PackageOperationPreviewScenario.write project "original"
            let target = PackageOperationPreviewScenario.target project "net10.0"
            let fingerprints = Map [ project, "hash" ]

            let snapshot =
                PackageOperationPreviewScenario.snapshot
                    project
                    [ PackageOperationPreviewScenario.dimension
                          project
                          "net10.0"
                          identity
                          PackageOperationPreviewScenario.directShape ]

            let request =
                PackageOperationPreviewScenario.request
                    root
                    (RequestedPackageOperation.InstallVersion(identity, selected))
                    [ target ]
                    None
                    fingerprints

            let baseline =
                PackageOperationPreviewScenario.evidence
                    root
                    [ snapshot ]
                    [ { PackageOperationPreviewScenario.graph target [] with
                          State = InstalledPackageGraphState.UnverifiablyFreshRestoreGraph } ]
                    None
                    (PackageSourceMappingPolicy.KnownConflict(transitive, [ feed ]))
                    fingerprints

            let preview =
                PackageOperationPreviewScenario.preview baseline request
                |> PackageOperationPreviewScenario.success
                |> PackageOperationPreviewScenario.targets
                |> List.exactlyOne

            PackageTargetPreview.graphFreshness preview
            |> should equal PackageGraphFreshness.AwaitingBackgroundRestore

            (PackageTargetPreview.impact preview).SourceMapping
            |> should
                equal
                (PackageSourceMappingImpact.UnknownTransitiveConsequences([ feed ], None))

            (PackageTargetPreview.impact preview).Restore
            |> should
                equal
                (PackageRestoreImpact.RequiredWithUnknownOutcome(
                    PackageGraphFreshness.AwaitingBackgroundRestore
                ))

            let unresolvedStates =
                [ InstalledPackageState.UnresolvedDirect(PackageVersionSelection.Exact selected)
                  InstalledPackageState.UnresolvedCentrallyManagedDirect(
                      PackageVersionSelection.Exact selected,
                      Path.Combine(root, "Directory.Packages.props")
                  ) ]

            for state in unresolvedStates do
                let unresolved = PackageOperationPreviewScenario.installed target identity state

                let blocked =
                    PackageOperationPreviewScenario.preview
                        { baseline with
                            Installed =
                                [ PackageOperationPreviewScenario.graph target [ unresolved ] ]
                            SourceMapping = PackageSourceMappingPolicy.Allowed [ feed ] }
                        request
                    |> PackageOperationPreviewScenario.failure

                PackageFailure.kind blocked |> should equal PackageFailureKind.InvalidRequest
                PackageFailure.message blocked |> should haveSubstring "already declared"
        finally
            Directory.Delete(root, true)
