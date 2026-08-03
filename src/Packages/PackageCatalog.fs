namespace Dotnet.WorkspaceExplorer.Packages

open System

[<RequireQualifiedAccess>]
type PackageSourceAvailability =
    | Available
    | Disabled
    | AuthenticationRequired
    | Unavailable

type PackageSource =
    { Id: PackageSourceId
      Name: string
      Location: Uri
      Availability: PackageSourceAvailability }

[<RequireQualifiedAccess>]
type PrereleaseSelection =
    | StableOnly
    | IncludePrerelease

[<RequireQualifiedAccess>]
type PackageSearchTerm =
    | AllPackages
    | Matching of string

type PackageSearch =
    { Term: PackageSearchTerm
      Prerelease: PrereleaseSelection
      Source: PackageSourceId option }

[<RequireQualifiedAccess>]
type PackageVersionSelection =
    | Latest
    | Exact of NuGetVersion
    | Range of NuGetVersionRange

[<RequireQualifiedAccess>]
type PackageDeprecation =
    | NotDeprecated
    | Deprecated of reasons: NonEmptyList<string> * alternate: PackageId option

[<RequireQualifiedAccess>]
type PackageVulnerabilitySeverity =
    | Low
    | Moderate
    | High
    | Critical

type PackageVulnerability =
    { Severity: PackageVulnerabilitySeverity
      Advisory: Uri }

type PackageSummary =
    { Identity: PackageId
      Version: NuGetVersion
      Description: string option
      Source: PackageSourceId }

type PackageDetails =
    { Summary: PackageSummary
      Authors: string list
      ProjectUrl: Uri option
      License: string option
      DependencyGroups: Map<TargetFramework option, (PackageId * NuGetVersionRange) list>
      Deprecation: PackageDeprecation
      Vulnerabilities: PackageVulnerability list }

type PackagePage<'value> =
    { Items: 'value list
      Continuation: string option }

[<RequireQualifiedAccess>]
type InstalledPackageState =
    | Direct of requested: PackageVersionSelection * resolved: NuGetVersion
    | CentrallyManagedDirect of
        requested: PackageVersionSelection *
        resolved: NuGetVersion *
        ownerFile: string
    | Transitive of resolved: NuGetVersion
    | FrameworkProvided of resolved: NuGetVersion
    | FrameworkProvidedWithoutVersion
    | UnresolvedDirect of requested: PackageVersionSelection
    | UnresolvedCentrallyManagedDirect of requested: PackageVersionSelection * ownerFile: string
    | MissingRestoreGraph
    | StaleRestoreGraph

type InstalledPackage =
    { Identity: PackageId
      Target: PackageTargetScope
      State: InstalledPackageState }

type PackageUpdate =
    { Installed: InstalledPackage
      Available: NonEmptyList<NuGetVersion> }

type PackageConsolidation =
    { Identity: PackageId
      CurrentVersions: NonEmptyList<NuGetVersion * NonEmptyList<PackageTargetScope>>
      CandidateVersions: NonEmptyList<NuGetVersion> }
