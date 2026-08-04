namespace Dotnet.WorkspaceExplorer.PackageExplorer.IntegrationTests

#nowarn "3511"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

module private PackageRpcSessionScenario =
    let private map = RpcValue.map
    let private text value = RpcValue.String value

    let target path =
        PackageWorkspaceTarget.file path |> Result.defaultWith (failwithf "%A")

    let initializationWithLimit frameLimit capabilities =
        Request(
            1u,
            "initialize",
            map
                [ "protocolVersion",
                  map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
                  "clientInfo", map [ "name", text "session-test" ]
                  "capabilities", capabilities |> Seq.map text |> RpcValue.array
                  "limits",
                  map
                      [ "maxFrameBytes", RpcValue.Integer(int64 frameLimit)
                        "maxPageSize", RpcValue.Integer 20L ] ]
        )

    let initialization capabilities =
        initializationWithLimit 65536 capabilities

    let requestId = "11111111-1111-1111-1111-111111111111"

    let encode frames =
        frames
        |> Seq.collect (MessagePackRpcCodec.encodeFrame >> Seq.ofArray)
        |> Seq.toArray

    let decode bytes =
        let rec loop remaining frames =
            if Array.isEmpty remaining then
                List.rev frames
            else
                match
                    MessagePackRpcCodec.tryReadValueLength
                        MessagePackRpcCodec.secureLimits
                        remaining
                with
                | Error error -> failwithf "Frame length failed: %A" error
                | Ok length ->
                    let current = remaining[.. length - 1]

                    match
                        MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits current
                    with
                    | Ok(RpcFrameDecodeResult.Frame frame) ->
                        loop remaining[length..] (frame :: frames)
                    | outcome -> failwithf "Frame decode failed: %A" outcome

        loop bytes []

    let notificationErrorCode parameters =
        parameters
        |> RpcValue.tryField "error"
        |> Option.bind (RpcValue.tryField "code")

    let hasErrorCode code parameters =
        notificationErrorCode parameters = Some(RpcValue.String code)

    let failure =
        PackageFailure.create
            PackageFailureKind.Unsupported
            "Not used by this scenario."
            PackageFailureRetry.Never
        |> Result.defaultWith (failwithf "%A")

    let package value =
        PackageId.create value |> Result.defaultWith (failwithf "%A")

    let version value =
        NuGetVersion.create value |> Result.defaultWith (failwithf "%A")

    let source value =
        PackageSourceId.create value |> Result.defaultWith (failwithf "%A")

    let project path =
        PackageProjectId.create path |> Result.defaultWith (failwithf "%A")

    let packageFailure kind =
        PackageFailure.create kind "sensitive dependency detail" PackageFailureRetry.AfterUserAction
        |> Result.defaultWith (failwithf "%A")

    let installed path packageName versionValue =
        let selectedVersion = version versionValue
        let selectedTarget = PackageTargetScope.Project(project path)

        { Identity = package packageName
          Target = selectedTarget
          State =
            InstalledPackageState.Direct(
                PackageVersionSelection.Exact selectedVersion,
                selectedVersion
            )
          Declaration = None }

    let graph path packages =
        { Target = PackageTargetScope.Project(project path)
          State = InstalledPackageGraphState.Current
          Packages = packages }

    let details packageName =
        let identity = package packageName
        let selectedVersion = version "2.0.0"

        { Summary =
            { Identity = identity
              Version = selectedVersion
              Description = Some "Example package"
              Summary = None
              Tags = [ "example" ]
              Authors = [ "ALSI" ]
              Owners = [ "ALSI" ]
              Source = source "nuget.org" }
          Versions = [ selectedVersion; version "1.0.0" ]
          Authors = [ "ALSI" ]
          ProjectUrl = None
          License = Some "MIT"
          LicenseUrl = None
          ReadmeUrl = None
          ReadmeContent = Some "# Example"
          DependencyGroups = Map.empty
          Deprecation =
            PackageDeprecation.Deprecated(
                NonEmptyList.singleton "legacy",
                Some
                    { Identity = package "Replacement.Package"
                      Range =
                        NuGetVersionRange.create "[2.0.0,)"
                        |> Result.defaultWith (failwithf "%A")
                        |> Some }
            )
          Vulnerabilities = [] }

    let preview path packageName versionValue =
        let identity = package packageName
        let current = version "1.0.0"
        let proposed = version versionValue
        let selectedTarget = PackageTargetScope.Project(project path)
        let owners = NonEmptyList.singleton path

        let impact =
            { Metadata =
                PackageMetadataImpact.Known(
                    [ package "Dependency",
                      NuGetVersionRange.create "[1.0.0,)" |> Result.defaultWith (failwithf "%A") ],
                    PackageDeprecation.NotDeprecated,
                    [],
                    Some "MIT"
                )
              SourceMapping =
                PackageSourceMappingImpact.BrowseSourceDoesNotConstrainApply(
                    source "nuget.org",
                    [ source "nuget.org" ]
                )
              Restore =
                PackageRestoreImpact.RequiredWithUnknownOutcome PackageGraphFreshness.Current }

        let targetPreview =
            PackageTargetPreview.create
                selectedTarget
                (PackageTargetChange.Update(
                    InstalledPackageState.Direct(PackageVersionSelection.Exact current, current),
                    ProposedPackageState.Direct proposed
                ))
                owners
                PackageGraphFreshness.Current
                impact
            |> Result.defaultWith (failwithf "%A")

        PackagePreview.create
            StringComparison.Ordinal
            (RequestedPackageOperation.UpdateVersion(identity, proposed))
            (NonEmptyList.singleton targetPreview)
            owners
            "revision-1"
            (Map [ path, "fingerprint-1" ])
        |> Result.defaultWith (failwithf "%A")

    let batchPreview path =
        let first = preview path "First.Package" "2.0.0"
        let second = preview path "Second.Package" "3.0.0"

        let update value versionValue =
            PackageUpdateTargetPreview.create
                (package value)
                (Some(version versionValue))
                (PackagePreview.targets (if value = "First.Package" then first else second)
                 |> NonEmptyList.toList
                 |> List.exactlyOne)

        PackageUpdateBatchPreview.create
            StringComparison.Ordinal
            (NonEmptyList.create
                (update "First.Package" "2.0.0")
                [ update "Second.Package" "3.0.0" ])
            (NonEmptyList.singleton path)
            "revision-1"
            (Map [ path, "fingerprint-1" ])
        |> Result.defaultWith (failwithf "%A")

    type GatedInputStream(initial: byte array, remaining: byte array, release: Task) =
        inherit Stream()
        let mutable offset = 0
        let mutable released = false
        override _.CanRead = true
        override _.CanSeek = false
        override _.CanWrite = false
        override _.Length = int64 (initial.Length + remaining.Length)

        override _.Position
            with get () = int64 offset
            and set _ = raise (NotSupportedException())

        override _.Flush() = ()
        override _.Read(_, _, _) = raise (NotSupportedException())
        override _.Seek(_, _) = raise (NotSupportedException())
        override _.SetLength _ = raise (NotSupportedException())
        override _.Write(_, _, _) = raise (NotSupportedException())

        override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) =
            ValueTask<int>(
                task {
                    if offset < initial.Length then
                        let count = min buffer.Length (initial.Length - offset)
                        initial.AsSpan(offset, count).CopyTo buffer.Span
                        offset <- offset + count
                        return count
                    elif not released then
                        do! release.WaitAsync cancellationToken
                        released <- true

                        let count = min buffer.Length remaining.Length
                        remaining.AsSpan(0, count).CopyTo buffer.Span
                        offset <- offset + count
                        return count
                    else
                        return 0
                }
            )

    type ObservingOutputStream(expected: string list, completed: TaskCompletionSource) =
        inherit Stream()
        let bytes = new MemoryStream()
        let remaining = HashSet<string>(expected, StringComparer.Ordinal)
        override _.CanRead = false
        override _.CanSeek = false
        override _.CanWrite = true
        override _.Length = bytes.Length

        override _.Position
            with get () = bytes.Position
            and set _ = raise (NotSupportedException())

        member _.ToArray() = bytes.ToArray()
        override _.Flush() = ()
        override _.FlushAsync(_: CancellationToken) = Task.CompletedTask
        override _.Read(_, _, _) = raise (NotSupportedException())
        override _.Seek(_, _) = raise (NotSupportedException())
        override _.SetLength _ = raise (NotSupportedException())
        override _.Write(_, _, _) = raise (NotSupportedException())

        override _.WriteAsync(buffer: ReadOnlyMemory<byte>, _: CancellationToken) =
            bytes.Write buffer.Span

            match
                MessagePackRpcCodec.decodeFrame MessagePackRpcCodec.secureLimits (buffer.ToArray())
            with
            | Ok(RpcFrameDecodeResult.Frame(Notification(methodName, _))) ->
                remaining.Remove methodName |> ignore

                if remaining.Count = 0 then
                    completed.TrySetResult() |> ignore
            | _ -> ()

            ValueTask()

    let ports (refresh: RefreshInstalledPackages) : PackageCatalogPorts =
        let unsupported _ = async.Return(Error failure)

        { ConfiguredSources = fun _ -> async.Return(Ok [])
          SourceMapping = unsupported
          Search = unsupported
          Details = unsupported
          Installed = fun _ -> async.Return(Ok [])
          RefreshInstalled = refresh
          Updates = fun _ -> async.Return(Ok [])
          Consolidation = fun _ -> async.Return(Ok [])
          PreviewPrecondition = unsupported
          Preview = unsupported
          UpdateBatchPrecondition = unsupported
          PreviewUpdateBatch = unsupported
          ExecuteConfirmed = fun _ _ -> async.Return(Error failure)
          ExecuteConfirmedUpdateBatch = fun _ _ -> async.Return(Error failure)
          Cancel = fun _ -> async.Return() }

    let runStream target ports (input: Stream) =
        task {
            use output = new MemoryStream()
            use errors = new StringWriter()

            let! exitCode =
                PackageRpcServer.runWithPortsAsync
                    target
                    ports
                    input
                    output
                    errors
                    CancellationToken.None

            return exitCode, decode (output.ToArray()), errors.ToString()
        }

    let run target ports (input: byte array) =
        task {
            use inputStream = new MemoryStream(input)
            return! runStream target ports inputStream
        }

    let runObserved target ports initial remaining expectedNotifications =
        task {
            let completed =
                TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

            use input = new GatedInputStream(encode initial, encode remaining, completed.Task)
            use output = new ObservingOutputStream(expectedNotifications, completed)
            use errors = new StringWriter()

            let! exitCode =
                PackageRpcServer.runWithPortsAsync
                    target
                    ports
                    input
                    output
                    errors
                    CancellationToken.None

            return exitCode, decode (output.ToArray()), errors.ToString()
        }

    let temporaryProject () =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-package-rpc-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore
        let project = Path.Combine(directory, "Example.fsproj")
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        directory, project

