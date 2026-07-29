namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open System.IO
open System.Text

module internal WorkspaceEditFingerprint =
    let writeValue (writer: BinaryWriter) (value: string) =
        let bytes = Encoding.UTF8.GetBytes value
        writer.Write bytes.Length
        writer.Write bytes

    let writeSection (writer: BinaryWriter) tag (count: int) =
        writeValue writer tag
        writer.Write count
