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
      Cancel: CancelPackageWork }

[<RequireQualifiedAccess>]
module NuGetPackageCatalog =
    let create () =
        let requests = ConcurrentDictionary<PackageRequestId, CancellationTokenSource>()

        let cancel cancellation =
            async {
                match cancellation with
                | PackageCancellation.Request request ->
                    match requests.TryGetValue request with
                    | true, active -> active.Cancel()
                    | _ -> ()
                | PackageCancellation.Operation _ -> ()
            }

        { ConfiguredSources = NuGetSources.configuredSources
          SourceMapping = NuGetSources.sourceMapping
          Search = NuGetPackageSearch.search requests
          Details = NuGetPackageDetails.details requests
          Installed = NuGetInstalledPackages.read
          Cancel = cancel }
