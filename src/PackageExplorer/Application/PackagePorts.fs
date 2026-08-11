namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System.Threading
open Dotnet.WorkspaceExplorer.Packages

[<Struct>]
type PackagePageSize =
    private
    | PackagePageSize of int

    member this.Value =
        let (PackagePageSize value) = this
        value

[<RequireQualifiedAccess>]
module PackagePageSize =
    let create value =
        if value < 1 || value > 200 then
            Error(PackageContractViolation.OutOfRange "pageSize")
        else
            Ok(PackagePageSize value)

type PackageRequest<'request> =
    { Id: PackageRequestId
      Target: PackageWorkspaceTarget
      Value: 'request }

type PackageSearchRequest =
    { Search: PackageSearch
      PageSize: PackagePageSize
      Continuation: string option }

type PackageDetailsRequest =
    { Package: PackageId
      Version: PackageVersionSelection
      Source: PackageSourceId }

type PackageSourceMappingRequest =
    { Package: PackageId
      CandidateSource: PackageSourceId option
      RestoredTransitives: PackageId list option }

type PackageBatchSink<'item> = CancellationToken -> NonEmptyList<'item> -> Async<unit>

type PackageProducer<'request, 'item, 'completion> =
    PackageRequest<'request>
        -> PackageBatchSink<'item>
        -> Async<Result<'completion, PackageFailure>>

type PackageSearchCompletion =
    { Query: PackageSearch
      Continuation: string option
      SourceFailures: PackageSourceFailure list }

type ConfiguredPackageSources =
    PackageRequest<unit> -> Async<Result<PackageSource list, PackageFailure>>

type SearchPackages = PackageProducer<PackageSearchRequest, PackageSummary, PackageSearchCompletion>

type ReadPackageDetails =
    PackageRequest<PackageDetailsRequest> -> Async<Result<PackageDetails, PackageFailure>>

type ReadPackageSourceMapping =
    PackageRequest<PackageSourceMappingRequest>
        -> Async<Result<PackageSourceMappingPolicy, PackageFailure>>

type ReadInstalledPackages = PackageProducer<unit, InstalledPackageEntry, unit>

type RefreshInstalledPackages = PackageProducer<unit, InstalledPackageEntry, unit>

type ReadPackageUpdates = PackageProducer<PrereleaseSelection, PackageUpdate, unit>

type ReadPackageConsolidation = PackageProducer<unit, PackageConsolidation, unit>

type ReadPackagePreviewPrecondition =
    PackageRequest<PackagePreviewPreconditionRequest>
        -> Async<Result<PackagePreviewPrecondition, PackageFailure>>

type PreviewPackageOperation =
    PackageRequest<PackageOperationRequest> -> Async<Result<PackagePreview, PackageFailure>>

type ReadPackageUpdateBatchPrecondition =
    PackageRequest<PackageUpdateBatchPreconditionRequest>
        -> Async<Result<PackagePreviewPrecondition, PackageFailure>>

type PreviewPackageUpdateBatch =
    PackageRequest<PackageUpdateBatchRequest>
        -> Async<Result<PackageUpdateBatchPreview, PackageFailure>>

type ExecutePackageOperation =
    PackageRequest<PackageConfirmation>
        -> (PackageProgress -> unit)
        -> Async<Result<PackageExecution, PackageFailure>>

type ExecutePackageUpdateBatch =
    PackageRequest<PackageUpdateBatchConfirmation>
        -> (PackageProgress -> unit)
        -> Async<Result<PackageExecution, PackageFailure>>

type CancelPackageWork = PackageCancellation -> Async<unit>
