namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.RegularExpressions

module internal CommandTargetDiscovery =
    let isProjectFile (path: string) =
        match Path.GetExtension(path).ToLowerInvariant() with
        | ".csproj"
        | ".fsproj"
        | ".vbproj" -> true
        | _ -> false

    let isFileBasedApp (path: string) =
        String.Equals(Path.GetExtension path, ".cs", StringComparison.OrdinalIgnoreCase)

    let projects (directory: string) =
        Directory.EnumerateFiles(directory, "*.*proj", SearchOption.TopDirectoryOnly)
        |> Seq.filter isProjectFile
        |> Seq.sort
        |> Seq.toList

    let defaultProject () =
        match projects (Directory.GetCurrentDirectory()) with
        | [ project ] -> Ok project
        | [] -> Error "No project exists in the current directory."
        | _ -> Error "More than one project exists in the current directory; use --project."

    let expandSolutionOperand (operand: string) =
        if operand.IndexOfAny [| '*'; '?' |] >= 0 then
            let full = Path.GetFullPath operand
            let segments = full.Replace('\\', '/').Split '/'

            let wildcard =
                segments
                |> Array.findIndex (fun segment -> segment.IndexOfAny [| '*'; '?' |] >= 0)

            let prefix = segments |> Array.take wildcard |> String.concat "/"

            let root =
                if String.IsNullOrEmpty prefix then
                    Path.DirectorySeparatorChar.ToString()
                else
                    prefix

            let expression =
                "^"
                + Regex
                    .Escape(full.Replace('\\', '/'))
                    .Replace("\\*\\*", ".*")
                    .Replace("\\*", "[^/]*")
                    .Replace("\\?", "[^/]")
                + "$"

            let matcher = Regex(expression, RegexOptions.CultureInvariant)

            if Directory.Exists root then
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun path -> matcher.IsMatch(path.Replace('\\', '/')))
                |> Seq.toList
            else
                []
        else
            [ operand ]
