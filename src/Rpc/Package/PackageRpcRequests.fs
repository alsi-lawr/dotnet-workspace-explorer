namespace Dotnet.WorkspaceExplorer.Rpc

open System
open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
type PackageRpcRequest =
    | Sources of requestId: PackageRequestId
    | SourceMapping of
        requestId: PackageRequestId *
        package: PackageId *
        source: PackageSourceId option *
        restoredTransitives: PackageId list option
    | Search of
        requestId: PackageRequestId *
        search: PackageSearch *
        pageSize: int *
        continuation: string option
    | Details of
        requestId: PackageRequestId *
        package: PackageId *
        version: PackageVersionSelection *
        source: PackageSourceId
    | Installed of requestId: PackageRequestId * pageSize: int * offset: int
    | Updates of
        requestId: PackageRequestId *
        prerelease: PrereleaseSelection *
        pageSize: int *
        offset: int
    | Consolidation of requestId: PackageRequestId * pageSize: int * offset: int
    | Preview of
        requestId: PackageRequestId *
        operation: RequestedPackageOperation *
        targets: NonEmptyList<PackageTargetScope> *
        source: PackageSourceId option
    | PreviewBatch of
        requestId: PackageRequestId *
        updates: NonEmptyList<PackageUpdateSelection> *
        source: PackageSourceId option
    | Execute of requestId: PackageRequestId * confirmationToken: string
    | ExecuteBatch of requestId: PackageRequestId * confirmationToken: string
    | CancelRequest of PackageRequestId
    | CancelOperation of PackageOperationId
    | Shutdown

