namespace Dotnet.WorkspaceExplorer.PackageExplorer

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

[<RequireQualifiedAccess>]
type PackageRestoreOutcome =
    | NotRequired
    | Completed

type PackageExecution =
    { Operation: PackageOperationId
      ChangedFiles: string list
      Restore: PackageRestoreOutcome }

type ConfiguredPackageSources =
    PackageRequest<unit> -> Async<Result<PackageSource list, PackageFailure>>

type SearchPackages =
    PackageRequest<PackageSearchRequest>
        -> Async<Result<PackagePage<PackageSummary>, PackageFailure>>

type ReadPackageDetails =
    PackageRequest<PackageDetailsRequest> -> Async<Result<PackageDetails, PackageFailure>>

type ReadPackageSourceMapping =
    PackageRequest<PackageSourceMappingRequest>
        -> Async<Result<PackageSourceMappingPolicy, PackageFailure>>

type ReadInstalledPackages =
    PackageRequest<unit> -> Async<Result<InstalledPackageGraph list, PackageFailure>>

type ReadPackageUpdates =
    PackageRequest<PrereleaseSelection> -> Async<Result<PackageUpdate list, PackageFailure>>

type ReadPackageConsolidation =
    PackageRequest<unit> -> Async<Result<PackageConsolidation list, PackageFailure>>

type PreviewPackageOperation =
    PackageRequest<PackageOperationRequest> -> Async<Result<PackagePreview, PackageFailure>>

type ExecutePackageOperation =
    PackageRequest<PackageConfirmation>
        -> (PackageProgress -> unit)
        -> Async<Result<PackageExecution, PackageFailure>>

type CancelPackageWork = PackageCancellation -> Async<unit>

type PackageExplorerPorts =
    { ConfiguredSources: ConfiguredPackageSources
      SourceMapping: ReadPackageSourceMapping
      Search: SearchPackages
      Details: ReadPackageDetails
      Installed: ReadInstalledPackages
      Updates: ReadPackageUpdates
      Consolidation: ReadPackageConsolidation
      Preview: PreviewPackageOperation
      ExecuteConfirmed: ExecutePackageOperation
      Cancel: CancelPackageWork }
