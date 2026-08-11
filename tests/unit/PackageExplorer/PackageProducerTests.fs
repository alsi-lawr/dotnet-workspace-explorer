namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open FsUnit.Xunit
open Xunit

module private PackageProducerScenario =
    let package value =
        PackageId.create value |> Result.defaultWith (failwithf "%A")

    let version value =
        NuGetVersion.create value |> Result.defaultWith (failwithf "%A")

    let source value =
        PackageSourceId.create value |> Result.defaultWith (failwithf "%A")

    let target path =
        PackageProjectId.create path
        |> Result.defaultWith (failwithf "%A")
        |> PackageTargetScope.Project

    let workspaceTarget path =
        PackageWorkspaceTarget.directory path |> Result.defaultWith (failwithf "%A")

    let request path value =
        { Id = PackageRequestId.newId ()
          Target = workspaceTarget path
          Value = value }

    let installed path identity current =
        let resolved = version current

        { Identity = package identity
          Target = target path
          State = InstalledPackageState.Direct(PackageVersionSelection.Exact resolved, resolved)
          Declaration = None }

    let graph path state packages =
        { Target = target path
          State = state
          Packages = packages }

    let failure kind =
        PackageFailure.create kind "The fake package producer failed." PackageFailureRetry.Transient
        |> Result.defaultWith (failwithf "%A")

    let details source identity versions =
        let selected = versions |> List.head

        { Summary =
            { Identity = package identity
              Version = selected
              Description = None
              Summary = None
              Tags = []
              Authors = []
              Owners = []
              Source = source }
          Versions = versions
          Authors = []
          ProjectUrl = None
          License = None
          LicenseUrl = None
          ReadmeUrl = None
          ReadmeContent = None
          DependencyGroups = Map.empty
          Deprecation = PackageDeprecation.NotDeprecated
          Vulnerabilities = [] }

    let collectBatches producer request =
        let batches = ResizeArray<_>()

        let sink _ batch =
            async { batches.Add(NonEmptyList.toList batch) }

        let outcome = producer request sink |> Async.RunSynchronously
        batches |> Seq.toList, outcome

