namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open System
open System.IO
open System.Text

type internal FreedesktopArtifactTrash(dataHome: string) =
    interface ArtifactTrash with
        member _.MoveToTrash path =
            try
                let root = Path.Combine(dataHome, "Trash")
                let files = Directory.CreateDirectory(Path.Combine(root, "files")).FullName
                let info = Directory.CreateDirectory(Path.Combine(root, "info")).FullName

                if not (OperatingSystem.IsWindows()) then
                    let mode =
                        UnixFileMode.UserRead
                        ||| UnixFileMode.UserWrite
                        ||| UnixFileMode.UserExecute

                    [ root; files; info ]
                    |> List.iter (fun path -> File.SetUnixFileMode(path, mode))

                let baseName = Path.GetFileName path |> Option.ofObj |> Option.defaultValue "item"

                let mutable name = baseName
                let mutable suffix = 1

                while ArtifactFiles.exists (Path.Combine(files, name))
                      || File.Exists(Path.Combine(info, $"{name}.trashinfo")) do
                    name <- $"{baseName}.{suffix}"
                    suffix <- suffix + 1

                let metadata = Path.Combine(info, $"{name}.trashinfo")

                let escaped =
                    Uri
                        .EscapeDataString(Path.GetFullPath path)
                        .Replace("%2F", "/", StringComparison.Ordinal)

                let deleted = DateTime.Now.ToString "yyyy-MM-ddTHH:mm:ss"

                use stream =
                    File.Open(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                use writer = new StreamWriter(stream, UTF8Encoding false, leaveOpen = true)
                writer.Write $"[Trash Info]\nPath={escaped}\nDeletionDate={deleted}\n"
                writer.Flush()
                stream.Flush true

                try
                    ArtifactFiles.move path (Path.Combine(files, name))
                    Ok()
                with ex ->
                    File.Delete metadata
                    Error { Message = ex.Message }
            with ex ->
                Error { Message = ex.Message }
