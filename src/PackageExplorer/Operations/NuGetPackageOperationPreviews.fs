namespace Dotnet.WorkspaceExplorer.PackageExplorer

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
module internal NuGetPackageOperationPreviews =
    type Ports =
        { ReadPrecondition: ReadPackagePreviewPrecondition
          Preview: PreviewPackageOperation
          ReadUpdateBatchPrecondition: ReadPackageUpdateBatchPrecondition
          PreviewUpdateBatch: PreviewPackageUpdateBatch }

    let private failure kind message retry =
        PackageFailure.create kind message retry |> Result.defaultWith (failwithf "%A")

    let private workspaceRoot target =
        let path = PackageWorkspaceTarget.path target

        if Directory.Exists path then
            path
        else
            Path.GetDirectoryName path
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

    let private pathKey sensitivity =
        fun path ->
            let full = Path.GetFullPath path

            if sensitivity = FileSystemCaseSensitivity.Insensitive then
                full.ToUpperInvariant()
            else
                full

    let private evidencePaths root evaluations configFiles =
        seq {
            yield Path.Combine(root, "Directory.Packages.props")
            yield! configFiles

            for evaluation: InstalledPackageEvaluation in evaluations do
                yield evaluation.Snapshot.ProjectPath.Value
                yield! evaluation.Snapshot.WatchInputs |> Seq.map _.Value

                for dimension in evaluation.Snapshot.Dimensions do
                    yield! dimension.PackageMemberships |> Seq.map (_.DeclaringPath.Value)
                    yield! dimension.PackageVersions |> Seq.map (_.DeclaringPath.Value)
        }

    let private fingerprints sensitivity root evaluations configFiles =
        try
            let key = pathKey sensitivity

            let paths =
                evidencePaths root evaluations configFiles
                |> Seq.map Path.GetFullPath
                |> Seq.toList

            let ambiguous =
                paths
                |> List.groupBy key
                |> List.exists (fun (_, matches) -> matches |> List.distinct |> List.length > 1)

            if ambiguous then
                failure
                    PackageFailureKind.Unsupported
                    "Package preview inputs contain ambiguous path identities."
                    PackageFailureRetry.AfterUserAction
                |> Error
            else
                paths
                |> List.distinctBy key
                |> List.sortBy key
                |> List.map (fun path ->
                    ArtifactFiles.fingerprint path |> Result.map (fun value -> path, value))
                |> List.fold
                    (fun state item ->
                        state
                        |> Result.bind (fun values ->
                            item |> Result.map (fun value -> value :: values)))
                    (Ok [])
                |> Result.map (List.rev >> Map.ofList)
                |> Result.mapError (fun _ ->
                    failure
                        PackageFailureKind.Unsupported
                        "A package preview input could not be fingerprinted safely."
                        PackageFailureRetry.AfterUserAction)
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException ->
            Error(
                failure
                    PackageFailureKind.Unsupported
                    "A package preview input could not be read safely."
                    PackageFailureRetry.AfterUserAction
            )

    let private revision sensitivity fingerprints =
        let key = pathKey sensitivity

        fingerprints
        |> Map.toList
        |> List.sortBy (fst >> key)
        |> List.map (fun (path, fingerprint) -> $"{key path}\u0000{fingerprint}")
        |> String.concat "\u0001"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    let private readBase (evaluator: ProjectEvaluator) requestId target =
        async {
            let unitRequest =
                { Id = requestId
                  Target = target
                  Value = () }

            let! evaluated =
                NuGetInstalledPackages.readEvaluationWithEvaluator evaluator unitRequest

            match evaluated with
            | Error error -> return Error error
            | Ok values ->
                let root = workspaceRoot target
                let sensitivity = FileSystemCaseSensitivityDetector.DetectFromExistingPath root

                let catalogs =
                    values
                    |> List.map (fun value ->
                        PackageWorkspaceTarget.file value.Snapshot.ProjectPath.Value
                        |> Result.mapError (fun _ ->
                            failure
                                PackageFailureKind.Unsupported
                                "A selected project target is invalid."
                                PackageFailureRetry.Never)
                        |> Result.bind NuGetSources.loadCatalog)

                match
                    catalogs
                    |> List.tryPick (function
                        | Error error -> Some error
                        | _ -> None)
                with
                | Some error -> return Error error
                | None ->
                    let configFiles =
                        catalogs |> List.choose Result.toOption |> List.collect _.ConfigFiles

                    match fingerprints sensitivity root values configFiles with
                    | Error error -> return Error error
                    | Ok fileFingerprints ->
                        return
                            Ok(
                                root,
                                sensitivity,
                                values,
                                { WorkspaceRevision = revision sensitivity fileFingerprints
                                  FileFingerprints = fileFingerprints }
                            )
        }

    let private packageOf =
        function
        | RequestedPackageOperation.InstallLatest package
        | RequestedPackageOperation.InstallVersion(package, _)
        | RequestedPackageOperation.UpdateLatest package
        | RequestedPackageOperation.UpdateVersion(package, _)
        | RequestedPackageOperation.Uninstall package
        | RequestedPackageOperation.ConsolidateVersion(package, _) -> package

    let private transitivePackages (evaluations: InstalledPackageEvaluation list) =
        evaluations
        |> List.collect _.Graphs
        |> List.collect _.Packages
        |> List.choose (fun package ->
            match package.State with
            | InstalledPackageState.Transitive _ -> Some package.Identity
            | _ -> None)
        |> List.distinct

    let private versions
        (operation: RequestedPackageOperation)
        (evaluations: InstalledPackageEvaluation list)
        =
        match operation with
        | RequestedPackageOperation.InstallVersion(_, version)
        | RequestedPackageOperation.UpdateVersion(_, version)
        | RequestedPackageOperation.ConsolidateVersion(_, version) ->
            [ PackageVersionSelection.Exact version ]
        | RequestedPackageOperation.InstallLatest _
        | RequestedPackageOperation.UpdateLatest _ -> [ PackageVersionSelection.Latest ]
        | RequestedPackageOperation.Uninstall package ->
            evaluations
            |> List.collect _.Graphs
            |> List.collect _.Packages
            |> List.choose (fun installed ->
                if installed.Identity <> package then
                    None
                else
                    match installed.State with
                    | InstalledPackageState.Direct(_, version)
                    | InstalledPackageState.CentrallyManagedDirect(_, version, _)
                    | InstalledPackageState.Transitive version
                    | InstalledPackageState.FrameworkProvided version ->
                        Some(PackageVersionSelection.Exact version)
                    | _ -> None)
            |> List.distinct


    let private readMappings
        requestId
        browseSource
        (package: PackageId)
        (evaluated: InstalledPackageEvaluation list)
        =
        async {
            let mutable result = Ok Map.empty

            for item: InstalledPackageEvaluation in evaluated do
                match result with
                | Error _ -> ()
                | Ok policies ->
                    let target =
                        PackageWorkspaceTarget.file item.Snapshot.ProjectPath.Value
                        |> Result.defaultWith (failwithf "%A")

                    let project =
                        PackageProjectId.create item.Snapshot.ProjectPath.Value
                        |> Result.defaultWith (failwithf "%A")

                    let mappingRequest =
                        { Id = requestId
                          Target = target
                          Value =
                            { Package = package
                              CandidateSource = browseSource
                              RestoredTransitives = Some(transitivePackages [ item ]) } }

                    let! policy = NuGetSources.sourceMapping mappingRequest

                    result <-
                        policy
                        |> Result.map (fun value -> Map.add (package, project) value policies)

            return result
        }

    let private allowedSources (policy: PackageSourceMappingPolicy) =
        match policy with
        | PackageSourceMappingPolicy.Allowed sources
        | PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence sources
        | PackageSourceMappingPolicy.KnownConflict(_, sources) -> sources

    let private readDetails
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        requestId
        target
        browseSource
        operation
        (package: PackageId)
        (mapping: PackageSourceMappingPolicy)
        (evaluations: InstalledPackageEvaluation list)
        =
        async {
            let source =
                browseSource
                |> Option.orElseWith (fun () -> allowedSources mapping |> List.tryHead)

            match source with
            | None -> return Ok Map.empty
            | Some source ->
                let selections = versions operation evaluations
                let mutable collected = Ok Map.empty

                for selection in selections do
                    match collected with
                    | Error _ -> ()
                    | Ok details ->
                        let detailRequest =
                            { Id = requestId
                              Target = target
                              Value =
                                { Package = package
                                  Version = selection
                                  Source = source } }

                        let! result = NuGetPackageDetails.details requests detailRequest

                        collected <-
                            match result with
                            | Ok value -> Ok(Map.add (package, value.Summary.Version) value details)
                            | Error _ -> Ok details

                return collected
        }

    let createWith
        (evaluatorFactory: unit -> ProjectEvaluator)
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        =
        let readPrecondition (request: PackageRequest<PackagePreviewPreconditionRequest>) =
            async {
                let evaluator = evaluatorFactory ()

                try
                    let! evidence = readBase evaluator request.Id request.Target
                    return evidence |> Result.map (fun (_, _, _, precondition) -> precondition)
                finally
                    evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }

        let readEvidence (request: PackageRequest<PackageOperationRequest>) =
            async {
                let evaluator = evaluatorFactory ()

                try
                    let! baseEvidence = readBase evaluator request.Id request.Target

                    match baseEvidence with
                    | Error error -> return Error error
                    | Ok(root, sensitivity, evaluated, precondition) ->
                        let package = packageOf request.Value.Operation

                        let! mappings =
                            readMappings request.Id request.Value.BrowseSource package evaluated

                        match mappings with
                        | Error error -> return Error error
                        | Ok policies ->
                            let detailPolicy =
                                policies
                                |> Map.toList
                                |> List.tryHead
                                |> Option.map snd
                                |> Option.defaultValue (PackageSourceMappingPolicy.Allowed [])

                            let! details =
                                readDetails
                                    requests
                                    request.Id
                                    request.Target
                                    request.Value.BrowseSource
                                    request.Value.Operation
                                    package
                                    detailPolicy
                                    evaluated

                            return
                                details
                                |> Result.map (fun packageDetails ->
                                    { WorkspaceRoot = root
                                      Evaluations = evaluated |> List.map _.Snapshot
                                      Installed = evaluated |> List.collect _.Graphs
                                      Details = packageDetails
                                      SourceMappings = policies
                                      CaseSensitivity = sensitivity
                                      WorkspaceRevision = precondition.WorkspaceRevision
                                      FileFingerprints = precondition.FileFingerprints })
                finally
                    evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }

        let readUpdateBatchPrecondition
            (request: PackageRequest<PackageUpdateBatchPreconditionRequest>)
            =
            async {
                let evaluator = evaluatorFactory ()

                try
                    let! evidence = readBase evaluator request.Id request.Target
                    return evidence |> Result.map (fun (_, _, _, precondition) -> precondition)
                finally
                    evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }

        let readUpdateBatchEvidence (request: PackageRequest<PackageUpdateBatchRequest>) =
            async {
                let evaluator = evaluatorFactory ()

                try
                    let! baseEvidence = readBase evaluator request.Id request.Target

                    match baseEvidence with
                    | Error error -> return Error error
                    | Ok(root, sensitivity, evaluated, precondition) ->
                        let operations =
                            request.Value.Updates
                            |> NonEmptyList.toList
                            |> List.map (fun selection ->
                                let package = PackageUpdateSelection.package selection

                                match PackageUpdateSelection.requestedVersion selection with
                                | Some version ->
                                    RequestedPackageOperation.UpdateVersion(package, version)
                                | None -> RequestedPackageOperation.UpdateLatest package)
                            |> List.distinct
                            |> List.sortBy (fun operation ->
                                let package = packageOf operation

                                let version =
                                    match operation with
                                    | RequestedPackageOperation.UpdateVersion(_, value) ->
                                        value.Value
                                    | _ -> ""

                                package.Value.ToUpperInvariant(), version)

                        let mutable collected =
                            Ok(
                                Map.empty<PackageId * PackageProjectId, PackageSourceMappingPolicy>,
                                Map.empty<PackageId * NuGetVersion, PackageDetails>
                            )

                        for operation in operations do
                            match collected with
                            | Error _ -> ()
                            | Ok(allMappings, allDetails) ->
                                let package = packageOf operation

                                let! mappings =
                                    readMappings
                                        request.Id
                                        request.Value.BrowseSource
                                        package
                                        evaluated

                                match mappings with
                                | Error error -> collected <- Error error
                                | Ok policies ->
                                    let detailPolicy =
                                        policies
                                        |> Map.toList
                                        |> List.tryHead
                                        |> Option.map snd
                                        |> Option.defaultValue (
                                            PackageSourceMappingPolicy.Allowed []
                                        )

                                    let! details =
                                        readDetails
                                            requests
                                            request.Id
                                            request.Target
                                            request.Value.BrowseSource
                                            operation
                                            package
                                            detailPolicy
                                            evaluated

                                    collected <-
                                        details
                                        |> Result.map (fun packageDetails ->
                                            let mergedMappings =
                                                policies
                                                |> Map.fold
                                                    (fun state key value ->
                                                        Map.add key value state)
                                                    allMappings

                                            let mergedDetails =
                                                packageDetails
                                                |> Map.fold
                                                    (fun state key value ->
                                                        Map.add key value state)
                                                    allDetails

                                            mergedMappings, mergedDetails)

                        return
                            collected
                            |> Result.map (fun (mappings, details) ->
                                { WorkspaceRoot = root
                                  Evaluations = evaluated |> List.map _.Snapshot
                                  Installed = evaluated |> List.collect _.Graphs
                                  Details = details
                                  SourceMappings = mappings
                                  CaseSensitivity = sensitivity
                                  WorkspaceRevision = precondition.WorkspaceRevision
                                  FileFingerprints = precondition.FileFingerprints })
                finally
                    evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }

        { ReadPrecondition = readPrecondition
          Preview = PackageOperationPreviews.create readEvidence
          ReadUpdateBatchPrecondition = readUpdateBatchPrecondition
          PreviewUpdateBatch = PackageOperationPreviews.createUpdateBatch readUpdateBatchEvidence }
