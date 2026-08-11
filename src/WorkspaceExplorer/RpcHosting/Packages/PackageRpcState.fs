namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Concurrent
open System.Collections.Immutable
open System.Collections.Generic
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
type internal CachedPackagePreview =
    | Single of PackagePreview
    | Batch of PackageUpdateBatchPreview

[<RequireQualifiedAccess>]
type internal PackageDiscoveryKind =
    | Installed
    | Search
    | Updates
    | Consolidation

type internal PackageRpcState(target: PackageWorkspaceTarget, ports: PackageCatalogPorts) =
    let previews =
        ConcurrentDictionary<string, CachedPackagePreview>(StringComparer.Ordinal)

    let activeDiscovery = ConcurrentDictionary<PackageDiscoveryKind, PackageRequestId>()

    let mutable readmeEnabled = false

    member _.Target = target
    member _.Ports = ports

    member _.ReadmeEnabled
        with get () = readmeEnabled
        and set value = readmeEnabled <- value

    member _.TryAdmit(kind, requestId) = activeDiscovery.TryAdd(kind, requestId)

    member _.Release(kind, requestId) =
        (activeDiscovery :> ICollection<KeyValuePair<PackageDiscoveryKind, PackageRequestId>>)
            .Remove(KeyValuePair(kind, requestId))
        |> ignore

    member _.Remember(preview: PackagePreview) =
        let token = PackagePreview.confirmationToken preview
        previews[token] <- CachedPackagePreview.Single preview

    member _.Remember(preview: PackageUpdateBatchPreview) =
        let token = PackageUpdateBatchPreview.confirmationToken preview
        previews[token] <- CachedPackagePreview.Batch preview

    member _.TakeSingle token =
        let mutable cached = Unchecked.defaultof<CachedPackagePreview>

        match previews.TryRemove(token, &cached), cached with
        | true, CachedPackagePreview.Single preview -> Some preview
        | true, CachedPackagePreview.Batch preview ->
            previews.TryAdd(token, CachedPackagePreview.Batch preview) |> ignore
            None
        | _ -> None

    member _.TakeBatch token =
        let mutable cached = Unchecked.defaultof<CachedPackagePreview>

        match previews.TryRemove(token, &cached), cached with
        | true, CachedPackagePreview.Batch preview -> Some preview
        | true, CachedPackagePreview.Single preview ->
            previews.TryAdd(token, CachedPackagePreview.Single preview) |> ignore
            None
        | _ -> None

type internal PackageRpcNegotiation() =
    let mutable maximumFrameBytes = 1024
    let mutable maximumPageSize = 1
    let mutable capabilities = ImmutableHashSet<string>.Empty

    member _.MaximumFrameBytes
        with get () = maximumFrameBytes
        and set value = maximumFrameBytes <- value

    member _.MaximumPageSize
        with get () = maximumPageSize
        and set value = maximumPageSize <- value

    member _.Capabilities
        with get () = capabilities
        and set value = capabilities <- value
