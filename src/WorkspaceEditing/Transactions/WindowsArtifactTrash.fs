namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open System.IO
open Microsoft.VisualBasic.FileIO

type internal WindowsArtifactTrash() =
    interface ArtifactTrash with
        member _.MoveToTrash path =
            try
                if File.Exists path then
                    FileSystem.DeleteFile(
                        path,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException
                    )
                elif Directory.Exists path then
                    FileSystem.DeleteDirectory(
                        path,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException
                    )
                else
                    raise (FileNotFoundException("The artifact does not exist.", path))

                Ok()
            with ex ->
                Error { Message = ex.Message }
