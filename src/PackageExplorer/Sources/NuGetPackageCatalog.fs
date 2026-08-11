namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System.Collections.Concurrent
open System.Threading
open Dotnet.WorkspaceExplorer.Packages

type PackageCatalogPorts =
    { ConfiguredSources: ConfiguredPackageSources
      SourceMapping: ReadPackageSourceMapping
      Search: SearchPackages
      Details: ReadPackageDetails
      Installed: ReadInstalledPackages
      RefreshInstalled: RefreshInstalledPackages
      Updates: ReadPackageUpdates
      Consolidation: ReadPackageConsolidation
      PreviewPrecondition: ReadPackagePreviewPrecondition
      Preview: PreviewPackageOperation
      UpdateBatchPrecondition: ReadPackageUpdateBatchPrecondition
      PreviewUpdateBatch: PreviewPackageUpdateBatch
      ExecuteConfirmed: ExecutePackageOperation
      ExecuteConfirmedUpdateBatch: ExecutePackageUpdateBatch
      Cancel: CancelPackageWork }

[<RequireQualifiedAccess>]
module NuGetPackageCatalog =
    let createWith
        (evaluatorFactory: unit -> Dotnet.WorkspaceExplorer.ProjectEvaluation.ProjectEvaluator)
        (runRestore: RunInstalledRestore)
        =
        let requests = ConcurrentDictionary<PackageRequestId, CancellationTokenSource>()
        let operations = ConcurrentDictionary<PackageOperationId, CancellationTokenSource>()

        let previews = NuGetPackageOperationPreviews.createWith evaluatorFactory requests

        let refresh =
            NuGetInstalledPackages.refreshWith evaluatorFactory runRestore requests

        let execution =
            PackageOperationExecution.createWith
                requests
                operations
                { ReadPrecondition = previews.ReadPrecondition
                  ReadUpdateBatchPrecondition = previews.ReadUpdateBatchPrecondition
                  RefreshInstalled = refresh
                  RunCommand = DotnetPackageOperations.run }

        let installedGraphs = NuGetInstalledPackages.readWithFactory evaluatorFactory

        let installed =
            NuGetInstalledPackages.readStreamWithFactory evaluatorFactory requests

        let details = NuGetPackageDetails.details requests

        ({ ConfiguredSources = NuGetSources.configuredSources
           SourceMapping = NuGetSources.sourceMapping
           Search = NuGetPackageSearch.search requests
           Details = details
           Installed = installed
           RefreshInstalled = refresh
           Updates =
             PackageInventories.updates
                 requests
                 installedGraphs
                 NuGetSources.configuredSources
                 NuGetSources.sourceMapping
                 details
           Consolidation = PackageInventories.consolidation requests installedGraphs
           PreviewPrecondition = previews.ReadPrecondition
           Preview = previews.Preview
           UpdateBatchPrecondition = previews.ReadUpdateBatchPrecondition
           PreviewUpdateBatch = previews.PreviewUpdateBatch
           ExecuteConfirmed = execution.Execute
           ExecuteConfirmedUpdateBatch = execution.ExecuteUpdateBatch
           Cancel = execution.Cancel }
        : PackageCatalogPorts)

    let create () =
        createWith
            (fun () -> new Dotnet.WorkspaceExplorer.ProjectEvaluation.ProjectEvaluator())
            DotnetInstalledRestore.run
