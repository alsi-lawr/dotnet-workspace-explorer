namespace Dotnet.WorkspaceExplorer.Rpc

open System
open System.Collections.Immutable

[<RequireQualifiedAccess>]
module PackageRpcContract =
    [<Literal>]
    let ProfileName = "dotnet-workspace-explorer/packages"

    [<Literal>]
    let VersionMajor = 2

    [<Literal>]
    let VersionMinor = 0

    [<Literal>]
    let MaximumPageSize = 200

    [<Literal>]
    let MinimumFrameBytes = 1024

    let capabilities =
        ImmutableHashSet.CreateRange<string>(
            StringComparer.Ordinal,
            [ "packages.sources.v1"
              "packages.source-mapping.v1"
              "packages.search.v2"
              "packages.details.v1"
              "packages.readme.v1"
              "packages.installed.v2"
              "packages.restore.v2"
              "packages.updates.v2"
              "packages.consolidation.v2"
              "packages.preview.v1"
              "packages.batch-preview.v1"
              "packages.execute.v1"
              "packages.batch-execute.v1"
              "packages.cancel.v1"
              "packages.partial-recovery.v1" ]
        )

    let private methods =
        [ "package/sources", Read
          "package/sourceMapping", Read
          "package/search/start", Read
          "package/details", Read
          "package/installed/start", Read
          "package/installed/restore/start", Read
          "package/updates/start", Read
          "package/consolidation/start", Read
          "package/preview", Read
          "package/previewBatch", Read
          "package/execute/start", Mutation
          "package/executeBatch/start", Mutation
          "package/cancel", Control
          "shutdown", Control
          "package/search/batch", NotificationMethod
          "package/search/completed", NotificationMethod
          "package/installed/batch", NotificationMethod
          "package/installed/completed", NotificationMethod
          "package/installed/restore/batch", NotificationMethod
          "package/installed/restore/completed", NotificationMethod
          "package/updates/batch", NotificationMethod
          "package/updates/completed", NotificationMethod
          "package/consolidation/batch", NotificationMethod
          "package/consolidation/completed", NotificationMethod
          "package/operations/progress", NotificationMethod
          "package/operations/completed", NotificationMethod ]

    let current =
        methods
        |> Seq.map (fun (name, classification) ->
            { Name = name
              Classification = classification })
        |> RpcProfile.create ProfileName VersionMajor VersionMinor

    let capabilityForMethod methodName =
        match methodName with
        | "package/sources" -> Some "packages.sources.v1"
        | "package/sourceMapping" -> Some "packages.source-mapping.v1"
        | "package/search/start" -> Some "packages.search.v2"
        | "package/details" -> Some "packages.details.v1"
        | "package/installed/start" -> Some "packages.installed.v2"
        | "package/installed/restore/start" -> Some "packages.restore.v2"
        | "package/updates/start" -> Some "packages.updates.v2"
        | "package/consolidation/start" -> Some "packages.consolidation.v2"
        | "package/preview" -> Some "packages.preview.v1"
        | "package/previewBatch" -> Some "packages.batch-preview.v1"
        | "package/execute/start" -> Some "packages.execute.v1"
        | "package/executeBatch/start" -> Some "packages.batch-execute.v1"
        | "package/cancel" -> Some "packages.cancel.v1"
        | "shutdown" -> None
        | _ -> None

type PackageInitializeRequest =
    { ProtocolMinor: int
      ClientName: string
      Capabilities: ImmutableArray<string>
      MaximumFrameBytes: int
      MaximumPageSize: int }
