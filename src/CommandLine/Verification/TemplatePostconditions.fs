namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"

open System.IO

module internal TemplatePostconditions =
    let snapshot (directory: string) =
        if Directory.Exists directory then
            Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path ->
                if File.Exists path then
                    let info = FileInfo path
                    path, (false, info.Length, info.LastWriteTimeUtc.Ticks)
                else
                    let info = DirectoryInfo path
                    path, (true, 0L, info.LastWriteTimeUtc.Ticks))
            |> Map.ofSeq
        else
            Map.empty

    let verifyNew (output: string) before =
        let after = snapshot output

        if after <> before then
            Ok None
        else
            Error(
                DirectCommandFailures.verification
                    "The template command did not create a verifiable output state."
            )
