namespace Dotnet.WorkspaceExplorer.Workspaces

open System
open System.IO
open System.Security.Cryptography
open System.Text

module private WorkspaceValue =
    let nonEmpty argumentName (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg argumentName "A non-empty value is required."

        value

module private WorkspaceIdentityHash =
    let sha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString

[<AbstractClass; Sealed>]
type WorkspaceIdentityVersion private () =
    static member MajorVersion = 1

type FileSystemCaseSensitivity =
    | Sensitive = 0
    | Insensitive = 1

[<AbstractClass; Sealed>]
type FileSystemCaseSensitivityDetector private () =
    /// Detects case comparison behaviour from existing entries without creating probe artifacts.
    static member DetectFromExistingPath(existingPath: string) =
        existingPath |> WorkspaceValue.nonEmpty (nameof existingPath) |> ignore

        let fullPath = Path.GetFullPath existingPath

        if not (File.Exists fullPath || Directory.Exists fullPath) then
            invalidArg (nameof existingPath) "Case semantics require an existing filesystem path."

        let alternateName (name: string) =
            match name |> Seq.tryFindIndex Char.IsLetter with
            | Some index ->
                let characters = name.ToCharArray()
                let character = characters[index]

                characters[index] <-
                    if Char.IsUpper character then
                        Char.ToLowerInvariant character
                    else
                        Char.ToUpperInvariant character

                Some(new String(characters))
            | None -> None

        let detect (candidate: string) =
            let name =
                Path.GetFileName candidate |> Option.ofObj |> Option.defaultValue String.Empty

            match alternateName name, Path.GetDirectoryName candidate |> Option.ofObj with
            | Some alternate, Some parent when alternate <> name ->
                let alternatePath = Path.Combine(parent, alternate)

                if File.Exists alternatePath || Directory.Exists alternatePath then
                    let matchingEntries =
                        Directory.EnumerateFileSystemEntries parent
                        |> Seq.filter (fun entry ->
                            String.Equals(
                                Path.GetFileName entry,
                                name,
                                StringComparison.OrdinalIgnoreCase
                            ))
                        |> Seq.truncate 2
                        |> Seq.length

                    if matchingEntries > 1 then
                        Some FileSystemCaseSensitivity.Sensitive
                    else
                        Some FileSystemCaseSensitivity.Insensitive
                else
                    Some FileSystemCaseSensitivity.Sensitive
            | _ -> None

        let containingDirectory =
            if Directory.Exists fullPath then
                fullPath
            else
                Path.GetDirectoryName fullPath |> Option.ofObj |> Option.defaultValue fullPath

        let children =
            try
                Directory.EnumerateFileSystemEntries containingDirectory |> Seq.toArray
            with
            | :? IOException
            | :? UnauthorizedAccessException -> Array.empty

        let rec ancestors (candidate: string) =
            seq {
                yield candidate

                match Path.GetDirectoryName candidate |> Option.ofObj with
                | Some parent when parent <> candidate -> yield! ancestors parent
                | _ -> ()
            }

        seq {
            yield fullPath
            yield! children
            yield! ancestors containingDirectory
        }
        |> Seq.distinct
        |> Seq.tryPick detect
        |> Option.defaultValue FileSystemCaseSensitivity.Sensitive