[<RequireQualifiedAccess>]
module PackageRpc =
    let private invalid message = Error(RpcErrors.invalidParams message)

    let private requireEmpty parameters =
        let fields = RpcValue.requireMap "params" parameters
        RpcValue.ensureOnly "params" Seq.empty fields

    let private requiredString name (fields: ImmutableDictionary<string, RpcValue>) =
        fields |> RpcValue.requireField name |> RpcValue.requireString name

    let private optionalString name (fields: ImmutableDictionary<string, RpcValue>) =
        match RpcValue.optionalField name fields with
        | None
        | Some RpcValue.Nil -> None
        | Some value -> Some(RpcValue.requireString name value)

    let private requiredInt name minimum maximum fields =
        let value = fields |> RpcValue.requireField name |> RpcValue.requireInteger name

        if value < int64 minimum || value > int64 maximum then
            invalidArg name $"{name} must be between {minimum} and {maximum}."

        int value

    let private requiredArray name (fields: ImmutableDictionary<string, RpcValue>) =
        fields |> RpcValue.requireField name |> RpcValue.requireArray name |> Seq.toList

    let private createValue name create value =
        match create value with
        | Ok result -> result
        | Error _ -> invalidArg name $"{name} is invalid."

    let private requestId fields =
        let value = requiredString "requestId" fields

        match Guid.TryParseExact(value, "D") with
        | true, parsed -> createValue "requestId" PackageRequestId.create parsed
        | _ -> invalidArg "requestId" "requestId must be a non-empty canonical UUID."

    let private packageId name value = createValue name PackageId.create value

    let private sourceId name value =
        createValue name PackageSourceId.create value

    let private version name value =
        createValue name NuGetVersion.create value

    let private versionRange name value =
        createValue name NuGetVersionRange.create value

    let private project name value =
        createValue name PackageProjectId.create value

    let private framework name value =
        createValue name TargetFramework.create value

    let private runtime name value =
        createValue name RuntimeIdentifier.create value

    let private parseStringArray name value =
        value
        |> RpcValue.requireArray name
        |> Seq.map (RpcValue.requireString name)
        |> Seq.toList

    let private page fields =
        let pageSize =
            match RpcValue.optionalField "pageSize" fields with
            | None -> 50
            | Some _ -> requiredInt "pageSize" 1 PackageRpcContract.MaximumPageSize fields

        let offset =
            match optionalString "continuation" fields with
            | None -> 0
            | Some value ->
                match Int32.TryParse value with
                | true, parsed when parsed >= 0 -> parsed
                | _ -> invalidArg "continuation" "continuation is invalid."

        pageSize, offset

    let private parseTarget value =
        let fields = RpcValue.requireMap "target" value
        RpcValue.ensureOnly "target" [ "project"; "framework"; "runtime" ] fields
        let selectedProject = requiredString "project" fields |> project "project"

        match optionalString "framework" fields, optionalString "runtime" fields with
        | None, None -> PackageTargetScope.Project selectedProject
        | Some selectedFramework, None ->
            PackageTargetScope.Framework(selectedProject, framework "framework" selectedFramework)
        | Some selectedFramework, Some selectedRuntime ->
            PackageTargetScope.Runtime(
                selectedProject,
                framework "framework" selectedFramework,
                runtime "runtime" selectedRuntime
            )
        | None, Some _ -> invalidArg "target" "runtime requires framework."

    let private parseTargets fields =
        requiredArray "targets" fields
        |> List.map parseTarget
        |> NonEmptyList.tryCreate
        |> Option.defaultWith (fun () -> invalidArg "targets" "targets must not be empty.")

    let private parseVersionSelection value =
        let fields = RpcValue.requireMap "version" value
        RpcValue.ensureOnly "version" [ "kind"; "value" ] fields

        match requiredString "kind" fields, optionalString "value" fields with
        | "latest", None -> PackageVersionSelection.Latest
        | "exact", Some value -> PackageVersionSelection.Exact(version "value" value)
        | "range", Some value -> PackageVersionSelection.Range(versionRange "value" value)
        | _ -> invalidArg "version" "version selection is invalid."

    let private parseOperation value =
        let fields = RpcValue.requireMap "operation" value
        RpcValue.ensureOnly "operation" [ "kind"; "package"; "version" ] fields
        let package = requiredString "package" fields |> packageId "package"

        match requiredString "kind" fields, optionalString "version" fields with
        | "installLatest", None -> RequestedPackageOperation.InstallLatest package
        | "installVersion", Some selected ->
            RequestedPackageOperation.InstallVersion(package, version "version" selected)
        | "updateLatest", None -> RequestedPackageOperation.UpdateLatest package
        | "updateVersion", Some selected ->
            RequestedPackageOperation.UpdateVersion(package, version "version" selected)
        | "uninstall", None -> RequestedPackageOperation.Uninstall package
        | "consolidate", Some selected ->
            RequestedPackageOperation.ConsolidateVersion(package, version "version" selected)
        | _ -> invalidArg "operation" "operation is invalid."

    let private parseUpdate value =
        let fields = RpcValue.requireMap "update" value
        RpcValue.ensureOnly "update" [ "package"; "version"; "target" ] fields
        let package = requiredString "package" fields |> packageId "package"
        let target = fields |> RpcValue.requireField "target" |> parseTarget

        match optionalString "version" fields with
        | Some selected ->
            PackageUpdateSelection.version package (version "version" selected) target
        | None -> PackageUpdateSelection.latest package target

    let private parseInitializeFields parameters =
        let fields = RpcValue.requireMap "params" parameters

        RpcValue.ensureOnly
            "params"
            [ "protocolVersion"; "clientInfo"; "capabilities"; "limits" ]
            fields

        let protocol =
            fields
            |> RpcValue.requireField "protocolVersion"
            |> RpcValue.requireMap "protocolVersion"

        RpcValue.ensureOnly "protocolVersion" [ "major"; "minor" ] protocol
        let major = requiredInt "major" 0 Int32.MaxValue protocol
        let minor = requiredInt "minor" 0 Int32.MaxValue protocol

        if major <> PackageRpcContract.VersionMajor then
            invalidArg "protocolVersion" "The package protocol major version is not supported."

        if minor > PackageRpcContract.VersionMinor then
            invalidArg "protocolVersion" "The package protocol minor version is not supported."

        let client =
            fields |> RpcValue.requireField "clientInfo" |> RpcValue.requireMap "clientInfo"

        RpcValue.ensureOnly "clientInfo" [ "name" ] client
        let clientName = requiredString "name" client

        let capabilities =
            fields
            |> RpcValue.requireField "capabilities"
            |> parseStringArray "capabilities"
            |> Seq.distinct
            |> ImmutableArray.CreateRange

        let limits =
            fields |> RpcValue.requireField "limits" |> RpcValue.requireMap "limits"

        RpcValue.ensureOnly "limits" [ "maxFrameBytes"; "maxPageSize" ] limits

        { ProtocolMinor = minor
          ClientName = clientName
          Capabilities = capabilities
          MaximumFrameBytes =
            requiredInt
                "maxFrameBytes"
                PackageRpcContract.MinimumFrameBytes
                MessagePackRpcCodec.secureLimits.MaximumValueBytes
                limits
          MaximumPageSize = requiredInt "maxPageSize" 1 PackageRpcContract.MaximumPageSize limits }

    let parseInitialize parameters =
        try
            Ok(parseInitializeFields parameters)
        with
        | :? ArgumentException -> invalid "Package initialization parameters are invalid."
        | _ -> invalid "Package initialization parameters are invalid."

    let private requestFields allowed parameters =
        let fields = RpcValue.requireMap "params" parameters
        RpcValue.ensureOnly "params" allowed fields
        fields

    let parseRequest methodName parameters =
        try
            let parsed =
                match methodName with
                | "package/sources" ->
                    let fields = requestFields [ "requestId" ] parameters
                    PackageRpcRequest.Sources(requestId fields)
                | "package/sourceMapping" ->
                    let fields =
                        requestFields
                            [ "requestId"; "package"; "source"; "restoredTransitives" ]
                            parameters

                    let transitives =
                        match RpcValue.optionalField "restoredTransitives" fields with
                        | None
                        | Some RpcValue.Nil -> None
                        | Some value ->
                            value
                            |> parseStringArray "restoredTransitives"
                            |> List.map (packageId "restoredTransitives")
                            |> Some

                    PackageRpcRequest.SourceMapping(
                        requestId fields,
                        requiredString "package" fields |> packageId "package",
                        optionalString "source" fields |> Option.map (sourceId "source"),
                        transitives
                    )
                | "package/search/start" ->
                    let fields =
                        requestFields
                            [ "requestId"
                              "term"
                              "includePrerelease"
                              "source"
                              "pageSize"
                              "continuation" ]
                            parameters

                    let term =
                        match optionalString "term" fields with
                        | None -> PackageSearchTerm.AllPackages
                        | Some value -> PackageSearchTerm.Matching value

                    let prerelease =
                        match RpcValue.optionalField "includePrerelease" fields with
                        | None -> PrereleaseSelection.StableOnly
                        | Some(RpcValue.Boolean true) -> PrereleaseSelection.IncludePrerelease
                        | Some(RpcValue.Boolean false) -> PrereleaseSelection.StableOnly
                        | Some _ ->
                            invalidArg "includePrerelease" "includePrerelease must be boolean."

                    let source = optionalString "source" fields |> Option.map (sourceId "source")

                    PackageRpcRequest.Search(
                        requestId fields,
                        { Term = term
                          Prerelease = prerelease
                          Source = source },
                        requiredInt "pageSize" 1 PackageRpcContract.MaximumPageSize fields,
                        optionalString "continuation" fields
                    )
                | "package/details" ->
                    let fields =
                        requestFields [ "requestId"; "package"; "version"; "source" ] parameters

                    PackageRpcRequest.Details(
                        requestId fields,
                        requiredString "package" fields |> packageId "package",
                        fields |> RpcValue.requireField "version" |> parseVersionSelection,
                        requiredString "source" fields |> sourceId "source"
                    )
                | "package/installed" ->
                    let fields =
                        requestFields [ "requestId"; "pageSize"; "continuation" ] parameters

                    let pageSize, offset = page fields
                    PackageRpcRequest.Installed(requestId fields, pageSize, offset)
                | "package/updates" ->
                    let fields =
                        requestFields
                            [ "requestId"; "includePrerelease"; "pageSize"; "continuation" ]
                            parameters

                    let prerelease =
                        match RpcValue.optionalField "includePrerelease" fields with
                        | None
                        | Some(RpcValue.Boolean false) -> PrereleaseSelection.StableOnly
                        | Some(RpcValue.Boolean true) -> PrereleaseSelection.IncludePrerelease
                        | Some _ ->
                            invalidArg "includePrerelease" "includePrerelease must be boolean."

                    let pageSize, offset = page fields
                    PackageRpcRequest.Updates(requestId fields, prerelease, pageSize, offset)
                | "package/consolidation" ->
                    let fields =
                        requestFields [ "requestId"; "pageSize"; "continuation" ] parameters

                    let pageSize, offset = page fields
                    PackageRpcRequest.Consolidation(requestId fields, pageSize, offset)
                | "package/preview" ->
                    let fields =
                        requestFields [ "requestId"; "operation"; "targets"; "source" ] parameters

                    PackageRpcRequest.Preview(
                        requestId fields,
                        fields |> RpcValue.requireField "operation" |> parseOperation,
                        parseTargets fields,
                        optionalString "source" fields |> Option.map (sourceId "source")
                    )
                | "package/previewBatch" ->
                    let fields = requestFields [ "requestId"; "updates"; "source" ] parameters

                    let updates =
                        requiredArray "updates" fields
                        |> List.map parseUpdate
                        |> NonEmptyList.tryCreate
                        |> Option.defaultWith (fun () ->
                            invalidArg "updates" "updates must not be empty.")

                    PackageRpcRequest.PreviewBatch(
                        requestId fields,
                        updates,
                        optionalString "source" fields |> Option.map (sourceId "source")
                    )
                | "package/execute/start" ->
                    let fields = requestFields [ "requestId"; "confirmationToken" ] parameters

                    PackageRpcRequest.Execute(
                        requestId fields,
                        requiredString "confirmationToken" fields
                    )
                | "package/executeBatch/start" ->
                    let fields = requestFields [ "requestId"; "confirmationToken" ] parameters

                    PackageRpcRequest.ExecuteBatch(
                        requestId fields,
                        requiredString "confirmationToken" fields
                    )
                | "package/cancel" ->
                    let fields = requestFields [ "requestId"; "operationId" ] parameters

                    match
                        optionalString "requestId" fields, optionalString "operationId" fields
                    with
                    | Some value, None ->
                        let parsed =
                            match Guid.TryParseExact(value, "D") with
                            | true, identifier ->
                                createValue "requestId" PackageRequestId.create identifier
                            | _ -> invalidArg "requestId" "requestId must be a canonical UUID."

                        PackageRpcRequest.CancelRequest parsed
                    | None, Some value ->
                        let parsed =
                            match Guid.TryParseExact(value, "D") with
                            | true, identifier ->
                                createValue "operationId" PackageOperationId.create identifier
                            | _ -> invalidArg "operationId" "operationId must be a canonical UUID."

                        PackageRpcRequest.CancelOperation parsed
                    | _ -> invalidArg "params" "Provide exactly one requestId or operationId."
                | "shutdown" ->
                    requireEmpty parameters
                    PackageRpcRequest.Shutdown
                | _ -> invalidArg "methodName" "The method is not part of the package protocol."

            Ok parsed
        with
        | :? ArgumentException -> invalid "Package request parameters are invalid."
        | _ -> invalid "Package request parameters are invalid."

    let requestedPageSize =
        function
        | PackageRpcRequest.Search(_, _, pageSize, _)
        | PackageRpcRequest.Installed(_, pageSize, _)
        | PackageRpcRequest.Updates(_, _, pageSize, _)
        | PackageRpcRequest.Consolidation(_, pageSize, _) -> Some pageSize
        | _ -> None
