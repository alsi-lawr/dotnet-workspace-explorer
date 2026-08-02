namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Generic
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceIndex

[<RequireQualifiedAccess>]
type internal AddExistingAvailability =
    | Available
    | AlreadyPresent
    | Ineligible

type internal AddExistingEntry =
    { Id: string
      Path: string
      ParentPath: string
      DisplayName: string
      IsDirectory: bool
      Selectable: bool
      Availability: AddExistingAvailability
      GitStates: GitStatusState array
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
      RegisteredPaths: HashSet<string>
      PresentationVersion2: bool
      GitSnapshot: WorkspaceGitPathSnapshot option }

[<RequireQualifiedAccess>]
module private AddExistingFormatting =
    let private availability =
        function
        | AddExistingAvailability.Available -> "available"
        | AddExistingAvailability.AlreadyPresent -> "alreadyPresent"
        | AddExistingAvailability.Ineligible -> "ineligible"

    let private gitState =
        function
        | GitStatusState.Staged -> "staged"
        | GitStatusState.Unstaged -> "unstaged"
        | GitStatusState.Renamed -> "renamed"
        | GitStatusState.Deleted -> "deleted"
        | GitStatusState.Unmerged -> "unmerged"
        | GitStatusState.Untracked -> "untracked"
        | GitStatusState.Ignored -> "ignored"

    let entry presentationVersion2 (value: AddExistingEntry) =
        let fields =
            ResizeArray<string * RpcValue>
                [ "entryId", RpcValue.String value.Id
                  "displayName", RpcValue.String value.DisplayName
                  "kind", RpcValue.String(if value.IsDirectory then "directory" else "file")
                  "expandable", RpcValue.Boolean value.Expandable
                  "selectable", RpcValue.Boolean value.Selectable ]

        if presentationVersion2 then
            fields.Add("availability", RpcValue.String(availability value.Availability))

            fields.Add(
                "gitStates",
                value.GitStates |> Seq.map (gitState >> RpcValue.String) |> RpcValue.array
            )

        value.IconHint
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.iter (fun hint -> fields.Add("iconHint", RpcValue.String hint))

        RpcValue.map fields

    let page
        presentationVersion2
        revision
        selectorId
        parentEntryId
        (entries: AddExistingEntry array)
        nextToken
        =
        let fields =
            ResizeArray<string * RpcValue>
                [ "revision", RpcValue.Integer revision
                  "selectorId", RpcValue.String selectorId
                  "parentEntryId", RpcValue.String parentEntryId
                  "entries", entries |> Seq.map (entry presentationVersion2) |> RpcValue.array ]

        nextToken
        |> Option.iter (fun token -> fields.Add("nextToken", RpcValue.String token))

        RpcValue.map fields

    let start
        revision
        selectorId
        (expiresAtUtc: DateTimeOffset)
        presentationVersion2
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
                  "root", entry presentationVersion2 root
                  "entries", entries |> Seq.map (entry presentationVersion2) |> RpcValue.array ]

        nextToken
        |> Option.iter (fun token -> fields.Add("nextToken", RpcValue.String token))

        RpcValue.map fields
