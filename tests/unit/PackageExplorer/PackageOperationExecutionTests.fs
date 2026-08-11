namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open FsUnit.Xunit
open Xunit

module private PackageExecutionScenario =
    type Fixture =
        { Directory: string
          Target: PackageWorkspaceTarget }

    let temporaryDirectory () =
        let path =
            Path.Combine(
                Path.GetTempPath(),
                "dotnet-workspace-explorer-tests",
                $"package-execution-{Guid.NewGuid():N}"
            )

        Directory.CreateDirectory path |> ignore

        { Directory = path
          Target = PackageWorkspaceTarget.directory path |> Result.defaultWith (failwithf "%A") }

    let write (path: string) (contents: string) = File.WriteAllText(path, contents)

    let fingerprint path =
        use stream = File.OpenRead path
        $"f:{(FileInfo path).Length}:{SHA256.HashData stream |> Convert.ToHexString}"

    let package value =
        PackageId.create value |> Result.defaultWith (failwithf "%A")

    let version value =
        NuGetVersion.create value |> Result.defaultWith (failwithf "%A")

    let target path =
        PackageProjectId.create path
        |> Result.map PackageTargetScope.Project
        |> Result.defaultWith (failwithf "%A")

    let private impact =
        { Metadata = PackageMetadataImpact.Unknown
          SourceMapping = PackageSourceMappingImpact.ApplyAllowed []
          Restore = PackageRestoreImpact.RequiredWithUnknownOutcome PackageGraphFreshness.Current }

    let updateTarget packagePath selected =
        let one = version "1.0.0"
        let scope = target packagePath

        PackageTargetPreview.create
            scope
            (PackageTargetChange.Update(
                InstalledPackageState.Direct(PackageVersionSelection.Exact one, one),
                ProposedPackageState.Direct selected
            ))
            (NonEmptyList.singleton packagePath)
            PackageGraphFreshness.Current
            impact
        |> Result.defaultWith (failwithf "%A")

    let operationPreview operation scope change ownerFiles revision =
        let targetPreview =
            PackageTargetPreview.create scope change ownerFiles PackageGraphFreshness.Current impact
            |> Result.defaultWith (failwithf "%A")

        let fingerprints =
            ownerFiles
            |> NonEmptyList.toList
            |> List.map (fun path -> path, fingerprint path)
            |> Map

        PackagePreview.create
            StringComparison.Ordinal
            operation
            (NonEmptyList.singleton targetPreview)
            ownerFiles
            revision
            fingerprints
        |> Result.defaultWith (failwithf "%A")

    let singlePreview packagePath identity selected revision =
        let targetPreview = updateTarget packagePath selected
        let ownerFiles = NonEmptyList.singleton packagePath
        let fingerprints = Map [ packagePath, fingerprint packagePath ]

        PackagePreview.create
            StringComparison.Ordinal
            (RequestedPackageOperation.UpdateVersion(identity, selected))
            (NonEmptyList.singleton targetPreview)
            ownerFiles
            revision
            fingerprints
        |> Result.defaultWith (failwithf "%A")

    let batchPreview updates revision =
        let previews =
            updates
            |> List.map (fun (identity, selected, project) ->
                PackageUpdateTargetPreview.create
                    identity
                    (Some selected)
                    (updateTarget project selected))
            |> NonEmptyList.tryCreate
            |> Option.defaultWith (fun () -> failwith "A batch fixture requires updates.")

        let owners =
            updates
            |> List.map (fun (_, _, project) -> project)
            |> List.distinct
            |> NonEmptyList.tryCreate
            |> Option.defaultWith (fun () -> failwith "A batch fixture requires owners.")

        let fingerprints =
            owners
            |> NonEmptyList.toList
            |> List.map (fun path -> path, fingerprint path)
            |> Map

        PackageUpdateBatchPreview.create
            StringComparison.Ordinal
            previews
            owners
            revision
            fingerprints
        |> Result.defaultWith (failwithf "%A")

    let confirmation preview =
        PackageConfirmation.create preview (PackagePreview.confirmationToken preview)
        |> Result.defaultWith (failwithf "%A")

    let batchConfirmation preview =
        PackageUpdateBatchConfirmation.create
            preview
            (PackageUpdateBatchPreview.confirmationToken preview)
        |> Result.defaultWith (failwithf "%A")

    let request target value =
        { Id = PackageRequestId.newId ()
          Target = target
          Value = value }

    let currentPrecondition revision owners =
        { WorkspaceRevision = revision
          FileFingerprints = owners |> List.map (fun path -> path, fingerprint path) |> Map }

    let ports revision owners runner refresh =
        let requests = ConcurrentDictionary<PackageRequestId, CancellationTokenSource>()
        let operations = ConcurrentDictionary<PackageOperationId, CancellationTokenSource>()

        let readSingle (_: PackageRequest<PackagePreviewPreconditionRequest>) =
            async { return Ok(currentPrecondition revision owners) }

        let readBatch (_: PackageRequest<PackageUpdateBatchPreconditionRequest>) =
            async { return Ok(currentPrecondition revision owners) }

        PackageOperationExecution.createWith
            requests
            operations
            { ReadPrecondition = readSingle
              ReadUpdateBatchPrecondition = readBatch
              RefreshInstalled = refresh
              RunCommand = runner }

    let projectFromArguments (arguments: string array) =
        match arguments |> Array.tryFindIndex ((=) "--project") with
        | Some index -> arguments[index + 1]
        | None -> arguments[1]

    let successfulRefresh (_: PackageRequest<unit>) _ = async { return Ok() }

    let requireFailure =
        function
        | Ok _ -> failwith "The package execution unexpectedly succeeded."
        | Error error -> error

    let requireSuccess =
        function
        | Error error ->
            failwithf "%s: %s" (PackageFailure.code error) (PackageFailure.message error)
        | Ok execution -> execution

