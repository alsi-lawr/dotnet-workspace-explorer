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
          Preview: PreviewPackageOperation }

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

    let private pathKey root =
        let sensitivity = FileSystemCaseSensitivityDetector.DetectFromExistingPath root

        fun path ->
            let full = Path.GetFullPath path

            if sensitivity = FileSystemCaseSensitivity.Insensitive then
                full.ToUpperInvariant()
            else
                full

    let private evidencePaths root evaluations =
        seq {
            yield Path.Combine(root, "Directory.Packages.props")

            for evaluation: InstalledPackageEvaluation in evaluations do
                yield evaluation.Snapshot.ProjectPath.Value
                yield! evaluation.Snapshot.WatchInputs |> Seq.map _.Value

                for dimension in evaluation.Snapshot.Dimensions do
                    yield! dimension.PackageMemberships |> Seq.map (_.DeclaringPath.Value)
                    yield! dimension.PackageVersions |> Seq.map (_.DeclaringPath.Value)
        }

    let private fingerprints root evaluations =
        try
            let key = pathKey root

            evidencePaths root evaluations
            |> Seq.map Path.GetFullPath
            |> Seq.distinctBy key
            |> Seq.sortBy key
            |> Seq.map (fun path ->
                ArtifactFiles.fingerprint path |> Result.map (fun value -> path, value))
            |> Seq.toList
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

    let private revision root fingerprints =
        let key = pathKey root

        fingerprints
        |> Map.toList
        |> List.sortBy (fst >> key)
        |> List.map (fun (path, fingerprint) -> $"{key path}\u0000{fingerprint}")
        |> String.concat "\u0001"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    let private readBase
        (evaluator: ProjectEvaluator)
        (request: PackageRequest<PackageOperationRequest>)
        =
        async {
            let unitRequest =
                { Id = request.Id
                  Target = request.Target
                  Value = () }

            let! evaluated =
                NuGetInstalledPackages.readEvaluationWithEvaluator evaluator unitRequest

            match evaluated with
            | Error error -> return Error error
            | Ok values ->
                let root = workspaceRoot request.Target

                match fingerprints root values with
                | Error error -> return Error error
                | Ok fileFingerprints ->
                    return
                        Ok(
                            root,
                            values,
                            { WorkspaceRevision = revision root fileFingerprints
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

    let private allowedSources (policy: PackageSourceMappingPolicy) =
        match policy with
        | PackageSourceMappingPolicy.Allowed sources
        | PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence sources
        | PackageSourceMappingPolicy.KnownConflict(_, sources) -> sources

    let private readDetails
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        (request: PackageRequest<PackageOperationRequest>)
        (package: PackageId)
        (mapping: PackageSourceMappingPolicy)
        (evaluations: InstalledPackageEvaluation list)
        =
        async {
            let source =
                request.Value.BrowseSource
                |> Option.orElseWith (fun () -> allowedSources mapping |> List.tryHead)

            match source with
            | None -> return Ok Map.empty
            | Some source ->
                let selections = versions request.Value.Operation evaluations
                let mutable collected = Ok Map.empty

                for selection in selections do
                    match collected with
                    | Error _ -> ()
                    | Ok details ->
                        let detailRequest =
                            { Id = request.Id
                              Target = request.Target
                              Value =
                                { Package = package
                                  Version = selection
                                  Source = source } }

                        let! result = NuGetPackageDetails.details requests detailRequest

                        collected <-
                            match result with
                            | Ok value -> Ok(Map.add value.Summary.Version value details)
                            | Error _ -> Ok details

                return collected
        }

    let createWith
        (evaluatorFactory: unit -> ProjectEvaluator)
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        =
        let readPrecondition (request: PackageRequest<PackageOperationRequest>) =
            async {
                let evaluator = evaluatorFactory ()

                try
                    let! evidence = readBase evaluator request
                    return evidence |> Result.map (fun (_, _, precondition) -> precondition)
                finally
                    evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }

        let readEvidence (request: PackageRequest<PackageOperationRequest>) =
            async {
                let evaluator = evaluatorFactory ()

                try
                    let! baseEvidence = readBase evaluator request

                    match baseEvidence with
                    | Error error -> return Error error
                    | Ok(root, evaluated, precondition) ->
                        let package = packageOf request.Value.Operation

                        let mappingRequest =
                            { Id = request.Id
                              Target = request.Target
                              Value =
                                { Package = package
                                  CandidateSource = request.Value.BrowseSource
                                  RestoredTransitives = Some(transitivePackages evaluated) } }

                        let! mapping = NuGetSources.sourceMapping mappingRequest

                        match mapping with
                        | Error error -> return Error error
                        | Ok policy ->
                            let! details = readDetails requests request package policy evaluated

                            return
                                details
                                |> Result.map (fun packageDetails ->
                                    { WorkspaceRoot = root
                                      Evaluations = evaluated |> List.map _.Snapshot
                                      Installed = evaluated |> List.collect _.Graphs
                                      Details = packageDetails
                                      SourceMapping = policy
                                      WorkspaceRevision = precondition.WorkspaceRevision
                                      FileFingerprints = precondition.FileFingerprints })
                finally
                    evaluator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }

        { ReadPrecondition = readPrecondition
          Preview = PackageOperationPreviews.create readEvidence }
