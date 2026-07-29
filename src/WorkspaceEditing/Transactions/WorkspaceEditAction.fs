namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

[<RequireQualifiedAccess>]
type WorkspaceEditAction =
    | ReplaceFile of destination: string * contents: byte array
    | Rename of source: string * destination: string
    | Move of source: string * destination: string
    | Delete of path: string * permanent: bool * recursive: bool
    | Trash of path: string

type ArtifactTrashFailure = { Message: string }

type ArtifactTrash =
    abstract MoveToTrash: string -> Result<unit, ArtifactTrashFailure>
