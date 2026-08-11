namespace Dotnet.WorkspaceExplorer.Rpc

open System
open Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
module PackageRpcResponses =
    let private map values = RpcValue.map values
    let private text value = RpcValue.String value
    let private integer value = RpcValue.Integer(int64 value)
    let private boolean value = RpcValue.Boolean value

    let private array mapping values =
        values |> Seq.map mapping |> RpcValue.array

    let private optional name mapping value =
        value |> Option.map (fun selected -> name, mapping selected) |> Option.toList

    let private availability =
        function
        | PackageSourceAvailability.Available -> "available"
        | PackageSourceAvailability.Disabled -> "disabled"
        | PackageSourceAvailability.AuthenticationRequired -> "authenticationRequired"
        | PackageSourceAvailability.Unavailable -> "unavailable"

    let private retry =
        function
        | PackageFailureRetry.Never -> "never"
        | PackageFailureRetry.AfterUserAction -> "afterUserAction"
        | PackageFailureRetry.Transient -> "transient"

    let private safeFailureMessage =
        function
        | PackageFailureKind.InvalidRequest -> "The package request is invalid."
        | PackageFailureKind.NotFound -> "The requested package resource was not found."
        | PackageFailureKind.AmbiguousTarget -> "The package target is ambiguous."
        | PackageFailureKind.Unsupported -> "The package operation is not supported."
        | PackageFailureKind.AuthenticationRequired ->
            "The configured package source requires authentication."
        | PackageFailureKind.Unauthorized -> "The configured package source rejected the request."
        | PackageFailureKind.MalformedSource ->
            "The configured package source returned an invalid response."
        | PackageFailureKind.SourceUnavailable -> "The configured package source is unavailable."
        | PackageFailureKind.StaleState -> "The package preview is stale."
        | PackageFailureKind.Cancelled -> "The package work was cancelled."
        | PackageFailureKind.ExternalToolFailed -> "The stock dotnet package command failed."
        | PackageFailureKind.PartialRecoveryRequired -> "Package recovery requires user attention."
        | PackageFailureKind.Internal -> "The package request could not be completed safely."

    let private projectLanguage =
        function
        | PackageProjectLanguage.CSharp -> "csharp"
        | PackageProjectLanguage.FSharp -> "fsharp"
        | PackageProjectLanguage.VisualBasic -> "visualBasic"

    let private workspaceKind =
        function
        | PackageWorkspaceTargetKind.Solution -> "solution"
        | PackageWorkspaceTargetKind.SolutionXml -> "solutionXml"
        | PackageWorkspaceTargetKind.SolutionFilter -> "solutionFilter"
        | PackageWorkspaceTargetKind.Project language -> $"project:{projectLanguage language}"
        | PackageWorkspaceTargetKind.Directory -> "directory"

    let target =
        function
        | PackageTargetScope.Project project -> map [ "project", text project.Value ]
        | PackageTargetScope.Framework(project, framework) ->
            map [ "project", text project.Value; "framework", text framework.Value ]
        | PackageTargetScope.Runtime(project, framework, runtime) ->
            map
                [ "project", text project.Value
                  "framework", text framework.Value
                  "runtime", text runtime.Value ]

    let private versionSelection =
        function
        | PackageVersionSelection.Latest -> map [ "kind", text "latest" ]
        | PackageVersionSelection.Exact version ->
            map [ "kind", text "exact"; "value", text version.Value ]
        | PackageVersionSelection.Range range ->
            map [ "kind", text "range"; "value", text range.Value ]

    let private installedState =
        function
        | InstalledPackageState.Direct(requested, resolved) ->
            map
                [ "kind", text "direct"
                  "requested", versionSelection requested
                  "resolved", text resolved.Value ]
        | InstalledPackageState.CentrallyManagedDirect(requested, resolved, owner) ->
            map
                [ "kind", text "centrallyManagedDirect"
                  "requested", versionSelection requested
                  "resolved", text resolved.Value
                  "ownerFile", text owner ]
        | InstalledPackageState.Transitive resolved ->
            map [ "kind", text "transitive"; "resolved", text resolved.Value ]
        | InstalledPackageState.FrameworkProvided resolved ->
            map [ "kind", text "frameworkProvided"; "resolved", text resolved.Value ]
        | InstalledPackageState.FrameworkProvidedWithoutVersion ->
            map [ "kind", text "frameworkProvided" ]
        | InstalledPackageState.UnresolvedDirect requested ->
            map [ "kind", text "unresolvedDirect"; "requested", versionSelection requested ]
        | InstalledPackageState.UnresolvedCentrallyManagedDirect(requested, owner) ->
            map
                [ "kind", text "unresolvedCentrallyManagedDirect"
                  "requested", versionSelection requested
                  "ownerFile", text owner ]

    let private installed (item: InstalledPackage) =
        map (
            [ "package", text item.Identity.Value
              "target", target item.Target
              "state", installedState item.State ]
            @ optional
                "declaration"
                (fun (declaration: PackageDeclaration) ->
                    map
                        [ "ownerFile", text declaration.OwnerFile
                          "condition", text declaration.Condition ])
                item.Declaration
        )

    let private graphState (state: InstalledPackageGraphState) =
        match state with
        | InstalledPackageGraphState.Current -> "current"
        | InstalledPackageGraphState.MissingRestoreGraph -> "missing"
        | InstalledPackageGraphState.MismatchedRestoreGraph -> "mismatched"
        | InstalledPackageGraphState.UnverifiablyFreshRestoreGraph -> "unverifiable"
        | InstalledPackageGraphState.StaleRestoreGraph -> "stale"

    let private page pageSize offset values =
        let items = values |> List.skip (min offset values.Length) |> List.truncate pageSize
        let nextOffset = offset + items.Length

        let continuation =
            if nextOffset < values.Length then
                Some(string nextOffset)
            else
                None

        items, continuation

    let installedResult
        (requestId: PackageRequestId)
        restoreState
        pageSize
        offset
        (entries: InstalledPackageEntry list)
        =
        let entries, continuation = page pageSize offset entries

        map (
            [ "requestId", text (requestId.Value.ToString "D")
              "restore", text restoreState
              "items",
              entries
              |> array (fun entry ->
                  map (
                      [ "target", target entry.Target
                        "graphState", text (graphState entry.GraphState) ]
                      @ optional "package" installed entry.Package
                  )) ]
            @ optional "continuation" text continuation
        )

    let private sourceFailure (failure: PackageSourceFailure) =
        map
            [ "source", text (PackageSourceFailure.source failure).Value
              "code", text (PackageSourceFailure.code failure)
              "message", text (PackageSourceFailure.message failure) ]

    let private summary (value: PackageSummary) =
        map (
            [ "package", text value.Identity.Value
              "version", text value.Version.Value
              "tags", value.Tags |> array text
              "authors", value.Authors |> array text
              "owners", value.Owners |> array text
              "source", text value.Source.Value ]
            @ optional "description" text value.Description
            @ optional "summary" text value.Summary
        )

    let searchResult (requestId: PackageRequestId) (page: PackagePage<PackageSummary>) =
        map (
            [ "requestId", text (requestId.Value.ToString "D")
              "items", page.Items |> array summary
              "sourceFailures", page.SourceFailures |> array sourceFailure ]
            @ optional "continuation" text page.Continuation
        )

    let sourcesResult (sources: PackageSource list) =
        map
            [ "sources",
              sources
              |> array (fun (source: PackageSource) ->
                  map
                      [ "id", text source.Id.Value
                        "name", text source.Name
                        "location", text (source.Location.ToString())
                        "availability", text (availability source.Availability) ]) ]

    let sourceMappingResult policy =
        match policy with
        | PackageSourceMappingPolicy.Allowed sources ->
            map [ "kind", text "allowed"; "sources", sources |> array (_.Value >> text) ]
        | PackageSourceMappingPolicy.KnownConflict(package, sources) ->
            map
                [ "kind", text "knownConflict"
                  "package", text package.Value
                  "sources", sources |> array (_.Value >> text) ]
        | PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence sources ->
            map
                [ "kind", text "insufficientRestoredTransitiveEvidence"
                  "sources", sources |> array (_.Value >> text) ]

    let private severity =
        function
        | PackageVulnerabilitySeverity.Low -> "low"
        | PackageVulnerabilitySeverity.Moderate -> "moderate"
        | PackageVulnerabilitySeverity.High -> "high"
        | PackageVulnerabilitySeverity.Critical -> "critical"

    let private deprecation =
        function
        | PackageDeprecation.NotDeprecated -> map [ "kind", text "notDeprecated" ]
        | PackageDeprecation.Deprecated(reasons, alternate) ->
            map (
                [ "kind", text "deprecated"
                  "reasons", reasons |> NonEmptyList.toList |> array text ]
                @ optional
                    "alternate"
                    (fun (value: AlternatePackage) ->
                        map (
                            [ "package", text value.Identity.Value ]
                            @ optional
                                "versionRange"
                                (fun (range: NuGetVersionRange) -> text range.Value)
                                value.Range
                        ))
                    alternate
            )

    let private vulnerability (value: PackageVulnerability) =
        map
            [ "severity", text (severity value.Severity)
              "advisory", text (value.Advisory.ToString()) ]

    let private dependency (package: PackageId, range: NuGetVersionRange) =
        map [ "package", text package.Value; "versionRange", text range.Value ]

    let detailsResult includeReadme (details: PackageDetails) =
        let readme = details.ReadmeContent |> Option.orElse details.Summary.Description

        map (
            [ "summary", summary details.Summary
              "versions", details.Versions |> array (_.Value >> text)
              "authors", details.Authors |> array text
              "dependencyGroups",
              details.DependencyGroups
              |> Map.toSeq
              |> array (fun (framework, dependencies) ->
                  map (
                      [ "dependencies", dependencies |> array dependency ]
                      @ optional
                          "framework"
                          (fun (value: TargetFramework) -> text value.Value)
                          framework
                  ))
              "deprecation", deprecation details.Deprecation
              "vulnerabilities", details.Vulnerabilities |> array vulnerability ]
            @ optional "projectUrl" (fun (value: Uri) -> text (value.ToString())) details.ProjectUrl
            @ optional "license" text details.License
            @ optional "licenseUrl" (fun (value: Uri) -> text (value.ToString())) details.LicenseUrl
            @ optional "readmeUrl" (fun (value: Uri) -> text (value.ToString())) details.ReadmeUrl
            @ if includeReadme then
                  optional "readmeCommonMark" text readme
              else
                  []
        )

    let private resolvedVersion (installed: InstalledPackage) =
        match installed.State with
        | InstalledPackageState.Direct(_, value)
        | InstalledPackageState.CentrallyManagedDirect(_, value, _)
        | InstalledPackageState.Transitive value
        | InstalledPackageState.FrameworkProvided value -> Some value.Value
        | _ -> None

    let updatesResult pageSize offset (updates: PackageUpdate list) =
        let updates, continuation = page pageSize offset updates

        map (
            [ "updates",
              updates
              |> array (fun (update: PackageUpdate) ->
                  map (
                      [ "package", text update.Installed.Identity.Value
                        "target", target update.Installed.Target
                        "available",
                        update.Available |> NonEmptyList.toList |> array (_.Value >> text) ]
                      @ optional "installedVersion" text (resolvedVersion update.Installed)
                  )) ]
            @ optional "continuation" text continuation
        )

    let consolidationResult pageSize offset (values: PackageConsolidation list) =
        let values, continuation = page pageSize offset values

        map (
            [ "packages",
              values
              |> array (fun (value: PackageConsolidation) ->
                  let candidates =
                      value.CandidateVersions |> NonEmptyList.toList |> array (_.Value >> text)

                  map
                      [ "package", text value.Identity.Value
                        "currentVersions",
                        value.CurrentVersions
                        |> NonEmptyList.toList
                        |> array (fun (version, targets) ->
                            map
                                [ "version", text version.Value
                                  "targets", targets |> NonEmptyList.toList |> array target ])
                        "candidateVersions", candidates ]) ]
            @ optional "continuation" text continuation
        )

    let private operation =
        function
        | RequestedPackageOperation.InstallLatest package ->
            map [ "kind", text "installLatest"; "package", text package.Value ]
        | RequestedPackageOperation.InstallVersion(package, version) ->
            map
                [ "kind", text "installVersion"
                  "package", text package.Value
                  "version", text version.Value ]
        | RequestedPackageOperation.UpdateLatest package ->
            map [ "kind", text "updateLatest"; "package", text package.Value ]
        | RequestedPackageOperation.UpdateVersion(package, version) ->
            map
                [ "kind", text "updateVersion"
                  "package", text package.Value
                  "version", text version.Value ]
        | RequestedPackageOperation.Uninstall package ->
            map [ "kind", text "uninstall"; "package", text package.Value ]
        | RequestedPackageOperation.ConsolidateVersion(package, version) ->
            map
                [ "kind", text "consolidate"
                  "package", text package.Value
                  "version", text version.Value ]

    let private executionState =
        function
        | PackageExecutionState.Completed -> "completed"
        | PackageExecutionState.Compensated -> "compensated"
        | PackageExecutionState.Unchanged -> "unchanged"
        | PackageExecutionState.Uncertain -> "uncertain"

    let executionEntry (entry: PackageExecutionEntry) =
        map
            [ "package", text entry.Package.Value
              "target", target entry.Target
              "state", text (executionState entry.State) ]

    let private proposedState =
        function
        | ProposedPackageState.Direct version ->
            map [ "kind", text "direct"; "version", text version.Value ]
        | ProposedPackageState.CentrallyManaged(version, ownerFile) ->
            map
                [ "kind", text "centrallyManaged"
                  "version", text version.Value
                  "ownerFile", text ownerFile ]

    let private consolidationPosition =
        function
        | PackageConsolidationPosition.AlreadyOnDestination -> "alreadyOnDestination"
        | PackageConsolidationPosition.BelowDestination -> "belowDestination"
        | PackageConsolidationPosition.AboveDestination -> "aboveDestination"
        | PackageConsolidationPosition.Unusable -> "unusable"

    let private targetChange =
        function
        | PackageTargetChange.Install(current, proposed) ->
            map (
                [ "kind", text "install"; "proposed", proposedState proposed ]
                @ optional "current" installedState current
            )
        | PackageTargetChange.Update(current, proposed) ->
            map
                [ "kind", text "update"
                  "current", installedState current
                  "proposed", proposedState proposed ]
        | PackageTargetChange.Uninstall current ->
            map [ "kind", text "uninstall"; "current", installedState current ]
        | PackageTargetChange.Consolidate(current, position, proposed) ->
            map (
                [ "kind", text "consolidate"
                  "position", text (consolidationPosition position) ]
                @ optional "current" installedState current
                @ optional "proposed" proposedState proposed
            )

    let private graphFreshness =
        function
        | PackageGraphFreshness.Current -> "current"
        | PackageGraphFreshness.AwaitingBackgroundRestore -> "awaitingRestore"

    let private metadataImpact =
        function
        | PackageMetadataImpact.Unknown -> map [ "kind", text "unknown" ]
        | PackageMetadataImpact.Known(dependencies, deprecationState, vulnerabilities, license) ->
            map (
                [ "kind", text "known"
                  "dependencies", dependencies |> array dependency
                  "deprecation", deprecation deprecationState
                  "vulnerabilities", vulnerabilities |> array vulnerability ]
                @ optional "license" text license
            )

    let private sourceMappingImpact =
        function
        | PackageSourceMappingImpact.ApplyAllowed sources ->
            map
                [ "kind", text "applyAllowed"
                  "allowedSources", sources |> array (_.Value >> text) ]
        | PackageSourceMappingImpact.BrowseSourceDoesNotConstrainApply(browse, sources) ->
            map
                [ "kind", text "browseSourceDoesNotConstrainApply"
                  "browseSource", text browse.Value
                  "allowedSources", sources |> array (_.Value >> text) ]
        | PackageSourceMappingImpact.UnknownTransitiveConsequences(sources, browse) ->
            map (
                [ "kind", text "unknownTransitiveConsequences"
                  "allowedSources", sources |> array (_.Value >> text) ]
                @ optional
                    "browseSource"
                    (fun (source: PackageSourceId) -> text source.Value)
                    browse
            )

    let private restoreImpact =
        function
        | PackageRestoreImpact.RequiredWithUnknownOutcome freshness ->
            map
                [ "kind", text "requiredWithUnknownOutcome"
                  "graphFreshness", text (graphFreshness freshness) ]

    let private targetImpact (impact: PackageTargetImpact) =
        map
            [ "metadata", metadataImpact impact.Metadata
              "sourceMapping", sourceMappingImpact impact.SourceMapping
              "restore", restoreImpact impact.Restore ]

    let private previewTarget (value: PackageTargetPreview) =
        map
            [ "target", value |> PackageTargetPreview.target |> target
              "change", value |> PackageTargetPreview.change |> targetChange
              "ownerFiles",
              value |> PackageTargetPreview.ownerFiles |> NonEmptyList.toList |> array text
              "graphFreshness",
              value |> PackageTargetPreview.graphFreshness |> graphFreshness |> text
              "impact", value |> PackageTargetPreview.impact |> targetImpact ]

    let previewResult (preview: PackagePreview) =
        map
            [ "operation", preview |> PackagePreview.operation |> operation
              "targets",
              preview |> PackagePreview.targets |> NonEmptyList.toList |> array previewTarget
              "ownerFiles",
              preview |> PackagePreview.ownerFiles |> NonEmptyList.toList |> array text
              "workspaceRevision", text (PackagePreview.workspaceRevision preview)
              "fileFingerprints",
              preview
              |> PackagePreview.fileFingerprints
              |> Map.toSeq
              |> array (fun (path, fingerprint) ->
                  map [ "path", text path; "fingerprint", text fingerprint ])
              "confirmationToken", text (PackagePreview.confirmationToken preview) ]

    let batchPreviewResult (preview: PackageUpdateBatchPreview) =
        map
            [ "updates",
              preview
              |> PackageUpdateBatchPreview.updates
              |> NonEmptyList.toList
              |> array (fun update ->
                  map (
                      [ "package", text (PackageUpdateTargetPreview.package update).Value
                        "targetPreview",
                        update |> PackageUpdateTargetPreview.target |> previewTarget ]
                      @ optional
                          "version"
                          (fun (value: NuGetVersion) -> text value.Value)
                          (PackageUpdateTargetPreview.requestedVersion update)
                  ))
              "ownerFiles",
              preview
              |> PackageUpdateBatchPreview.ownerFiles
              |> NonEmptyList.toList
              |> array text
              "workspaceRevision", text (PackageUpdateBatchPreview.workspaceRevision preview)
              "fileFingerprints",
              preview
              |> PackageUpdateBatchPreview.fileFingerprints
              |> Map.toSeq
              |> array (fun (path, fingerprint) ->
                  map [ "path", text path; "fingerprint", text fingerprint ])
              "confirmationToken", text (PackageUpdateBatchPreview.confirmationToken preview) ]

    let initializeResult (target: PackageWorkspaceTarget) (request: PackageInitializeRequest) =
        let negotiated =
            request.Capabilities
            |> Seq.filter PackageRpcContract.capabilities.Contains
            |> Seq.sort

        map
            [ "protocolVersion",
              map
                  [ "major", integer PackageRpcContract.VersionMajor
                    "minor", integer PackageRpcContract.VersionMinor ]
              "serverInfo", map [ "name", text "dotnet-workspace-explorer"; "version", text "1" ]
              "target",
              map
                  [ "path", text (PackageWorkspaceTarget.path target)
                    "kind", text (target |> PackageWorkspaceTarget.kind |> workspaceKind) ]
              "capabilities", negotiated |> array text
              "limits",
              map
                  [ "maxFrameBytes", integer request.MaximumFrameBytes
                    "maxPageSize", integer request.MaximumPageSize
                    "maxDepth", integer MessagePackRpcCodec.secureLimits.MaximumDepth ] ]

    let accepted (requestId: PackageRequestId) =
        map [ "accepted", boolean true; "requestId", text (requestId.Value.ToString "D") ]

    let cancelled accepted = map [ "accepted", boolean accepted ]
    let shutdown = map [ "accepted", boolean true ]

    let progress (progress: PackageProgress) =
        let fields =
            [ "operationId", text ((PackageProgress.operation progress).Value.ToString "D")
              "stage",
              text (
                  match PackageProgress.stage progress with
                  | PackageOperationStage.Preparing -> "preparing"
                  | PackageOperationStage.Applying -> "applying"
                  | PackageOperationStage.Restoring -> "restoring"
                  | PackageOperationStage.Refreshing -> "refreshing"
                  | PackageOperationStage.Completed -> "completed"
              ) ]

        match PackageProgress.completed progress with
        | None -> map fields
        | Some(completed, total) ->
            map (fields @ [ "completed", integer completed; "total", integer total ])

    let executionResult (execution: PackageExecution) =
        map
            [ "operationId", text (execution.Operation.Value.ToString "D")
              "entries", execution.Entries |> array executionEntry
              "changedFiles", execution.ChangedFiles |> array text
              "restore",
              text (
                  match execution.Restore with
                  | PackageRestoreOutcome.NotRequired -> "notRequired"
                  | PackageRestoreOutcome.Completed -> "completed"
              ) ]

    let failureError (failure: PackageFailure) =
        { Code = PackageFailure.code failure
          Message = failure |> PackageFailure.kind |> safeFailureMessage
          Data =
            Some(
                map
                    [ "retry", failure |> PackageFailure.retry |> retry |> text
                      "recovery", failure |> PackageFailure.recovery |> array executionEntry ]
            ) }

    let private rpcError (error: RpcError) =
        map (
            [ "code", text error.Code; "message", text error.Message ]
            @ optional "data" id error.Data
        )

    let completedNotification
        methodName
        (requestId: PackageRequestId)
        (outcome: Result<RpcValue, PackageFailure>)
        =
        let parameters =
            match outcome with
            | Ok result ->
                map [ "requestId", text (requestId.Value.ToString "D"); "result", result ]
            | Error failure ->
                let error = failureError failure

                map [ "requestId", text (requestId.Value.ToString "D"); "error", rpcError error ]

        Notification(methodName, parameters)

    let transportFailureNotification methodName (requestId: PackageRequestId) error =
        Notification(
            methodName,
            map [ "requestId", text (requestId.Value.ToString "D"); "error", rpcError error ]
        )

    let restoreCompletedNotification (requestId: PackageRequestId) outcome =
        match outcome with
        | Ok() ->
            Notification(
                "package/restore/completed",
                map [ "requestId", text (requestId.Value.ToString "D"); "state", text "refreshed" ]
            )
        | Error(failure: PackageFailure) ->
            let error = failureError failure

            Notification(
                "package/restore/completed",
                map
                    [ "requestId", text (requestId.Value.ToString "D")
                      "state",
                      text (
                          if PackageFailure.kind failure = PackageFailureKind.Cancelled then
                              "cancelled"
                          else
                              "failed"
                      )
                      "error", rpcError error ]
            )

    let restoreTransportFailureNotification (requestId: PackageRequestId) error =
        Notification(
            "package/restore/completed",
            map
                [ "requestId", text (requestId.Value.ToString "D")
                  "state", text "failed"
                  "error", rpcError error ]
        )
