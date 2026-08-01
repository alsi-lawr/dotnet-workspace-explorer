namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Generic
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex

type internal AddExistingEntry =
    { Id: string
      Path: string
      ParentPath: string
      DisplayName: string
      IsDirectory: bool
      Selectable: bool
      Expandable: bool
      IconHint: string option
      Fingerprint: string option }

type internal AddExistingDirectorySnapshot =
    { Fingerprint: string
      Entries: AddExistingEntry array }

type internal AddExistingContinuation =
    { ParentEntryId: string
      ParentPath: string
      Snapshot: string
      Offset: int }

type internal AddExistingSession =
    { Id: string
      SelectionId: string
      Revision: int64
      ExpiresAtUtc: DateTimeOffset
      RootPath: string
      RootEntry: AddExistingEntry
      Target: WorkspaceSemanticContext
      Entries: Dictionary<string, AddExistingEntry>
      Snapshots: Dictionary<string, AddExistingDirectorySnapshot>
      Continuations: Dictionary<string, AddExistingContinuation>
      RegisteredPaths: HashSet<string> }

[<RequireQualifiedAccess>]
module private AddExistingFormatting =
    let entry (value: AddExistingEntry) =
        let fields =
            ResizeArray<string * RpcValue>
                [ "entryId", RpcValue.String value.Id
                  "displayName", RpcValue.String value.DisplayName
                  "kind", RpcValue.String(if value.IsDirectory then "directory" else "file")
                  "expandable", RpcValue.Boolean value.Expandable
                  "selectable", RpcValue.Boolean value.Selectable ]

        value.IconHint
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.iter (fun hint -> fields.Add("iconHint", RpcValue.String hint))

        RpcValue.map fields

    let page revision selectorId parentEntryId (entries: AddExistingEntry array) nextToken =
        let fields =
            ResizeArray<string * RpcValue>
                [ "revision", RpcValue.Integer revision
                  "selectorId", RpcValue.String selectorId
                  "parentEntryId", RpcValue.String parentEntryId
                  "entries", entries |> Seq.map entry |> RpcValue.array ]

        nextToken
        |> Option.iter (fun token -> fields.Add("nextToken", RpcValue.String token))

        RpcValue.map fields

    let start
        revision
        selectorId
        (expiresAtUtc: DateTimeOffset)
        root
        (entries: AddExistingEntry array)
        nextToken
        =
        let fields =
            ResizeArray<string * RpcValue>
                [ "revision", RpcValue.Integer revision
                  "selectorId", RpcValue.String selectorId
                  "expiresAtUtc", RpcValue.String(expiresAtUtc.ToString "O")
                  "maxSelectionCount", RpcValue.Integer 256L
                  "root", entry root
                  "entries", entries |> Seq.map entry |> RpcValue.array ]

        nextToken
        |> Option.iter (fun token -> fields.Add("nextToken", RpcValue.String token))

        RpcValue.map fields
