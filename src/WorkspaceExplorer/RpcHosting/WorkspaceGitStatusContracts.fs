namespace Dotnet.WorkspaceExplorer

type internal GitDecorationState =
    | Changed
    | Added

type internal GitStatusState =
    | Staged
    | Unstaged
    | Renamed
    | Deleted
    | Unmerged
    | Untracked
    | Ignored

[<RequireQualifiedAccess>]
module internal GitStatusStates =
    let ordered = [| Staged; Unstaged; Renamed; Deleted; Unmerged; Untracked; Ignored |]

    let normalize states =
        let present = states |> Set.ofSeq
        ordered |> Array.filter present.Contains

type internal WorkspaceGitPathStatus =
    { Path: string
      States: GitStatusState array
      LegacyState: GitDecorationState option }

type internal WorkspaceGitPathSnapshot =
    { RepositoryRoot: string
      Entries: WorkspaceGitPathStatus array }

type internal GitStatusSnapshot =
    { Available: bool
      LegacyDecorations: (string * GitDecorationState) array
      Decorations: (string * GitStatusState array) array }

type internal GitStatusResponseVersion =
    | Legacy
    | Version2