type PackageProducerTests() =
    [<Fact>]
    member _.``installed inventory emits package-less rows and cancellation after a batch is terminal``
        ()
        =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-installed-producer-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            let firstProject = Path.Combine(directory, "First.fsproj")
            let secondProject = Path.Combine(directory, "Second.fsproj")
            let requests = ConcurrentDictionary<PackageRequestId, CancellationTokenSource>()
            let request = PackageProducerScenario.request directory ()
            let observed = ResizeArray<InstalledPackageEntry>()

            let firstBatch =
                TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

            let resolve _ =
                async.Return(Ok [ firstProject; secondProject ])

            let evaluate _ project _ =
                async {
                    let packages =
                        if project = firstProject then
                            [ PackageProducerScenario.installed project "First.Package" "1.0.0" ]
                        else
                            []

                    return
                        Ok
                            [ PackageProducerScenario.graph
                                  project
                                  InstalledPackageGraphState.Current
                                  packages ]
                }

            let sink cancellation batch =
                async {
                    observed.AddRange(NonEmptyList.toList batch)
                    firstBatch.TrySetResult() |> ignore
                    do! Task.Delay(Timeout.Infinite, cancellation) |> Async.AwaitTask
                }

            let running =
                NuGetInstalledPackages.streamWithFunctions
                    resolve
                    None
                    evaluate
                    requests
                    request
                    sink
                |> Async.StartAsTask

            firstBatch.Task.Wait(TimeSpan.FromSeconds 5.0) |> should equal true
            running.IsCompleted |> should equal false
            requests[request.Id].Cancel()

            match running.GetAwaiter().GetResult() with
            | Ok() -> failwith "The cancelled installed producer unexpectedly completed."
            | Error error -> PackageFailure.kind error |> should equal PackageFailureKind.Cancelled

            observed |> Seq.toList |> should haveLength 1

            let batches, inventory =
                PackageProducerScenario.collectBatches
                    (NuGetInstalledPackages.streamWithFunctions
                        resolve
                        None
                        evaluate
                        (ConcurrentDictionary()))
                    (PackageProducerScenario.request directory ())

            match inventory with
            | Ok() -> ()
            | Error error -> failwith (PackageFailure.message error)

            batches |> List.collect id |> List.choose _.Package |> should haveLength 1

            batches
            |> List.collect id
            |> List.filter (fun entry -> entry.Package.IsNone)
            |> should haveLength 1
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``restore evaluation failure follows partial output without redefining inventory``() =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-restore-producer-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            let firstProject = Path.Combine(directory, "First.fsproj")
            let secondProject = Path.Combine(directory, "Second.fsproj")
            let restored = ResizeArray<string>()

            let expectedFailure =
                PackageProducerScenario.failure PackageFailureKind.ExternalToolFailed

            let resolve _ =
                async.Return(Ok [ firstProject; secondProject ])

            let restore _ project _ =
                async {
                    restored.Add project

                    if project = secondProject then
                        return Error expectedFailure
                    else
                        return Ok()
                }

            let evaluate _ project restoreVerified =
                async {
                    restoreVerified |> should equal true

                    return
                        Ok
                            [ PackageProducerScenario.graph
                                  project
                                  InstalledPackageGraphState.Current
                                  [ PackageProducerScenario.installed
                                        project
                                        "Example.Package"
                                        "1.0.0" ] ]
                }

            let batches, outcome =
                PackageProducerScenario.collectBatches
                    (NuGetInstalledPackages.streamWithFunctions
                        resolve
                        (Some restore)
                        evaluate
                        (ConcurrentDictionary()))
                    (PackageProducerScenario.request directory ())

            batches |> List.collect id |> should haveLength 1

            match outcome with
            | Ok() -> failwith "The failed restore producer unexpectedly completed."
            | Error failure ->
                PackageFailure.code failure
                |> should equal (PackageFailure.code expectedFailure)

            restored |> Seq.toList |> should equal [ firstProject; secondProject ]
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``updates preserve row multiplicity and order while suppressing unavailable metadata``
        ()
        =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-updates-producer-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            let firstProject = Path.Combine(directory, "First.fsproj")
            let secondProject = Path.Combine(directory, "Second.fsproj")
            let thirdProject = Path.Combine(directory, "Third.fsproj")
            let first = PackageProducerScenario.installed firstProject "First.Package" "1.0.0"

            let second =
                PackageProducerScenario.installed secondProject "Second.Package" "1.0.0"

            let repeated =
                PackageProducerScenario.installed thirdProject "First.Package" "1.5.0"

            let graphs =
                [ PackageProducerScenario.graph
                      firstProject
                      InstalledPackageGraphState.Current
                      [ first ]
                  PackageProducerScenario.graph
                      secondProject
                      InstalledPackageGraphState.Current
                      [ second ]
                  PackageProducerScenario.graph
                      thirdProject
                      InstalledPackageGraphState.Current
                      [ repeated ] ]

            let sourceId = PackageProducerScenario.source "feed"

            let source =
                { Id = sourceId
                  Name = "feed"
                  Location = Uri "https://example.test/v3/index.json"
                  Availability = PackageSourceAvailability.Available }

            let metadataReads = ConcurrentDictionary<string, int>()
            let installed _ = async.Return(Ok graphs)
            let configured _ = async.Return(Ok [ source ])

            let mapping _ =
                async.Return(Ok(PackageSourceMappingPolicy.Allowed [ sourceId ]))

            let details (request: PackageRequest<PackageDetailsRequest>) =
                async {
                    let identity = request.Value.Package.Value
                    metadataReads.AddOrUpdate(identity, 1, fun _ count -> count + 1) |> ignore

                    if identity = "Second.Package" then
                        return
                            Error(
                                PackageProducerScenario.failure PackageFailureKind.SourceUnavailable
                            )
                    else
                        return
                            Ok(
                                PackageProducerScenario.details
                                    sourceId
                                    identity
                                    [ PackageProducerScenario.version "3.0.0"
                                      PackageProducerScenario.version "2.0.0"
                                      PackageProducerScenario.version "2.0.0" ]
                            )
                }

            let batches, outcome =
                PackageProducerScenario.collectBatches
                    (PackageInventories.updates
                        (ConcurrentDictionary())
                        installed
                        configured
                        mapping
                        details)
                    (PackageProducerScenario.request directory PrereleaseSelection.StableOnly)

            match outcome with
            | Ok() -> ()
            | Error error -> failwith (PackageFailure.message error)

            batches |> should haveLength 2

            batches
            |> List.collect id
            |> List.map (fun update -> update.Installed.Target)
            |> should equal [ first.Target; repeated.Target ]

            metadataReads["First.Package"] |> should equal 1
            metadataReads["Second.Package"] |> should equal 1

            batches
            |> List.collect id
            |> List.iter (fun update ->
                update.Available
                |> NonEmptyList.toList
                |> List.map _.Value
                |> should equal [ "3.0.0"; "2.0.0" ])
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``consolidation emits completed identity groups in deterministic local order``() =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-consolidation-producer-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            let graph project identity version =
                PackageProducerScenario.graph
                    project
                    InstalledPackageGraphState.Current
                    [ PackageProducerScenario.installed project identity version ]

            let graphs =
                [ graph (Path.Combine(directory, "Z.fsproj")) "Z.Package" "2.0.0"
                  graph (Path.Combine(directory, "A.fsproj")) "A.Package" "1.0.0"
                  graph (Path.Combine(directory, "Z2.fsproj")) "Z.Package" "1.0.0"
                  graph (Path.Combine(directory, "A2.fsproj")) "A.Package" "3.0.0"
                  graph (Path.Combine(directory, "Single.fsproj")) "Single.Package" "1.0.0" ]

            let batches, outcome =
                PackageProducerScenario.collectBatches
                    (PackageInventories.consolidation (ConcurrentDictionary()) (fun _ ->
                        async.Return(Ok graphs)))
                    (PackageProducerScenario.request directory ())

            match outcome with
            | Ok() -> ()
            | Error error -> failwith (PackageFailure.message error)

            batches |> should haveLength 2

            let values = batches |> List.collect id
            values |> List.map _.Identity.Value |> should equal [ "Z.Package"; "A.Package" ]

            values
            |> List.map (fun value ->
                value.CandidateVersions |> NonEmptyList.toList |> List.map _.Value)
            |> should equal [ [ "2.0.0"; "1.0.0" ]; [ "3.0.0"; "1.0.0" ] ]

            let emptyBatches, emptyOutcome =
                PackageProducerScenario.collectBatches
                    (PackageInventories.consolidation (ConcurrentDictionary()) (fun _ ->
                        async.Return(Ok [ List.last graphs ])))
                    (PackageProducerScenario.request directory ())

            emptyBatches |> should be Empty

            match emptyOutcome with
            | Ok() -> ()
            | Error error -> failwith (PackageFailure.message error)
        finally
            Directory.Delete(directory, true)
