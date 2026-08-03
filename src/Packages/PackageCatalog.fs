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
type PackageSourceFailureKind =
    | AuthenticationRequired
    | Unauthorized
    | Malformed
    | Unavailable

type PackageSourceFailure =
    private
        { Source: PackageSourceId
          Kind: PackageSourceFailureKind
          Code: string
          Message: string }

[<RequireQualifiedAccess>]
module PackageSourceFailure =
    let private stableCode kind =
        match kind with
        | PackageSourceFailureKind.AuthenticationRequired ->
            "DWE-PACKAGE-SOURCE-AUTHENTICATION-REQUIRED"
        | PackageSourceFailureKind.Unauthorized -> "DWE-PACKAGE-SOURCE-UNAUTHORIZED"
        | PackageSourceFailureKind.Malformed -> "DWE-PACKAGE-SOURCE-MALFORMED"
        | PackageSourceFailureKind.Unavailable -> "DWE-PACKAGE-SOURCE-UNAVAILABLE"

    let private stableMessage kind =
        match kind with
        | PackageSourceFailureKind.AuthenticationRequired ->
            "The configured package source requires authentication."
        | PackageSourceFailureKind.Unauthorized ->
            "The configured package source rejected the request."
        | PackageSourceFailureKind.Malformed ->
            "The configured package source returned an invalid response."
        | PackageSourceFailureKind.Unavailable -> "The configured package source is unavailable."

    let create source kind =
        { Source = source
          Kind = kind
          Code = stableCode kind
          Message = stableMessage kind }

    let source failure = failure.Source
    let kind failure = failure.Kind
    let code failure = failure.Code
    let message failure = failure.Message

[<RequireQualifiedAccess>]
type PackageSourceMappingPolicy =
    | Allowed of sources: PackageSourceId list
    | KnownConflict of package: PackageId * configuredSources: PackageSourceId list
    | InsufficientRestoredTransitiveEvidence of allowedSources: PackageSourceId list

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
      Summary: string option
      Tags: string list
      Authors: string list
      Owners: string list
      Source: PackageSourceId }

type AlternatePackage =
    { Identity: PackageId
      Range: NuGetVersionRange option }

[<RequireQualifiedAccess>]
type PackageDeprecation =
    | NotDeprecated
    | Deprecated of reasons: NonEmptyList<string> * alternate: AlternatePackage option

type PackageDetails =
    { Summary: PackageSummary
      Versions: NuGetVersion list
      Authors: string list
      ProjectUrl: Uri option
      License: string option
      LicenseUrl: Uri option
      ReadmeUrl: Uri option
      DependencyGroups: Map<TargetFramework option, (PackageId * NuGetVersionRange) list>
      Deprecation: PackageDeprecation
      Vulnerabilities: PackageVulnerability list }

type PackagePage<'value> =
    { Items: 'value list
      Continuation: string option
      SourceFailures: PackageSourceFailure list }

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

type PackageDeclaration =
    { OwnerFile: string; Condition: string }

type InstalledPackage =
    { Identity: PackageId
      Target: PackageTargetScope
      State: InstalledPackageState
      Declaration: PackageDeclaration option }

[<RequireQualifiedAccess>]
type InstalledPackageGraphState =
    | Current
    | MissingRestoreGraph
    | MismatchedRestoreGraph
    | UnverifiablyFreshRestoreGraph
    | StaleRestoreGraph

type InstalledPackageGraph =
    { Target: PackageTargetScope
      State: InstalledPackageGraphState
      Packages: InstalledPackage list }

type PackageUpdate =
    { Installed: InstalledPackage
      Available: NonEmptyList<NuGetVersion> }

type PackageConsolidation =
    { Identity: PackageId
      CurrentVersions: NonEmptyList<NuGetVersion * NonEmptyList<PackageTargetScope>>
      CandidateVersions: NonEmptyList<NuGetVersion> }
