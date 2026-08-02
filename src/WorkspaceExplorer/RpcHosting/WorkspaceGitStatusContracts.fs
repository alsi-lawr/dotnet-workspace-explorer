namespace Dotnet.WorkspaceExplorer

open System.IO

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

[<RequireQualifiedAccess>]
module internal WorkspaceGitPaths =
    let withoutTrailingDirectorySeparators (path: string) =
        let rootLength =
            Path.GetPathRoot path
            |> Option.ofObj
            |> Option.map _.Length
            |> Option.defaultValue 0

        let mutable normalized = path

        while normalized.Length > rootLength && Path.EndsInDirectorySeparator normalized do
            normalized <- Path.TrimEndingDirectorySeparator normalized

        normalized

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
