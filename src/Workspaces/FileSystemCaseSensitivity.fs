namespace Dotnet.WorkspaceExplorer.Workspaces

open System
open System.Globalization
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
    /// Detects case comparison behaviour using the resolved, existing target itself.
    static member DetectFromExistingPath(existingPath: string) =
        existingPath |> WorkspaceValue.nonEmpty (nameof existingPath) |> ignore

        let fullPath = Path.GetFullPath existingPath

        if not (File.Exists fullPath || Directory.Exists fullPath) then
            invalidArg (nameof existingPath) "Case semantics require an existing filesystem path."

        let name =
            Path.GetFileName fullPath |> Option.ofObj |> Option.defaultValue String.Empty

        let alternateName =
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

        match alternateName with
        | Some alternate when alternate <> name ->
            match Path.GetDirectoryName fullPath |> Option.ofObj with
            | None -> FileSystemCaseSensitivity.Sensitive
            | Some parent ->
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
                        FileSystemCaseSensitivity.Sensitive
                    else
                        FileSystemCaseSensitivity.Insensitive
                else
                    FileSystemCaseSensitivity.Sensitive
        | _ -> FileSystemCaseSensitivity.Sensitive
