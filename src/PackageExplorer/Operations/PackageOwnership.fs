namespace Dotnet.WorkspaceExplorer.PackageExplorer

#nowarn "3261"

open System
open System.IO
open Dotnet.WorkspaceExplorer.Packages
open Dotnet.WorkspaceExplorer.ProjectEvaluation

[<RequireQualifiedAccess>]
type internal PackageOwnership =
    | Direct of projectFile: string
    | Central of projectFile: string * ownerFile: string

[<RequireQualifiedAccess>]
type internal PackageOwnershipFailure =
    | MissingEvaluation
    | MissingTargetFramework
    | ImportedMembership
    | NestedCentralOwner
    | ConflictingOwnership
    | ConditionalOwnership
    | TransitivePinning
    | SymbolicLink
    | ExternalOwner
    | FrameworkProvided

[<RequireQualifiedAccess>]
module internal PackageOwnership =
    let private pathComparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let private fullPath value = Path.GetFullPath value

    let private samePath left right =
        String.Equals(fullPath left, fullPath right, pathComparison)

    let private packageEquals (package: PackageId) (candidate: string) =
        String.Equals(package.Value, candidate, StringComparison.OrdinalIgnoreCase)

    let private projectPath (target: PackageTargetScope) =
        match target with
        | PackageTargetScope.Project project
        | PackageTargetScope.Framework(project, _)
        | PackageTargetScope.Runtime(project, _, _) -> project.Value

    let private framework (target: PackageTargetScope) =
        match target with
        | PackageTargetScope.Project _ -> None
        | PackageTargetScope.Framework(_, framework)
        | PackageTargetScope.Runtime(_, framework, _) -> Some framework.Value

    let private snapshotFor
        (target: PackageTargetScope)
        (evaluations: ProjectEvaluationSnapshot list)
        =
        evaluations
        |> List.tryFind (fun snapshot -> samePath snapshot.ProjectPath.Value (projectPath target))

    let private dimensions (target: PackageTargetScope) (snapshot: ProjectEvaluationSnapshot) =
        let available = snapshot.Dimensions |> Seq.toList

        match framework target with
        | None ->
            let inner = available |> List.filter (fun dimension -> not dimension.IsOuterBuild)
            if List.isEmpty inner then available else inner
        | Some selected ->
            available
            |> List.filter (fun dimension ->
                dimension.TargetFramework.HasValue
                && dimension.TargetFramework.Value.Value = selected)

    let private property name (dimension: ProjectEvaluationDimension) =
        dimension.Properties
        |> Seq.tryFind (fun property -> property.Name = name)
        |> Option.map _.Value
        |> Option.defaultValue String.Empty

    let private enabled value =
        String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)

    let private isUnder root candidate =
        let relative = Path.GetRelativePath(fullPath root, fullPath candidate)

        relative <> ".."
        && not (relative.StartsWith($"..{Path.DirectorySeparatorChar}", pathComparison))
        && not (Path.IsPathRooted relative)

    let rec private containsLink root candidate =
        if not (isUnder root candidate) then
            false
        else
            let current =
                if File.Exists candidate || Directory.Exists candidate then
                    Some candidate
                else
                    Path.GetDirectoryName candidate |> Option.ofObj

            match current with
            | None -> false
            | Some path ->
                let linked =
                    try
                        File.GetAttributes(path).HasFlag FileAttributes.ReparsePoint
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> false

                if linked || samePath path root then
                    linked
                else
                    match Directory.GetParent path with
                    | null -> false
                    | parent -> containsLink root parent.FullName

    let private currentState (installed: InstalledPackage option) = installed |> Option.map _.State

    let private currentCentralOwner (installed: InstalledPackage option) =
        match currentState installed with
        | Some(InstalledPackageState.CentrallyManagedDirect(_, _, owner))
        | Some(InstalledPackageState.UnresolvedCentrallyManagedDirect(_, owner)) -> Some owner
        | _ -> None

    let private unsupportedCurrent
        (operation: RequestedPackageOperation)
        (installed: InstalledPackage option)
        =
        match operation, currentState installed with
        | (RequestedPackageOperation.InstallLatest _ | RequestedPackageOperation.InstallVersion _),
          Some(InstalledPackageState.Transitive _) -> None
        | _, Some(InstalledPackageState.Transitive _) ->
            Some PackageOwnershipFailure.TransitivePinning
        | _, Some(InstalledPackageState.FrameworkProvided _)
        | _, Some InstalledPackageState.FrameworkProvidedWithoutVersion ->
            Some PackageOwnershipFailure.FrameworkProvided
        | _ -> None

    let private conditional
        (memberships: EvaluatedPackageMembership list)
        (versions: EvaluatedPackageVersion list)
        =
        memberships
        |> List.exists (fun item -> not (String.IsNullOrWhiteSpace item.Condition))
        || versions
           |> List.exists (fun item -> not (String.IsNullOrWhiteSpace item.Condition))

    let private conflicting
        (memberships: EvaluatedPackageMembership list)
        (versions: EvaluatedPackageVersion list)
        =
        let distinctMemberships =
            memberships
            |> List.map (fun item ->
                fullPath item.DeclaringPath.Value, item.Version, item.Condition)
            |> List.distinct

        let distinctVersions =
            versions
            |> List.map (fun item ->
                fullPath item.DeclaringPath.Value, item.Version, item.Condition)
            |> List.distinct

        distinctMemberships.Length > 1 || distinctVersions.Length > 1

    let private classifyOwner workspaceRoot project central owner =
        if not (isUnder workspaceRoot project) || not (isUnder workspaceRoot owner) then
            Error PackageOwnershipFailure.ExternalOwner
        elif containsLink workspaceRoot project || containsLink workspaceRoot owner then
            Error PackageOwnershipFailure.SymbolicLink
        elif central then
            let expected = Path.Combine(fullPath workspaceRoot, "Directory.Packages.props")

            if not (samePath owner expected) then
                Error PackageOwnershipFailure.NestedCentralOwner
            else
                Ok(PackageOwnership.Central(fullPath project, fullPath owner))
        else
            Ok(PackageOwnership.Direct(fullPath project))

    let resolve
        (workspaceRoot: string)
        (evaluations: ProjectEvaluationSnapshot list)
        (operation: RequestedPackageOperation)
        (package: PackageId)
        (target: PackageTargetScope)
        (installed: InstalledPackage option)
        =
        match snapshotFor target evaluations with
        | None -> Error PackageOwnershipFailure.MissingEvaluation
        | Some snapshot ->
            match dimensions target snapshot with
            | [] -> Error PackageOwnershipFailure.MissingTargetFramework
            | selected ->
                match unsupportedCurrent operation installed with
                | Some reason -> Error reason
                | None ->
                    let memberships =
                        selected
                        |> List.collect (fun dimension ->
                            dimension.PackageMemberships
                            |> Seq.filter (fun item -> packageEquals package item.Id)
                            |> Seq.toList)

                    let versions =
                        selected
                        |> List.collect (fun dimension ->
                            dimension.PackageVersions
                            |> Seq.filter (fun item -> packageEquals package item.Id)
                            |> Seq.toList)

                    let transitivePinning =
                        selected
                        |> List.exists (
                            property "CentralPackageTransitivePinningEnabled" >> enabled
                        )

                    let centralManagement =
                        selected
                        |> List.map (property "ManagePackageVersionsCentrally" >> enabled)
                        |> List.distinct

                    if transitivePinning then
                        Error PackageOwnershipFailure.TransitivePinning
                    elif conflicting memberships versions || centralManagement.Length > 1 then
                        Error PackageOwnershipFailure.ConflictingOwnership
                    elif conditional memberships versions then
                        Error PackageOwnershipFailure.ConditionalOwnership
                    elif
                        memberships
                        |> List.exists (fun membership ->
                            not (
                                samePath membership.DeclaringPath.Value snapshot.ProjectPath.Value
                            ))
                    then
                        Error PackageOwnershipFailure.ImportedMembership
                    else
                        let isCentral =
                            currentCentralOwner installed |> Option.isSome
                            || centralManagement = [ true ]

                        let owner =
                            if isCentral then
                                currentCentralOwner installed
                                |> Option.orElseWith (fun () ->
                                    versions
                                    |> List.tryHead
                                    |> Option.map (fun version -> version.DeclaringPath.Value))
                                |> Option.defaultValue (
                                    Path.Combine(workspaceRoot, "Directory.Packages.props")
                                )
                            else
                                snapshot.ProjectPath.Value

                        classifyOwner workspaceRoot snapshot.ProjectPath.Value isCentral owner

    let ownerFiles (operation: RequestedPackageOperation) (ownership: PackageOwnership) =
        match ownership, operation with
        | PackageOwnership.Direct project, _ -> [ project ]
        | PackageOwnership.Central(_, owner), RequestedPackageOperation.UpdateLatest _
        | PackageOwnership.Central(_, owner), RequestedPackageOperation.UpdateVersion _
        | PackageOwnership.Central(_, owner), RequestedPackageOperation.ConsolidateVersion _ ->
            [ owner ]
        | PackageOwnership.Central(project, owner), _ -> [ project; owner ]

    let proposed (version: NuGetVersion) (ownership: PackageOwnership) =
        match ownership with
        | PackageOwnership.Direct _ -> ProposedPackageState.Direct version
        | PackageOwnership.Central(_, owner) ->
            ProposedPackageState.CentrallyManaged(version, owner)

    let failureMessage =
        function
        | PackageOwnershipFailure.MissingEvaluation ->
            "The selected project has no evaluated package ownership information."
        | PackageOwnershipFailure.MissingTargetFramework ->
            "The selected target framework was not evaluated for this project."
        | PackageOwnershipFailure.ImportedMembership ->
            "Package membership is imported and cannot be changed safely."
        | PackageOwnershipFailure.NestedCentralOwner ->
            "A nested Directory.Packages.props owns the selected package."
        | PackageOwnershipFailure.ConflictingOwnership ->
            "Package ownership contains conflicting declarations."
        | PackageOwnershipFailure.ConditionalOwnership ->
            "Conditional package ownership is not supported."
        | PackageOwnershipFailure.TransitivePinning ->
            "Central transitive pinning is not supported for package changes."
        | PackageOwnershipFailure.SymbolicLink ->
            "Package ownership passes through a symbolic link."
        | PackageOwnershipFailure.ExternalOwner ->
            "Package ownership is outside the workspace root."
        | PackageOwnershipFailure.FrameworkProvided ->
            "Framework-provided packages cannot be changed."