[<Collection("Package installed scenarios")>]
type PackageRpcSessionTests() =
    let hasErrorCode = PackageRpcSessionScenario.hasErrorCode

    [<Fact>]
    member _.``installed restore remains background work while unrelated package requests complete``
        ()
        =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        try
            let frames =
                [ PackageRpcSessionScenario.initialization
                      [ "packages.installed.v1"; "packages.restore.v1"; "packages.sources.v1" ]
                  Request(
                      2u,
                      "package/installed",
                      RpcValue.map
                          [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId
                            "pageSize", RpcValue.Integer 20L ]
                  )
                  Request(
                      3u,
                      "package/sources",
                      RpcValue.map
                          [ "requestId", RpcValue.String "33333333-3333-3333-3333-333333333333" ]
                  )
                  Request(4u, "shutdown", RpcValue.emptyMap) ]

            let refresh _ =
                async {
                    do! Async.Sleep 60000
                    return Ok []
                }

            let exitCode, output, errors =
                PackageRpcSessionScenario.run
                    (PackageRpcSessionScenario.target project)
                    (PackageRpcSessionScenario.ports refresh)
                    (PackageRpcSessionScenario.encode frames)
                |> _.Result

            exitCode |> should equal 0
            errors |> should equal String.Empty

            output
            |> List.choose (function
                | Response(id, Ok _) -> Some id
                | _ -> None)
            |> should equal [ 1u; 2u; 3u; 4u ]
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``installed restore reports refreshed failed and cancelled terminal states``() =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        let runCase refresh cancel expectedState =
            let ports =
                { PackageRpcSessionScenario.ports refresh with
                    Cancel = cancel }

            let initial =
                [ PackageRpcSessionScenario.initialization
                      [ "packages.installed.v1"; "packages.restore.v1"; "packages.cancel.v1" ]
                  Request(
                      2u,
                      "package/installed",
                      RpcValue.map
                          [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId
                            "pageSize", RpcValue.Integer 20L ]
                  ) ]

            let initial =
                if expectedState = "cancelled" then
                    initial
                    @ [ Request(
                            3u,
                            "package/cancel",
                            RpcValue.map
                                [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId ]
                        ) ]
                else
                    initial

            let exitCode, output, errors =
                PackageRpcSessionScenario.runObserved
                    (PackageRpcSessionScenario.target project)
                    ports
                    initial
                    [ Request(4u, "shutdown", RpcValue.emptyMap) ]
                    [ "package/restore/completed" ]
                |> _.Result

            exitCode |> should equal 0
            errors |> should equal String.Empty

            let foundState =
                output
                |> List.exists (function
                    | Notification("package/restore/completed", parameters) ->
                        Some(RpcValue.String expectedState) = RpcValue.tryField "state" parameters
                    | _ -> false)

            if not foundState then
                failwithf "Expected restore state %s in %A" expectedState output

        try
            let restored =
                PackageRpcSessionScenario.graph
                    project
                    [ PackageRpcSessionScenario.installed project "Example.Package" "1.0.0" ]

            runCase (fun _ -> async.Return(Ok [ restored ])) (fun _ -> async.Return()) "refreshed"

            runCase
                (fun _ ->
                    async.Return(
                        Error(
                            PackageRpcSessionScenario.packageFailure
                                PackageFailureKind.SourceUnavailable
                        )
                    ))
                (fun _ -> async.Return())
                "failed"

            let cancellation =
                TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

            runCase
                (fun _ ->
                    async {
                        do! cancellation.Task |> Async.AwaitTask

                        return
                            Error(
                                PackageRpcSessionScenario.packageFailure
                                    PackageFailureKind.Cancelled
                            )
                    })
                (fun _ -> async { cancellation.TrySetResult() |> ignore })
                "cancelled"
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``package details prefer README content, fall back to description, and respect negotiated metadata``
        ()
        =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        try
            let detailsRequest id =
                Request(
                    id,
                    "package/details",
                    RpcValue.map
                        [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId
                          "package", RpcValue.String "Example.Package"
                          "version", RpcValue.map [ "kind", RpcValue.String "latest" ]
                          "source", RpcValue.String "nuget.org" ]
                )

            let run details capabilities =
                let ports =
                    { PackageRpcSessionScenario.ports (fun _ -> async.Return(Ok [])) with
                        Details = fun _ -> async.Return(Ok details) }

                PackageRpcSessionScenario.run
                    (PackageRpcSessionScenario.target project)
                    ports
                    (PackageRpcSessionScenario.encode
                        [ PackageRpcSessionScenario.initialization capabilities
                          detailsRequest 2u
                          Request(3u, "shutdown", RpcValue.emptyMap) ])
                |> _.Result
                |> fun (_, output, _) ->
                    output
                    |> List.pick (function
                        | Response(2u, Ok result) -> Some result
                        | _ -> None)

            let details = PackageRpcSessionScenario.details "Example.Package"
            let capabilities = [ "packages.details.v1"; "packages.readme.v1" ]
            let withReadme = run details capabilities

            RpcValue.tryField "readmeCommonMark" withReadme
            |> should equal (Some(RpcValue.String "# Example"))

            let withDescription = run { details with ReadmeContent = None } capabilities

            RpcValue.tryField "readmeCommonMark" withDescription
            |> should equal (Some(RpcValue.String "Example package"))

            let deprecation =
                RpcValue.tryField "deprecation" withReadme
                |> Option.defaultWith (fun () -> failwith "Deprecation was absent.")

            RpcValue.tryField "kind" deprecation
            |> should equal (Some(RpcValue.String "deprecated"))

            let withoutReadme = run details [ "packages.details.v1" ]
            RpcValue.tryField "readmeCommonMark" withoutReadme |> should equal None
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``update and consolidation inventories page results and cancel active requests``() =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        let runCancellation methodName capability configure =
            let cancelled =
                TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

            let operation =
                async {
                    do! cancelled.Task |> Async.AwaitTask

                    return
                        Error(PackageRpcSessionScenario.packageFailure PackageFailureKind.Cancelled)
                }

            let ports =
                configure
                    { PackageRpcSessionScenario.ports (fun _ -> async.Return(Ok [])) with
                        Cancel = fun _ -> async { cancelled.TrySetResult() |> ignore } }
                    operation

            let request =
                Request(
                    2u,
                    methodName,
                    RpcValue.map
                        [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId
                          "pageSize", RpcValue.Integer 1L ]
                )

            let exitCode, output, errors =
                PackageRpcSessionScenario.runObserved
                    (PackageRpcSessionScenario.target project)
                    ports
                    [ PackageRpcSessionScenario.initialization [ capability; "packages.cancel.v1" ]
                      request
                      Request(
                          3u,
                          "package/cancel",
                          RpcValue.map
                              [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId ]
                      ) ]
                    [ Request(4u, "shutdown", RpcValue.emptyMap) ]
                    [ $"{methodName}/completed" ]
                |> _.Result

            exitCode |> should equal 0
            errors |> should equal String.Empty

            output
            |> List.exists (function
                | Notification(name, parameters) when name = $"{methodName}/completed" ->
                    hasErrorCode "DWE-PACKAGE-CANCELLED" parameters
                | _ -> false)
            |> should equal true

        try
            runCancellation "package/updates" "packages.updates.v1" (fun ports operation ->
                { ports with
                    Updates = fun _ -> operation })

            runCancellation
                "package/consolidation"
                "packages.consolidation.v1"
                (fun ports operation ->
                    { ports with
                        Consolidation = fun _ -> operation })

            let first = PackageRpcSessionScenario.installed project "First.Package" "1.0.0"
            let second = PackageRpcSessionScenario.installed project "Second.Package" "1.0.0"
            let secondUpdate = PackageRpcSessionScenario.version "3.0.0"

            let updates =
                [ { Installed = first
                    Available = NonEmptyList.singleton (PackageRpcSessionScenario.version "2.0.0") }
                  { Installed = second
                    Available = NonEmptyList.singleton secondUpdate } ]

            let ports =
                { PackageRpcSessionScenario.ports (fun _ -> async.Return(Ok [])) with
                    Updates = fun _ -> async { return Ok updates } }

            let _, output, _ =
                PackageRpcSessionScenario.runObserved
                    (PackageRpcSessionScenario.target project)
                    ports
                    [ PackageRpcSessionScenario.initialization [ "packages.updates.v1" ]
                      Request(
                          2u,
                          "package/updates",
                          RpcValue.map
                              [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId
                                "pageSize", RpcValue.Integer 1L ]
                      ) ]
                    [ Request(3u, "shutdown", RpcValue.emptyMap) ]
                    [ "package/updates/completed" ]
                |> _.Result

            let result =
                output
                |> List.pick (function
                    | Notification("package/updates/completed", parameters) ->
                        RpcValue.tryField "result" parameters
                    | _ -> None)

            let items =
                result
                |> RpcValue.tryField "updates"
                |> Option.map (RpcValue.requireArray "updates")
                |> Option.defaultWith (fun () -> failwith "Updates were absent.")

            items.Length |> should equal 1

            RpcValue.tryField "continuation" result
            |> should equal (Some(RpcValue.String "1"))
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``single and batch previews retain impacts and confirmed execution reports recovery``
        ()
        =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        try
            let single = PackageRpcSessionScenario.preview project "Example.Package" "2.0.0"
            let batch = PackageRpcSessionScenario.batchPreview project

            let precondition =
                { WorkspaceRevision = "revision-1"
                  FileFingerprints = Map [ project, "fingerprint-1" ] }

            let operationId =
                Guid.Parse "22222222-2222-2222-2222-222222222222"
                |> PackageOperationId.create
                |> Result.defaultWith (failwithf "%A")

            let recovery =
                { Package = PackageRpcSessionScenario.package "Example.Package"
                  Target = PackageTargetScope.Project(PackageRpcSessionScenario.project project)
                  State = PackageExecutionState.Uncertain }

            let partial =
                PackageRpcSessionScenario.packageFailure PackageFailureKind.PartialRecoveryRequired
                |> PackageFailure.withRecovery [ recovery ]

            let ports =
                { PackageRpcSessionScenario.ports (fun _ -> async.Return(Ok [])) with
                    PreviewPrecondition = fun _ -> async.Return(Ok precondition)
                    Preview = fun _ -> async.Return(Ok single)
                    UpdateBatchPrecondition = fun _ -> async.Return(Ok precondition)
                    PreviewUpdateBatch = fun _ -> async.Return(Ok batch)
                    ExecuteConfirmed =
                        fun _ progress ->
                            async {
                                progress (
                                    PackageProgress.determinate
                                        operationId
                                        PackageOperationStage.Applying
                                        1
                                        2
                                    |> Result.defaultWith (failwithf "%A")
                                )

                                return Error partial
                            } }

            let target = RpcValue.map [ "project", RpcValue.String project ]

            let singleRequest =
                Request(
                    2u,
                    "package/preview",
                    RpcValue.map
                        [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId
                          "operation",
                          RpcValue.map
                              [ "kind", RpcValue.String "updateVersion"
                                "package", RpcValue.String "Example.Package"
                                "version", RpcValue.String "2.0.0" ]
                          "targets", RpcValue.array [ target ]
                          "source", RpcValue.String "nuget.org" ]
                )

            let batchRequest =
                Request(
                    3u,
                    "package/previewBatch",
                    RpcValue.map
                        [ "requestId", RpcValue.String "33333333-3333-3333-3333-333333333333"
                          "updates",
                          RpcValue.array
                              [ RpcValue.map
                                    [ "package", RpcValue.String "First.Package"
                                      "version", RpcValue.String "2.0.0"
                                      "target", target ]
                                RpcValue.map
                                    [ "package", RpcValue.String "Second.Package"
                                      "version", RpcValue.String "3.0.0"
                                      "target", target ] ] ]
                )

            let executeRequest =
                Request(
                    4u,
                    "package/execute/start",
                    RpcValue.map
                        [ "requestId", RpcValue.String "44444444-4444-4444-4444-444444444444"
                          "confirmationToken",
                          RpcValue.String(PackagePreview.confirmationToken single) ]
                )

            let exitCode, output, errors =
                PackageRpcSessionScenario.runObserved
                    (PackageRpcSessionScenario.target project)
                    ports
                    [ PackageRpcSessionScenario.initialization
                          [ "packages.preview.v1"
                            "packages.batch-preview.v1"
                            "packages.execute.v1"
                            "packages.partial-recovery.v1" ]
                      singleRequest
                      batchRequest
                      executeRequest ]
                    [ Request(6u, "shutdown", RpcValue.emptyMap) ]
                    [ "package/operations/completed" ]
                |> _.Result

            exitCode |> should equal 0
            errors |> should equal String.Empty

            let singleResult =
                output
                |> List.pick (function
                    | Response(2u, Ok result) -> Some result
                    | _ -> None)

            let firstTarget =
                singleResult
                |> RpcValue.tryField "targets"
                |> Option.map (RpcValue.requireArray "targets")
                |> Option.map Seq.head
                |> Option.defaultWith (fun () -> failwith "Single preview target was absent.")

            RpcValue.tryField "impact" firstTarget |> Option.isSome |> should equal true

            let change =
                firstTarget
                |> RpcValue.tryField "change"
                |> Option.defaultWith (fun () -> failwith "Preview change was absent.")

            RpcValue.tryField "current" change |> Option.isSome |> should equal true
            RpcValue.tryField "proposed" change |> Option.isSome |> should equal true

            let batchResult =
                output
                |> List.pick (function
                    | Response(3u, Ok result) -> Some result
                    | _ -> None)

            let batchUpdates =
                batchResult
                |> RpcValue.tryField "updates"
                |> Option.map (RpcValue.requireArray "updates")
                |> Option.defaultWith (fun () -> failwith "Batch preview updates were absent.")

            batchUpdates.Length |> should equal 2

            batchUpdates
            |> Seq.forall (fun update ->
                RpcValue.tryField "targetPreview" update
                |> Option.bind (RpcValue.tryField "impact")
                |> Option.isSome)
            |> should equal true

            output
            |> List.exists (function
                | Notification("package/operations/progress", _) -> true
                | _ -> false)
            |> should equal true

            let foundRecovery =
                output
                |> List.exists (function
                    | Notification("package/operations/completed", parameters) ->
                        hasErrorCode "DWE-PACKAGE-PARTIAL-RECOVERY" parameters
                    | _ -> false)

            if not foundRecovery then
                failwithf "Expected partial recovery completion in %A" output

            let _, staleOutput, _ =
                PackageRpcSessionScenario.run
                    (PackageRpcSessionScenario.target project)
                    ports
                    (PackageRpcSessionScenario.encode
                        [ PackageRpcSessionScenario.initialization
                              [ "packages.execute.v1"; "packages.partial-recovery.v1" ]
                          Request(
                              7u,
                              "package/execute/start",
                              RpcValue.map
                                  [ "requestId",
                                    RpcValue.String "77777777-7777-7777-7777-777777777777"
                                    "confirmationToken", RpcValue.String "STALE" ]
                          )
                          Request(8u, "shutdown", RpcValue.emptyMap) ])
                |> _.Result

            staleOutput
            |> List.exists (function
                | Response(7u, Error error) -> error.Code = "invalid_params"
                | _ -> false)
            |> should equal true
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``recoverable package framing errors stay stable and preserve the initialized session``
        ()
        =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        let rawRequest id parameters =
            Array.concat
                [ [| 0x94uy; 0uy; byte id |]
                  MessagePackRpcCodec.encodeValue (RpcValue.String "package/sources")
                  parameters ]

        let collectionHeader code count =
            [| code
               byte (count >>> 24)
               byte (count >>> 16)
               byte (count >>> 8)
               byte count |]

        try
            let wrongArity =
                MessagePackRpcCodec.encodeValue (
                    RpcValue.array
                        [ RpcValue.Unsigned 0UL
                          RpcValue.Unsigned 20UL
                          RpcValue.String "package/sources" ]
                )

            let invalidMethod =
                MessagePackRpcCodec.encodeValue (
                    RpcValue.array
                        [ RpcValue.Unsigned 0UL
                          RpcValue.Unsigned 21UL
                          RpcValue.Integer 42L
                          RpcValue.emptyMap ]
                )

            let arrayCount = 1000001
            let mapCount = 500001

            let mapItems =
                Array.init (mapCount * 3) (fun index ->
                    match index % 3 with
                    | 0 -> 0xa1uy
                    | 1 -> byte 'x'
                    | _ -> 0xc0uy)

            let recoverable =
                [ 20u,
                  "invalid_request",
                  "A request frame must contain exactly four values.",
                  wrongArity
                  21u,
                  "invalid_request",
                  "A request method must be a non-empty UTF-8 string.",
                  invalidMethod
                  22u,
                  "invalid_params",
                  "Request params must be a string-key map.",
                  MessagePackRpcCodec.encodeFrame (Request(22u, "package/sources", RpcValue.Nil))
                  23u,
                  "invalid_params",
                  "MessagePack strings must contain valid UTF-8.",
                  rawRequest 23u [| 0xa1uy; 0xffuy |]
                  24u,
                  "invalid_params",
                  "MessagePack nesting exceeds the configured limit.",
                  rawRequest 24u (Array.append (Array.replicate 65 0x91uy) [| 0xc0uy |])
                  25u,
                  "invalid_params",
                  "MessagePack arrays exceed the configured item limit. (Parameter 'value')",
                  rawRequest
                      25u
                      (Array.append
                          (collectionHeader 0xdduy arrayCount)
                          (Array.replicate arrayCount 0xc0uy))
                  26u,
                  "invalid_params",
                  "MessagePack maps exceed the configured item limit. (Parameter 'value')",
                  rawRequest 26u (Array.append (collectionHeader 0xdfuy mapCount) mapItems)
                  27u,
                  "invalid_params",
                  "MessagePack map keys must be strings. (Parameter 'value')",
                  rawRequest 27u [| 0x81uy; 1uy; 0xc0uy |]
                  28u,
                  "invalid_params",
                  "MessagePack map keys must be non-empty strings. (Parameter 'value')",
                  rawRequest 28u [| 0x81uy; 0xa0uy; 0xc0uy |]
                  29u,
                  "invalid_params",
                  "MessagePack maps cannot contain duplicate keys. (Parameter 'value')",
                  rawRequest 29u [| 0x82uy; 0xa1uy; byte 'x'; 0xc0uy; 0xa1uy; byte 'x'; 0xc0uy |]
                  30u,
                  "invalid_params",
                  "MessagePack extension values are not allowed. (Parameter 'value')",
                  rawRequest 30u [| 0xd4uy; 0uy; 0uy |] ]

            let initialization =
                PackageRpcSessionScenario.initialization [ "packages.sources.v1" ]

            let repeatedInitialization =
                match initialization with
                | Request(_, methodName, parameters) -> Request(31u, methodName, parameters)
                | _ -> failwith "The package initialization fixture is not a request."

            let shutdown = Request(32u, "shutdown", RpcValue.emptyMap)

            let input =
                [ yield MessagePackRpcCodec.encodeFrame initialization
                  yield! recoverable |> List.map (fun (_, _, _, bytes) -> bytes)
                  yield MessagePackRpcCodec.encodeFrame repeatedInitialization
                  yield MessagePackRpcCodec.encodeFrame shutdown ]
                |> Array.concat

            let exitCode, output, errors =
                PackageRpcSessionScenario.run
                    (PackageRpcSessionScenario.target project)
                    (PackageRpcSessionScenario.ports (fun _ -> async.Return(Ok [])))
                    input
                |> _.Result

            exitCode |> should equal 0
            errors |> should equal String.Empty

            let expectedErrors =
                [ yield! recoverable |> List.map (fun (id, code, message, _) -> id, code, message)
                  yield 31u, "invalid_request", "A session cannot be initialized more than once." ]

            output
            |> List.choose (function
                | Response(id, Error error) -> Some(id, error.Code, error.Message)
                | _ -> None)
            |> should equal expectedErrors

            output
            |> List.choose (function
                | Response(id, Ok _) -> Some id
                | _ -> None)
            |> should equal [ 1u; 32u ]
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``malformed and oversized package work stays bounded and preserves the session``() =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        try
            let summary =
                { Identity = PackageRpcSessionScenario.package "Large.Package"
                  Version = PackageRpcSessionScenario.version "1.0.0"
                  Description = Some(String('x', 5000))
                  Summary = None
                  Tags = []
                  Authors = []
                  Owners = []
                  Source = PackageRpcSessionScenario.source "nuget.org" }

            let ports =
                { PackageRpcSessionScenario.ports (fun _ -> async.Return(Ok [])) with
                    Search =
                        fun _ ->
                            async {
                                return
                                    Ok
                                        { Items = [ summary ]
                                          Continuation = None
                                          SourceFailures = [] }
                            } }

            let malformed =
                Request(
                    2u,
                    "package/search/start",
                    RpcValue.map
                        [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId
                          "pageSize", RpcValue.Integer 0L ]
                )

            let search =
                Request(
                    3u,
                    "package/search/start",
                    RpcValue.map
                        [ "requestId", RpcValue.String "33333333-3333-3333-3333-333333333333"
                          "pageSize", RpcValue.Integer 20L ]
                )

            let sourceRequestId = "44444444-4444-4444-4444-444444444444"

            let exitCode, output, errors =
                PackageRpcSessionScenario.runObserved
                    (PackageRpcSessionScenario.target project)
                    ports
                    [ PackageRpcSessionScenario.initializationWithLimit
                          1024
                          [ "packages.search.v1"; "packages.sources.v1" ]
                      malformed
                      search ]
                    [ Request(
                          4u,
                          "package/sources",
                          RpcValue.map [ "requestId", RpcValue.String sourceRequestId ]
                      )
                      Request(5u, "shutdown", RpcValue.emptyMap) ]
                    [ "package/search/completed" ]
                |> _.Result

            exitCode |> should equal 0
            errors |> should equal String.Empty

            output
            |> List.exists (function
                | Response(2u, Error error) ->
                    error.Code = "invalid_params"
                    && error.Message = "Package request parameters are invalid."
                | _ -> false)
            |> should equal true

            output
            |> List.exists (function
                | Notification("package/search/completed", parameters) ->
                    hasErrorCode "response_too_large" parameters
                | _ -> false)
            |> should equal true

            output
            |> List.exists (function
                | Response(4u, Ok _) -> true
                | _ -> false)
            |> should equal true
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``every asynchronous package result contains frame limits without ending the session``
        ()
        =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        try
            let single = PackageRpcSessionScenario.preview project "Example.Package" "2.0.0"

            let precondition =
                { WorkspaceRevision = "revision-1"
                  FileFingerprints = Map [ project, "fingerprint-1" ] }

            let installed =
                [ 1..80 ]
                |> List.map (fun index ->
                    PackageRpcSessionScenario.installed project $"Example.Package.{index}" "1.0.0")

            let updates =
                installed
                |> List.map (fun package ->
                    { Installed = package
                      Available =
                        NonEmptyList.singleton (PackageRpcSessionScenario.version "2.0.0") })

            let consolidations =
                installed
                |> List.map (fun package ->
                    { Identity = package.Identity
                      CurrentVersions =
                        NonEmptyList.singleton (
                            PackageRpcSessionScenario.version "1.0.0",
                            NonEmptyList.singleton package.Target
                        )
                      CandidateVersions =
                        NonEmptyList.singleton (PackageRpcSessionScenario.version "2.0.0") })

            let execution =
                { Operation =
                    Guid.Parse "22222222-2222-2222-2222-222222222222"
                    |> PackageOperationId.create
                    |> Result.defaultWith (failwithf "%A")
                  Entries =
                    installed
                    |> List.map (fun package ->
                        { Package = package.Identity
                          Target = package.Target
                          State = PackageExecutionState.Completed })
                  ChangedFiles = []
                  Restore = PackageRestoreOutcome.NotRequired }

            let largeSummary =
                { Identity = PackageRpcSessionScenario.package "Large.Package"
                  Version = PackageRpcSessionScenario.version "1.0.0"
                  Description = Some(String('x', 5000))
                  Summary = None
                  Tags = []
                  Authors = []
                  Owners = []
                  Source = PackageRpcSessionScenario.source "nuget.org" }

            let refreshedGraph = PackageRpcSessionScenario.graph project installed
            let refresh _ = async.Return(Ok [ refreshedGraph ])

            let ports =
                { PackageRpcSessionScenario.ports refresh with
                    Search =
                        fun _ ->
                            async {
                                return
                                    Ok
                                        { Items = [ largeSummary ]
                                          Continuation = None
                                          SourceFailures = [] }
                            }
                    Updates = fun _ -> async { return Ok updates }
                    Consolidation = fun _ -> async { return Ok consolidations }
                    PreviewPrecondition = fun _ -> async.Return(Ok precondition)
                    Preview = fun _ -> async.Return(Ok single)
                    ExecuteConfirmed = fun _ _ -> async { return Ok execution } }

            let requestId index =
                $"{index:D8}-1111-1111-1111-111111111111"

            let target = RpcValue.map [ "project", RpcValue.String project ]

            let initial =
                [ PackageRpcSessionScenario.initializationWithLimit
                      1024
                      [ "packages.search.v1"
                        "packages.installed.v1"
                        "packages.restore.v1"
                        "packages.updates.v1"
                        "packages.consolidation.v1"
                        "packages.preview.v1"
                        "packages.execute.v1"
                        "packages.partial-recovery.v1"
                        "packages.sources.v1" ]
                  Request(
                      2u,
                      "package/search/start",
                      RpcValue.map
                          [ "requestId", RpcValue.String(requestId 2)
                            "pageSize", RpcValue.Integer 20L ]
                  )
                  Request(
                      3u,
                      "package/installed",
                      RpcValue.map
                          [ "requestId", RpcValue.String(requestId 3)
                            "pageSize", RpcValue.Integer 20L ]
                  )
                  Request(
                      4u,
                      "package/updates",
                      RpcValue.map
                          [ "requestId", RpcValue.String(requestId 4)
                            "pageSize", RpcValue.Integer 20L ]
                  )
                  Request(
                      5u,
                      "package/consolidation",
                      RpcValue.map
                          [ "requestId", RpcValue.String(requestId 5)
                            "pageSize", RpcValue.Integer 20L ]
                  )
                  Request(
                      6u,
                      "package/preview",
                      RpcValue.map
                          [ "requestId", RpcValue.String(requestId 6)
                            "operation",
                            RpcValue.map
                                [ "kind", RpcValue.String "updateVersion"
                                  "package", RpcValue.String "Example.Package"
                                  "version", RpcValue.String "2.0.0" ]
                            "targets", RpcValue.array [ target ] ]
                  )
                  Request(
                      7u,
                      "package/execute/start",
                      RpcValue.map
                          [ "requestId", RpcValue.String(requestId 7)
                            "confirmationToken",
                            RpcValue.String(PackagePreview.confirmationToken single) ]
                  ) ]

            let exitCode, output, errors =
                PackageRpcSessionScenario.runObserved
                    (PackageRpcSessionScenario.target project)
                    ports
                    initial
                    [ Request(
                          8u,
                          "package/sources",
                          RpcValue.map [ "requestId", RpcValue.String(requestId 8) ]
                      )
                      Request(9u, "shutdown", RpcValue.emptyMap) ]
                    [ "package/search/completed"
                      "package/restore/completed"
                      "package/updates/completed"
                      "package/consolidation/completed"
                      "package/operations/completed" ]
                |> _.Result

            exitCode |> should equal 0
            errors |> should equal String.Empty

            [ "package/search/completed"
              "package/updates/completed"
              "package/consolidation/completed"
              "package/operations/completed" ]
            |> List.iter (fun methodName ->
                output
                |> List.exists (function
                    | Notification(name, parameters) when name = methodName ->
                        hasErrorCode "response_too_large" parameters
                    | _ -> false)
                |> should equal true)

            output
            |> List.exists (function
                | Notification("package/restore/completed", parameters) ->
                    Some(RpcValue.String "failed") = RpcValue.tryField "state" parameters
                    && hasErrorCode "response_too_large" parameters
                | _ -> false)
            |> should equal true

            output
            |> List.exists (function
                | Response(8u, Ok _) -> true
                | _ -> false)
            |> should equal true
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``credential and unauthorized package errors redact dependency messages``() =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        try
            let run kind =
                let ports =
                    { PackageRpcSessionScenario.ports (fun _ -> async.Return(Ok [])) with
                        Details =
                            fun _ ->
                                async.Return(Error(PackageRpcSessionScenario.packageFailure kind)) }

                PackageRpcSessionScenario.run
                    (PackageRpcSessionScenario.target project)
                    ports
                    (PackageRpcSessionScenario.encode
                        [ PackageRpcSessionScenario.initialization [ "packages.details.v1" ]
                          Request(
                              2u,
                              "package/details",
                              RpcValue.map
                                  [ "requestId", RpcValue.String PackageRpcSessionScenario.requestId
                                    "package", RpcValue.String "Private.Package"
                                    "version", RpcValue.map [ "kind", RpcValue.String "latest" ]
                                    "source", RpcValue.String "private" ]
                          )
                          Request(3u, "shutdown", RpcValue.emptyMap) ])
                |> _.Result
                |> fun (_, output, _) ->
                    output
                    |> List.pick (function
                        | Response(2u, Error error) -> Some error
                        | _ -> None)

            let authentication = run PackageFailureKind.AuthenticationRequired
            authentication.Code |> should equal "DWE-PACKAGE-AUTHENTICATION-REQUIRED"

            authentication.Message
            |> should equal "The configured package source requires authentication."

            authentication.Message |> should not' (haveSubstring "sensitive")

            let unauthorized = run PackageFailureKind.Unauthorized
            unauthorized.Code |> should equal "DWE-PACKAGE-UNAUTHORIZED"

            unauthorized.Message
            |> should equal "The configured package source rejected the request."

            unauthorized.Message |> should not' (haveSubstring "sensitive")
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``exact package pipe process starts the RPC profile without a terminal product``() =
        let directory, project = PackageRpcSessionScenario.temporaryProject ()

        try
            let assembly = typeof<ProgramInvocation>.Assembly.Location
            let start = ProcessStartInfo "dotnet"
            start.ArgumentList.Add assembly
            start.ArgumentList.Add "packages"
            start.ArgumentList.Add project
            start.ArgumentList.Add "--pipe"
            start.RedirectStandardInput <- true
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.UseShellExecute <- false

            use childProcess =
                Process.Start start
                |> Option.ofObj
                |> Option.defaultWith (fun () -> failwith "Package RPC process did not start.")

            let input =
                PackageRpcSessionScenario.encode
                    [ PackageRpcSessionScenario.initialization []
                      Request(2u, "shutdown", RpcValue.emptyMap) ]

            childProcess.StandardInput.BaseStream.Write input
            childProcess.StandardInput.Close()
            use output = new MemoryStream()
            childProcess.StandardOutput.BaseStream.CopyTo output
            let errors = childProcess.StandardError.ReadToEnd()
            childProcess.WaitForExit 10000 |> should equal true
            childProcess.ExitCode |> should equal 0
            errors |> should equal String.Empty

            PackageRpcSessionScenario.decode (output.ToArray())
            |> List.choose (function
                | Response(id, Ok _) -> Some id
                | _ -> None)
            |> should equal [ 1u; 2u ]
        finally
            Directory.Delete(directory, true)
