namespace Dotnet.WorkspaceExplorer.PackageExplorer

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Generic
open System.IO
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open NuGet.Common
open NuGet.LibraryModel
open NuGet.ProjectModel

type internal EffectiveRestoreConfiguration =
    { Sources: string list
      ConfigFiles: string list
      SourceMappingEnabled: bool
      RestoreVerified: bool }

[<RequireQualifiedAccess>]
module internal InstalledPackageGraphs =
    let private pathComparer =
        if OperatingSystem.IsWindows() then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    let private packageId value =
        PackageId.create value |> Result.defaultWith (failwithf "%A")

    let private packageKey (value: string) = value.ToUpperInvariant()

    let private projectId value =
        PackageProjectId.create value |> Result.defaultWith (failwithf "%A")

    let private targetFramework value =
        TargetFramework.create value |> Result.defaultWith (failwithf "%A")

    let private runtimeIdentifier value =
        RuntimeIdentifier.create value |> Result.defaultWith (failwithf "%A")

    let private version (value: NuGet.Versioning.NuGetVersion) =
        NuGetVersion.create (value.ToNormalizedString())
        |> Result.defaultWith (failwithf "%A")

    let private requestedVersion (value: string) =
        if value.IndexOfAny([| '['; ']'; '('; ')'; ','; '*' |]) >= 0 then
            NuGetVersionRange.create value
            |> Result.map PackageVersionSelection.Range
            |> Result.defaultWith (failwithf "%A")
        else
            NuGetVersion.create value
            |> Result.map PackageVersionSelection.Exact
            |> Result.defaultWith (failwithf "%A")

    let private property name (dimension: ProjectEvaluationDimension) =
        dimension.Properties
        |> Seq.tryFind (fun candidate -> candidate.Name = name)
        |> Option.map _.Value
        |> Option.defaultValue String.Empty

    let private runtimeIdentifiers (dimension: ProjectEvaluationDimension) =
        [ property "RuntimeIdentifier" dimension
          property "RuntimeIdentifiers" dimension ]
        |> Seq.collect (fun value ->
            value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries
            ))
        |> Seq.distinct
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        |> Seq.toList

    let private dimensions (snapshot: ProjectEvaluationSnapshot) =
        snapshot.Dimensions
        |> Seq.choose (fun dimension ->
            if dimension.TargetFramework.HasValue then
                Some(dimension.TargetFramework.Value.Value, dimension)
            else
                None)
        |> Seq.sortBy fst
        |> Seq.toList

    let private scope projectPath framework runtime =
        let project = projectId projectPath

        match framework, runtime with
        | None, _ -> PackageTargetScope.Project project
        | Some framework, None -> PackageTargetScope.Framework(project, targetFramework framework)
        | Some framework, Some runtime ->
            PackageTargetScope.Runtime(
                project,
                targetFramework framework,
                runtimeIdentifier runtime
            )

    let private expectedScopes (snapshot: ProjectEvaluationSnapshot) =
        match dimensions snapshot with
        | [] -> [ scope snapshot.ProjectPath.Value None None ]
        | evaluated ->
            evaluated
            |> List.collect (fun (framework, dimension) ->
                scope snapshot.ProjectPath.Value (Some framework) None
                :: (runtimeIdentifiers dimension
                    |> List.map (fun runtime ->
                        scope snapshot.ProjectPath.Value (Some framework) (Some runtime))))

    let private unavailable state (snapshot: ProjectEvaluationSnapshot) =
        expectedScopes snapshot
        |> List.map (fun target ->
            { Target = target
              State = state
              Packages = [] })

    let private normalizedPath (path: string) =
        try
            Path.GetFullPath path
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException -> path

    let private samePath (left: string) (right: string) =
        pathComparer.Equals(normalizedPath left, normalizedPath right)

    let private normalizedSource root (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when uri.IsFile -> normalizedPath uri.LocalPath
        | true, uri -> uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped)
        | _ -> normalizedPath (Path.Combine(root, value))

    let private currentConfiguration root (catalog: ConfiguredCatalog) =
        { Sources =
            catalog.Sources
            |> List.map (fun source -> normalizedSource root source.Configuration.Source)
            |> List.distinct
            |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
          ConfigFiles =
            catalog.ConfigFiles
            |> List.map normalizedPath
            |> List.distinct
            |> List.sortWith (fun left right -> pathComparer.Compare(left, right))
          SourceMappingEnabled = catalog.Mapping.IsEnabled
          RestoreVerified = false }

    let private restoredConfiguration root (lockFile: LockFile) =
        let metadata = lockFile.PackageSpec.RestoreMetadata

        { Sources =
            metadata.Sources
            |> Seq.map (fun source -> normalizedSource root source.Source)
            |> Seq.filter (fun source ->
                not (
                    source.Contains(
                        $"{Path.DirectorySeparatorChar}library-packs",
                        StringComparison.Ordinal
                    )
                ))
            |> Seq.distinct
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
            |> Seq.toList
          ConfigFiles =
            metadata.ConfigFilePaths
            |> Seq.map normalizedPath
            |> Seq.distinct
            |> Seq.sortWith (fun left right -> pathComparer.Compare(left, right))
            |> Seq.toList
          SourceMappingEnabled = false
          RestoreVerified = false }

    let private setEquals
        (comparer: IEqualityComparer<string>)
        (left: string seq)
        (right: string seq)
        =
        let leftSet = HashSet<string>(left, comparer)
        leftSet.SetEquals right

    let private frameworkName (framework: NuGet.Frameworks.NuGetFramework) =
        framework.GetShortFolderName()

    let private restoredFrameworks (lockFile: LockFile) =
        lockFile.PackageSpec.TargetFrameworks
        |> Seq.map (fun framework -> frameworkName framework.FrameworkName)
        |> Seq.toList

    let private restoredTargets (lockFile: LockFile) =
        lockFile.Targets
        |> Seq.map (fun target ->
            frameworkName target.TargetFramework,
            (target.RuntimeIdentifier
             |> Option.ofObj
             |> Option.filter (String.IsNullOrWhiteSpace >> not)))
        |> Seq.toList

    let private expectedTargets snapshot =
        dimensions snapshot
        |> List.collect (fun (framework, dimension) ->
            (framework, None)
            :: (runtimeIdentifiers dimension
                |> List.map (fun runtime -> framework, Some runtime)))

    let private restoredIdentityMatches (snapshot: ProjectEvaluationSnapshot) (lockFile: LockFile) =
        let metadata = lockFile.PackageSpec.RestoreMetadata

        samePath snapshot.ProjectPath.Value metadata.ProjectPath
        && samePath snapshot.ProjectPath.Value metadata.ProjectUniqueName

    let private dependencyMap (framework: TargetFrameworkInformation) =
        framework.Dependencies
        |> Seq.map (fun dependency -> packageKey dependency.Name, dependency)
        |> Map.ofSeq

    let private centralVersionMap (dimension: ProjectEvaluationDimension) =
        dimension.PackageVersions
        |> Seq.choose (fun packageVersion ->
            packageVersion.Version
            |> Option.ofObj
            |> Option.map (fun value -> packageKey packageVersion.Id, value))
        |> Map.ofSeq

    let private restoredCentralVersionMap (framework: TargetFrameworkInformation) =
        framework.CentralPackageVersions
        |> Seq.map (fun pair ->
            packageKey pair.Key, pair.Value.VersionRange.MinVersion.ToNormalizedString())
        |> Map.ofSeq

    let private membershipVersion
        (centralVersions: Map<string, string>)
        (membership: EvaluatedPackageMembership)
        =
        membership.Version
        |> Option.ofObj
        |> Option.orElseWith (fun () -> Map.tryFind (packageKey membership.Id) centralVersions)

    let private requestedVersionsMatch
        (dimension: ProjectEvaluationDimension)
        (framework: TargetFrameworkInformation)
        =
        let central = centralVersionMap dimension
        let restoredDependencies = dependencyMap framework

        let currentIds =
            dimension.PackageMemberships |> Seq.map (_.Id >> packageKey) |> Set.ofSeq

        let restoredIds = restoredDependencies |> Map.keys |> Set.ofSeq

        currentIds = restoredIds
        && (dimension.PackageMemberships
            |> Seq.forall (fun membership ->
                match Map.tryFind (packageKey membership.Id) restoredDependencies with
                | None -> false
                | Some restored ->
                    match membershipVersion central membership with
                    | None -> isNull restored.LibraryRange.VersionRange
                    | Some current ->
                        not (isNull restored.LibraryRange.VersionRange)
                        && restored.LibraryRange.VersionRange.ToNormalizedString() = NuGet
                            .Versioning.VersionRange
                            .Parse(current)
                            .ToNormalizedString()))

    let private centralVersionsMatch
        (dimension: ProjectEvaluationDimension)
        (framework: TargetFrameworkInformation)
        =
        centralVersionMap dimension = restoredCentralVersionMap framework

    let private projectReferencesMatch
        projectDirectory
        (dimension: ProjectEvaluationDimension)
        (metadata: ProjectRestoreMetadataFrameworkInfo)
        =
        let current =
            dimension.ProjectReferences
            |> Seq.map (fun reference ->
                reference.ResolvedPath
                |> Option.ofObj
                |> Option.map _.Value
                |> Option.defaultWith (fun () ->
                    if Path.IsPathRooted reference.Include then
                        reference.Include
                    else
                        Path.Combine(projectDirectory, reference.Include))
                |> normalizedPath)

        let restored =
            metadata.ProjectReferences |> Seq.map (_.ProjectPath >> normalizedPath)

        setEquals pathComparer current restored

    let private evaluatedInputsMatch (snapshot: ProjectEvaluationSnapshot) (lockFile: LockFile) =
        let restoredSpecs =
            lockFile.PackageSpec.TargetFrameworks
            |> Seq.map (fun framework -> frameworkName framework.FrameworkName, framework)
            |> Map.ofSeq

        let restoredMetadata =
            lockFile.PackageSpec.RestoreMetadata.TargetFrameworks
            |> Seq.map (fun framework -> frameworkName framework.FrameworkName, framework)
            |> Map.ofSeq

        let projectDirectory =
            Path.GetDirectoryName snapshot.ProjectPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        setEquals
            StringComparer.Ordinal
            (dimensions snapshot |> List.map fst)
            (restoredFrameworks lockFile)
        && Set.ofList (expectedTargets snapshot) = Set.ofList (restoredTargets lockFile)
        && (dimensions snapshot
            |> List.forall (fun (framework, dimension) ->
                match
                    Map.tryFind framework restoredSpecs, Map.tryFind framework restoredMetadata
                with
                | Some spec, Some metadata ->
                    requestedVersionsMatch dimension spec
                    && centralVersionsMatch dimension spec
                    && projectReferencesMatch projectDirectory dimension metadata
                | _ -> false))

    let private graphState
        (snapshot: ProjectEvaluationSnapshot)
        (configuration: EffectiveRestoreConfiguration)
        (lockFile: LockFile)
        =
        let root = Path.GetDirectoryName snapshot.ProjectPath.Value

        if not (restoredIdentityMatches snapshot lockFile) then
            InstalledPackageGraphState.MismatchedRestoreGraph
        elif not (evaluatedInputsMatch snapshot lockFile) then
            InstalledPackageGraphState.StaleRestoreGraph
        else
            let restored = restoredConfiguration root lockFile

            if
                not (setEquals StringComparer.Ordinal configuration.Sources restored.Sources)
                || not (setEquals pathComparer configuration.ConfigFiles restored.ConfigFiles)
            then
                InstalledPackageGraphState.StaleRestoreGraph
            elif
                List.isEmpty configuration.ConfigFiles
                || (configuration.SourceMappingEnabled && not configuration.RestoreVerified)
            then
                InstalledPackageGraphState.UnverifiablyFreshRestoreGraph
            else
                InstalledPackageGraphState.Current

    let private declaration (membership: EvaluatedPackageMembership) =
        Some
            { OwnerFile = membership.DeclaringPath.Value
              Condition = membership.Condition }

    let private centralOwner (dimension: ProjectEvaluationDimension) (package: string) =
        dimension.PackageVersions
        |> Seq.tryFind (fun version ->
            String.Equals(version.Id, package, StringComparison.OrdinalIgnoreCase))

    let private targetLibraries (target: LockFileTarget) =
        target.Libraries
        |> Seq.filter (fun library ->
            String.Equals(library.Type, "package", StringComparison.OrdinalIgnoreCase))
        |> Seq.map (fun library -> packageKey library.Name, library)
        |> Map.ofSeq

    let private installedState
        (dimension: ProjectEvaluationDimension)
        (dependencies: Map<string, LibraryDependency>)
        (libraries: Map<string, LockFileTargetLibrary>)
        (membership: EvaluatedPackageMembership)
        =
        let central = centralOwner dimension membership.Id

        let requested =
            membership.Version
            |> Option.ofObj
            |> Option.orElseWith (fun () ->
                central |> Option.bind (fun owner -> owner.Version |> Option.ofObj))
            |> Option.map requestedVersion
            |> Option.defaultValue PackageVersionSelection.Latest

        match
            Map.tryFind (packageKey membership.Id) dependencies,
            Map.tryFind (packageKey membership.Id) libraries
        with
        | Some dependency, Some resolved when dependency.AutoReferenced ->
            InstalledPackageState.FrameworkProvided(version resolved.Version)
        | _, Some resolved ->
            match central with
            | Some owner ->
                InstalledPackageState.CentrallyManagedDirect(
                    requested,
                    version resolved.Version,
                    owner.DeclaringPath.Value
                )
            | None -> InstalledPackageState.Direct(requested, version resolved.Version)
        | _, None ->
            match central with
            | Some owner ->
                InstalledPackageState.UnresolvedCentrallyManagedDirect(
                    requested,
                    owner.DeclaringPath.Value
                )
            | None -> InstalledPackageState.UnresolvedDirect requested

    let private frameworkPackages
        (projectPath: string)
        (framework: string)
        (dimension: ProjectEvaluationDimension)
        (frameworkSpec: TargetFrameworkInformation)
        (target: LockFileTarget)
        =
        let targetScope = scope projectPath (Some framework) None
        let dependencies = dependencyMap frameworkSpec
        let libraries = targetLibraries target

        let direct =
            dimension.PackageMemberships
            |> Seq.map (fun membership ->
                { Identity = packageId membership.Id
                  Target = targetScope
                  State = installedState dimension dependencies libraries membership
                  Declaration = declaration membership })
            |> Seq.toList

        let directIds = direct |> Seq.map (_.Identity.Value >> packageKey) |> Set.ofSeq

        let transitive =
            libraries
            |> Map.toSeq
            |> Seq.choose (fun (identity, library) ->
                if Set.contains identity directIds then
                    None
                else
                    Some
                        { Identity = packageId library.Name
                          Target = targetScope
                          State = InstalledPackageState.Transitive(version library.Version)
                          Declaration = None })
            |> Seq.toList

        let frameworkProvided =
            frameworkSpec.FrameworkReferences
            |> Seq.map (fun reference ->
                { Identity = packageId reference.Name
                  Target = targetScope
                  State = InstalledPackageState.FrameworkProvidedWithoutVersion
                  Declaration = None })
            |> Seq.toList

        direct @ transitive @ frameworkProvided
        |> List.sortBy (fun package -> package.Identity.Value, package.State.ToString())

    let private runtimePackages
        (projectPath: string)
        (framework: string)
        (dimension: ProjectEvaluationDimension)
        (frameworkSpec: TargetFrameworkInformation)
        (baseTarget: LockFileTarget)
        (runtimeTarget: LockFileTarget)
        =
        let baseLibraries = targetLibraries baseTarget
        let runtimeLibraries = targetLibraries runtimeTarget
        let dependencies = dependencyMap frameworkSpec

        let memberships =
            dimension.PackageMemberships
            |> Seq.map (fun membership -> packageKey membership.Id, membership)
            |> Map.ofSeq

        let runtimeScope =
            scope projectPath (Some framework) (Some runtimeTarget.RuntimeIdentifier)

        runtimeLibraries
        |> Map.toSeq
        |> Seq.choose (fun (identity, library) ->
            match Map.tryFind identity baseLibraries with
            | Some baseLibrary when baseLibrary.Version = library.Version -> None
            | _ ->
                let state, declarationOwner =
                    match Map.tryFind identity memberships with
                    | Some membership ->
                        installedState dimension dependencies runtimeLibraries membership,
                        declaration membership
                    | None -> InstalledPackageState.Transitive(version library.Version), None

                Some
                    { Identity = packageId library.Name
                      Target = runtimeScope
                      State = state
                      Declaration = declarationOwner })
        |> Seq.sortBy (_.Identity.Value)
        |> Seq.toList

    let private currentGraphs (snapshot: ProjectEvaluationSnapshot) (lockFile: LockFile) =
        let specs: Map<string, TargetFrameworkInformation> =
            lockFile.PackageSpec.TargetFrameworks
            |> Seq.map (fun framework -> frameworkName framework.FrameworkName, framework)
            |> Map.ofSeq

        let targets: Map<string * string option, LockFileTarget> =
            lockFile.Targets
            |> Seq.map (fun target ->
                (frameworkName target.TargetFramework,
                 target.RuntimeIdentifier
                 |> Option.ofObj
                 |> Option.filter (String.IsNullOrWhiteSpace >> not)),
                target)
            |> Map.ofSeq

        dimensions snapshot
        |> List.collect (fun (framework, dimension) ->
            let baseTarget = targets[framework, None]

            let baseGraph =
                { Target = scope snapshot.ProjectPath.Value (Some framework) None
                  State = InstalledPackageGraphState.Current
                  Packages =
                    frameworkPackages
                        snapshot.ProjectPath.Value
                        framework
                        dimension
                        specs[framework]
                        baseTarget }

            baseGraph
            :: (runtimeIdentifiers dimension
                |> List.map (fun runtime ->
                    let runtimeTarget = targets[framework, Some runtime]

                    { Target = scope snapshot.ProjectPath.Value (Some framework) (Some runtime)
                      State = InstalledPackageGraphState.Current
                      Packages =
                        runtimePackages
                            snapshot.ProjectPath.Value
                            framework
                            dimension
                            specs[framework]
                            baseTarget
                            runtimeTarget })))

    let readSnapshot
        (configuration: EffectiveRestoreConfiguration)
        (snapshot: ProjectEvaluationSnapshot)
        (assetsPath: string)
        =
        if not (File.Exists assetsPath) then
            unavailable InstalledPackageGraphState.MissingRestoreGraph snapshot
        else
            try
                let lockFile = LockFileFormat().Read(assetsPath, NullLogger.Instance)

                if
                    lockFile.Version = Int32.MinValue
                    || isNull lockFile.PackageSpec
                    || isNull lockFile.PackageSpec.RestoreMetadata
                then
                    unavailable InstalledPackageGraphState.MismatchedRestoreGraph snapshot
                else
                    match graphState snapshot configuration lockFile with
                    | InstalledPackageGraphState.Current -> currentGraphs snapshot lockFile
                    | InstalledPackageGraphState.UnverifiablyFreshRestoreGraph ->
                        currentGraphs snapshot lockFile
                        |> List.map (fun graph ->
                            { graph with
                                State = InstalledPackageGraphState.UnverifiablyFreshRestoreGraph })
                    | state -> unavailable state snapshot
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? ArgumentException
            | :? InvalidDataException ->
                unavailable InstalledPackageGraphState.MismatchedRestoreGraph snapshot

    let private evaluatedRestoreSources (snapshot: ProjectEvaluationSnapshot) =
        snapshot.Dimensions
        |> Seq.collect _.Properties
        |> Seq.filter (fun property ->
            property.Name = "RestoreSources"
            || property.Name = "RestoreAdditionalProjectSources")
        |> Seq.collect (fun property ->
            property.Value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries
            ))
        |> Seq.toList

    let configurationFor
        (snapshot: ProjectEvaluationSnapshot)
        (target: PackageWorkspaceTarget)
        (catalog: ConfiguredCatalog)
        =
        let targetPath = PackageWorkspaceTarget.path target

        let root =
            match PackageWorkspaceTarget.kind target with
            | PackageWorkspaceTargetKind.Directory -> targetPath
            | _ ->
                Path.GetDirectoryName targetPath
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

        let configuration = currentConfiguration root catalog

        { configuration with
            Sources =
                configuration.Sources
                @ (evaluatedRestoreSources snapshot |> List.map (normalizedSource root))
                |> List.distinct
                |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)) }