[<Sealed>]
type PackageOperationExecutionTests() =
    [<Fact>]
    member _.``confirmed direct update invokes one closed stock vector refreshes state and reports the changed owner``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"
            let identity = PackageExecutionScenario.package "Example.Package"
            let selected = PackageExecutionScenario.version "2.0.0"

            let preview =
                PackageExecutionScenario.singlePreview project identity selected "revision-1"

            let invocations = ResizeArray<string array>()
            let mutable refreshes = 0

            let runner _ arguments _ =
                async {
                    invocations.Add arguments
                    PackageExecutionScenario.write project "after"
                    return Ok()
                }

            let refresh request sink =
                async {
                    refreshes <- refreshes + 1
                    return! PackageExecutionScenario.successfulRefresh request sink
                }

            let progress = ResizeArray<PackageOperationStage>()

            let result =
                PackageExecutionScenario.ports "revision-1" [ project ] runner refresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        (PackageProgress.stage >> progress.Add)
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireSuccess

            invocations
            |> Seq.map Array.toList
            |> Seq.toList
            |> should
                equal
                [ [ "package"; "update"; "Example.Package@2.0.0"; "--project"; project ] ]

            invocations |> Seq.collect id |> should not' (contain "--interactive")

            refreshes |> should equal 1
            result.ChangedFiles |> should equal [ project ]

            result.Entries
            |> List.map _.State
            |> should equal [ PackageExecutionState.Completed ]

            progress
            |> Seq.toList
            |> should
                equal
                [ PackageOperationStage.Preparing
                  PackageOperationStage.Applying
                  PackageOperationStage.Restoring
                  PackageOperationStage.Refreshing
                  PackageOperationStage.Completed ]
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``install uninstall and consolidation use only their closed selected-SDK package vectors``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let identity = PackageExecutionScenario.package "Example.Package"
            let one = PackageExecutionScenario.version "1.0.0"
            let two = PackageExecutionScenario.version "2.0.0"

            let framework =
                TargetFramework.create "net10.0" |> Result.defaultWith (failwithf "%A")

            let scenarios =
                [ "Install.csproj",
                  (fun project ->
                      let scope =
                          PackageTargetScope.Framework(
                              PackageProjectId.create project
                              |> Result.defaultWith (failwithf "%A"),
                              framework
                          )

                      PackageExecutionScenario.operationPreview
                          (RequestedPackageOperation.InstallVersion(identity, two))
                          scope
                          (PackageTargetChange.Install(None, ProposedPackageState.Direct two))
                          (NonEmptyList.singleton project)
                          "install-revision"),
                  [ "package"
                    "add"
                    "Example.Package"
                    "--version"
                    "2.0.0"
                    "--framework"
                    "net10.0"
                    "--project"
                    "Install.csproj" ]
                  "Uninstall.csproj",
                  (fun project ->
                      let scope = PackageExecutionScenario.target project

                      PackageExecutionScenario.operationPreview
                          (RequestedPackageOperation.Uninstall identity)
                          scope
                          (PackageTargetChange.Uninstall(
                              InstalledPackageState.Direct(PackageVersionSelection.Exact one, one)
                          ))
                          (NonEmptyList.singleton project)
                          "uninstall-revision"),
                  [ "package"; "remove"; "Example.Package"; "--project"; "Uninstall.csproj" ]
                  "Consolidate.csproj",
                  (fun project ->
                      let scope = PackageExecutionScenario.target project

                      PackageExecutionScenario.operationPreview
                          (RequestedPackageOperation.ConsolidateVersion(identity, two))
                          scope
                          (PackageTargetChange.Consolidate(
                              Some(
                                  InstalledPackageState.Direct(
                                      PackageVersionSelection.Exact one,
                                      one
                                  )
                              ),
                              PackageConsolidationPosition.BelowDestination,
                              Some(ProposedPackageState.Direct two)
                          ))
                          (NonEmptyList.singleton project)
                          "consolidate-revision"),
                  [ "package"
                    "update"
                    "Example.Package@2.0.0"
                    "--project"
                    "Consolidate.csproj" ] ]

            for fileName, createPreview, expectedRelative in scenarios do
                let project = Path.Combine(fixture.Directory, fileName)
                PackageExecutionScenario.write project "before"
                let preview = createPreview project
                let revision = PackagePreview.workspaceRevision preview
                let invocations = ResizeArray<string array>()

                let runner _ arguments _ =
                    async {
                        invocations.Add arguments
                        PackageExecutionScenario.write project "after"
                        return Ok()
                    }

                PackageExecutionScenario.ports
                    revision
                    [ project ]
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireSuccess
                |> ignore

                let expected =
                    expectedRelative
                    |> List.map (fun argument -> if argument = fileName then project else argument)

                invocations |> Seq.map Array.toList |> Seq.toList |> should equal [ expected ]

                invocations |> Seq.collect id |> should not' (contain "--interactive")
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``framework-scoped update and uninstall are rejected before the selected SDK process can start``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"
            let identity = PackageExecutionScenario.package "Example.Package"
            let one = PackageExecutionScenario.version "1.0.0"
            let two = PackageExecutionScenario.version "2.0.0"

            let projectId =
                PackageProjectId.create project |> Result.defaultWith (failwithf "%A")

            let framework =
                TargetFramework.create "net10.0" |> Result.defaultWith (failwithf "%A")

            let mutable starts = 0

            let runner _ _ _ =
                async {
                    starts <- starts + 1
                    return Ok()
                }

            let installed = InstalledPackageState.Direct(PackageVersionSelection.Exact one, one)

            let scenarios =
                [ RequestedPackageOperation.UpdateVersion(identity, two),
                  PackageTargetChange.Update(installed, ProposedPackageState.Direct two)
                  RequestedPackageOperation.Uninstall identity,
                  PackageTargetChange.Uninstall installed ]

            for operation, change in scenarios do
                let preview =
                    PackageExecutionScenario.operationPreview
                        operation
                        (PackageTargetScope.Framework(projectId, framework))
                        change
                        (NonEmptyList.singleton project)
                        "revision-1"

                let failure =
                    PackageExecutionScenario.ports
                        "revision-1"
                        [ project ]
                        runner
                        PackageExecutionScenario.successfulRefresh
                    |> fun ports ->
                        ports.Execute
                            (PackageExecutionScenario.request
                                fixture.Target
                                (PackageExecutionScenario.confirmation preview))
                            ignore
                    |> Async.RunSynchronously
                    |> PackageExecutionScenario.requireFailure

                PackageFailure.kind failure |> should equal PackageFailureKind.Unsupported

            starts |> should equal 0
            File.ReadAllText project |> should equal "before"
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``runtime-scoped install is rejected before the selected SDK process can start``() =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"
            let identity = PackageExecutionScenario.package "Example.Package"
            let selected = PackageExecutionScenario.version "2.0.0"

            let projectId =
                PackageProjectId.create project |> Result.defaultWith (failwithf "%A")

            let framework =
                TargetFramework.create "net10.0" |> Result.defaultWith (failwithf "%A")

            let runtime =
                RuntimeIdentifier.create "linux-x64" |> Result.defaultWith (failwithf "%A")

            let preview =
                PackageExecutionScenario.operationPreview
                    (RequestedPackageOperation.InstallVersion(identity, selected))
                    (PackageTargetScope.Runtime(projectId, framework, runtime))
                    (PackageTargetChange.Install(None, ProposedPackageState.Direct selected))
                    (NonEmptyList.singleton project)
                    "revision-1"

            let mutable starts = 0

            let runner _ _ _ =
                async {
                    starts <- starts + 1
                    return Ok()
                }

            let failure =
                PackageExecutionScenario.ports
                    "revision-1"
                    [ project ]
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure |> should equal PackageFailureKind.Unsupported
            starts |> should equal 0
            File.ReadAllText project |> should equal "before"
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``mismatched confirmation token is rejected before a confirmed package request can be created``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Example.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            match PackageConfirmation.create preview "not-the-preview-token" with
            | Ok _ -> failwith "The mismatched confirmation token unexpectedly succeeded."
            | Error violation ->
                violation
                |> should equal (PackageContractViolation.InvalidValue "confirmationToken")
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``changed revision rejects the confirmed preview without spawning dotnet or refreshing package state``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Example.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            let mutable starts = 0
            let mutable refreshes = 0

            let runner _ _ _ =
                async {
                    starts <- starts + 1
                    return Ok()
                }

            let refresh _ _ =
                async {
                    refreshes <- refreshes + 1
                    return Ok()
                }

            let failure =
                PackageExecutionScenario.ports "revision-2" [ project ] runner refresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure |> should equal PackageFailureKind.StaleState

            PackageFailure.recovery failure
            |> List.map _.State
            |> should equal [ PackageExecutionState.Unchanged ]

            starts |> should equal 0
            refreshes |> should equal 0
            File.ReadAllText project |> should equal "before"
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``changed owner fingerprint rejects the confirmed preview even when its workspace revision is repeated``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Example.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            PackageExecutionScenario.write project "changed-after-preview"
            let mutable starts = 0

            let runner _ _ _ =
                async {
                    starts <- starts + 1
                    return Ok()
                }

            let failure =
                PackageExecutionScenario.ports
                    "revision-1"
                    [ project ]
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure |> should equal PackageFailureKind.StaleState
            starts |> should equal 0
            File.ReadAllText project |> should equal "changed-after-preview"
        finally
            Directory.Delete(fixture.Directory, true)

    [<Theory>]
    [<InlineData("authentication-required")>]
    [<InlineData("unauthorized")>]
    member _.``source authorization failures remain typed redacted and recoverable for every package client``
        (outcome: string)
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Private.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            let commandFailure, expectedKind, expectedCode =
                if outcome = "authentication-required" then
                    DotnetPackageCommandFailure.AuthenticationRequired,
                    PackageFailureKind.AuthenticationRequired,
                    "DWE-PACKAGE-AUTHENTICATION-REQUIRED"
                else
                    DotnetPackageCommandFailure.Unauthorized,
                    PackageFailureKind.Unauthorized,
                    "DWE-PACKAGE-UNAUTHORIZED"

            let runner _ _ _ = async { return Error commandFailure }

            let failure =
                PackageExecutionScenario.ports
                    "revision-1"
                    [ project ]
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure |> should equal expectedKind
            PackageFailure.code failure |> should equal expectedCode

            PackageFailure.message(failure).Contains("Private.Package", StringComparison.Ordinal)
            |> should equal false

            PackageFailure.recovery failure
            |> List.map _.State
            |> should equal [ PackageExecutionState.Unchanged ]
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``restore failure preserves the applied owner and reports the completed target without claiming rollback``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Example.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            let runner _ _ _ =
                async {
                    PackageExecutionScenario.write project "applied"
                    return Ok()
                }

            let refreshFailure =
                PackageFailure.create
                    PackageFailureKind.ExternalToolFailed
                    "The dotnet restore command failed."
                    PackageFailureRetry.Transient
                |> Result.defaultWith (failwithf "%A")

            let refresh _ _ = async { return Error refreshFailure }

            let failure =
                PackageExecutionScenario.ports "revision-1" [ project ] runner refresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure
            |> should equal PackageFailureKind.ExternalToolFailed

            PackageFailure.recovery failure
            |> List.map _.State
            |> should equal [ PackageExecutionState.Completed ]

            File.ReadAllText project |> should equal "applied"
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``multi-package failure compensates completed and failed owners and leaves later targets unchanged``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let projects =
                [ "Alpha.csproj"; "Beta.csproj"; "Gamma.csproj" ]
                |> List.map (fun name ->
                    let path = Path.Combine(fixture.Directory, name)
                    PackageExecutionScenario.write path $"before-{name}"
                    path)

            let updates =
                [ PackageExecutionScenario.package "Alpha.Package",
                  PackageExecutionScenario.version "2.0.0",
                  projects[0]
                  PackageExecutionScenario.package "Beta.Package",
                  PackageExecutionScenario.version "3.0.0",
                  projects[1]
                  PackageExecutionScenario.package "Gamma.Package",
                  PackageExecutionScenario.version "4.0.0",
                  projects[2] ]

            let preview = PackageExecutionScenario.batchPreview updates "revision-1"
            let invocations = ResizeArray<string array>()

            let runner _ arguments _ =
                async {
                    invocations.Add arguments
                    let project = PackageExecutionScenario.projectFromArguments arguments
                    PackageExecutionScenario.write project "mutated"

                    return
                        if invocations.Count = 2 then
                            Error DotnetPackageCommandFailure.Failed
                        else
                            Ok()
                }

            let failure =
                PackageExecutionScenario.ports
                    "revision-1"
                    projects
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.ExecuteUpdateBatch
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.batchConfirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            invocations
            |> Seq.map (fun arguments -> arguments[2])
            |> Seq.toList
            |> should equal [ "Alpha.Package@2.0.0"; "Beta.Package@3.0.0" ]

            PackageFailure.kind failure
            |> should equal PackageFailureKind.ExternalToolFailed

            PackageFailure.recovery failure
            |> List.map (fun entry -> entry.Package.Value, entry.State)
            |> should
                equal
                [ "Alpha.Package", PackageExecutionState.Compensated
                  "Beta.Package", PackageExecutionState.Compensated
                  "Gamma.Package", PackageExecutionState.Unchanged ]

            projects
            |> List.map File.ReadAllText
            |> should equal [ "before-Alpha.csproj"; "before-Beta.csproj"; "before-Gamma.csproj" ]
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``failed owner recovery reports an uncertain target and requires partial recovery``() =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Example.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            let runner _ _ _ =
                async {
                    File.Delete project
                    Directory.CreateDirectory project |> ignore
                    return Error DotnetPackageCommandFailure.Failed
                }

            let failure =
                PackageExecutionScenario.ports
                    "revision-1"
                    [ project ]
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure
            |> should equal PackageFailureKind.PartialRecoveryRequired

            PackageFailure.recovery failure
            |> List.map _.State
            |> should equal [ PackageExecutionState.Uncertain ]

            Directory.Exists project |> should equal true
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``unpreviewed source mutation stops execution reports partial recovery and preserves the unexpected file``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            let unexpected = Path.Combine(fixture.Directory, "Unexpected.cs")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Example.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            let runner _ _ _ =
                async {
                    PackageExecutionScenario.write project "mutated"
                    PackageExecutionScenario.write unexpected "preserve me"
                    return Ok()
                }

            let failure =
                PackageExecutionScenario.ports
                    "revision-1"
                    [ project ]
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure
            |> should equal PackageFailureKind.PartialRecoveryRequired

            PackageFailure.recovery failure
            |> List.map _.State
            |> should equal [ PackageExecutionState.Compensated ]

            File.ReadAllText project |> should equal "before"
            File.ReadAllText unexpected |> should equal "preserve me"
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``failed command with an unpreviewed mutation stops the batch and reports partial recovery before later commands start``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let projects =
                [ "Alpha.csproj"; "Beta.csproj" ]
                |> List.map (fun name ->
                    let path = Path.Combine(fixture.Directory, name)
                    PackageExecutionScenario.write path $"before-{name}"
                    path)

            let updates =
                [ PackageExecutionScenario.package "Alpha.Package",
                  PackageExecutionScenario.version "2.0.0",
                  projects[0]
                  PackageExecutionScenario.package "Beta.Package",
                  PackageExecutionScenario.version "3.0.0",
                  projects[1] ]

            let preview = PackageExecutionScenario.batchPreview updates "revision-1"
            let unexpected = Path.Combine(fixture.Directory, "Unexpected.cs")
            let invocations = ResizeArray<string array>()

            let runner _ arguments _ =
                async {
                    invocations.Add arguments
                    PackageExecutionScenario.write projects[0] "mutated"
                    PackageExecutionScenario.write unexpected "preserve me"
                    return Error DotnetPackageCommandFailure.Failed
                }

            let failure =
                PackageExecutionScenario.ports
                    "revision-1"
                    projects
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.ExecuteUpdateBatch
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.batchConfirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            invocations.Count |> should equal 1

            PackageFailure.kind failure
            |> should equal PackageFailureKind.PartialRecoveryRequired

            PackageFailure.recovery failure
            |> List.map _.State
            |> should equal [ PackageExecutionState.Compensated; PackageExecutionState.Unchanged ]

            projects
            |> List.map File.ReadAllText
            |> should equal [ "before-Alpha.csproj"; "before-Beta.csproj" ]

            File.ReadAllText unexpected |> should equal "preserve me"
        finally
            Directory.Delete(fixture.Directory, true)

    [<Theory>]
    [<InlineData("bin")>]
    [<InlineData("obj")>]
    member _.``non-restore mutations below conventional output directories remain inside the protected workspace boundary``
        (outputDirectory: string)
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Example.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            let outputRoot = Path.Combine(fixture.Directory, outputDirectory)
            let output = Path.Combine(outputRoot, "Unexpected.fs")

            let runner _ _ _ =
                async {
                    PackageExecutionScenario.write project "mutated"
                    Directory.CreateDirectory outputRoot |> ignore
                    PackageExecutionScenario.write output "preserve me"
                    return Ok()
                }

            let failure =
                PackageExecutionScenario.ports
                    "revision-1"
                    [ project ]
                    runner
                    PackageExecutionScenario.successfulRefresh
                |> fun ports ->
                    ports.Execute
                        (PackageExecutionScenario.request
                            fixture.Target
                            (PackageExecutionScenario.confirmation preview))
                        ignore
                |> Async.RunSynchronously
                |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure
            |> should equal PackageFailureKind.PartialRecoveryRequired

            File.ReadAllText project |> should equal "before"
            File.ReadAllText output |> should equal "preserve me"
        finally
            Directory.Delete(fixture.Directory, true)

    [<Fact>]
    member _.``operation cancellation reaches the active command and cannot refresh or publish late success``
        ()
        =
        let fixture = PackageExecutionScenario.temporaryDirectory ()

        try
            let project = Path.Combine(fixture.Directory, "Example.csproj")
            PackageExecutionScenario.write project "before"

            let preview =
                PackageExecutionScenario.singlePreview
                    project
                    (PackageExecutionScenario.package "Example.Package")
                    (PackageExecutionScenario.version "2.0.0")
                    "revision-1"

            let started =
                TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

            let mutable refreshes = 0

            let runner _ _ cancellationToken =
                async {
                    started.TrySetResult() |> ignore

                    try
                        do! Task.Delay(Timeout.Infinite, cancellationToken) |> Async.AwaitTask
                        return Ok()
                    with :? OperationCanceledException ->
                        return Error DotnetPackageCommandFailure.Cancelled
                }

            let refresh _ _ =
                async {
                    refreshes <- refreshes + 1
                    return Ok()
                }

            let ports = PackageExecutionScenario.ports "revision-1" [ project ] runner refresh
            let operation = TaskCompletionSource<PackageOperationId>()

            let execution =
                ports.Execute
                    (PackageExecutionScenario.request
                        fixture.Target
                        (PackageExecutionScenario.confirmation preview))
                    (fun progress ->
                        operation.TrySetResult(PackageProgress.operation progress) |> ignore)
                |> Async.StartAsTask

            started.Task.Wait(TimeSpan.FromSeconds 5.0) |> should equal true

            ports.Cancel(PackageCancellation.Operation operation.Task.Result)
            |> Async.RunSynchronously

            let failure =
                execution.GetAwaiter().GetResult() |> PackageExecutionScenario.requireFailure

            PackageFailure.kind failure |> should equal PackageFailureKind.Cancelled
            refreshes |> should equal 0
            File.ReadAllText project |> should equal "before"
        finally
            Directory.Delete(fixture.Directory, true)
