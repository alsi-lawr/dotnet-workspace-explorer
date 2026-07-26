namespace Dotnet.CLI.Plus

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading
open Microsoft.VisualBasic.FileIO
open Dotnet.CLI.Plus.Core

[<RequireQualifiedAccess>]
type MutationAction =
    | ReplaceFile of destination: string * contents: byte array
    | Rename of source: string * destination: string
    | Move of source: string * destination: string
    | Delete of path: string * permanent: bool * recursive: bool
    | Trash of path: string

type TrashFailure = { Message: string }

type TrashBackend =
    abstract MoveToTrash: string -> Result<unit, TrashFailure>

module internal CanonicalBinary =
    let writeValue (writer: BinaryWriter) (value: string) =
        let bytes = Encoding.UTF8.GetBytes value
        writer.Write bytes.Length
        writer.Write bytes

    let writeSection (writer: BinaryWriter) tag (count: int) =
        writeValue writer tag
        writer.Write count

module internal MutationFiles =
    let linkTarget path =
        let file = FileInfo path :> FileSystemInfo
        let directory = DirectoryInfo path :> FileSystemInfo

        [ file.LinkTarget; directory.LinkTarget ]
        |> List.choose Option.ofObj
        |> List.tryHead

    let isLink path = linkTarget path |> Option.isSome

    let exists path =
        isLink path || File.Exists path || Directory.Exists path

    let canonicalNoFollow allowTerminalLink path =
        let full = Path.GetFullPath path

        match Path.GetPathRoot full |> Option.ofObj with
        | None -> Error "The path has no filesystem root."
        | Some root ->
            let mutable current = root
            let mutable linked = false

            let segments =
                Path
                    .GetRelativePath(root, full)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)

            for index, segment in Array.indexed segments do
                current <- Path.Combine(current, segment)

                if isLink current && (not allowTerminalLink || index <> segments.Length - 1) then
                    linked <- true

            if linked then
                Error "A symbolic link component is not allowed."
            else
                Ok full

    let isUnder root path =
        let relative = Path.GetRelativePath(root, path)

        relative <> ".."
        && not (relative.StartsWith $"..{Path.DirectorySeparatorChar}")
        && not (Path.IsPathRooted relative)

    let rec private fingerprintAt allowLink path =
        if isLink path then
            if allowLink then
                let target = linkTarget path |> Option.defaultValue String.Empty
                Ok $"l:{SHA256.HashData(Encoding.UTF8.GetBytes target) |> Convert.ToHexString}"
            else
                Error "A symbolic link within a directory cannot be fingerprinted."
        elif File.Exists path then
            use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            Ok $"f:{(FileInfo path).Length}:{SHA256.HashData stream |> Convert.ToHexString}"
        elif Directory.Exists path then
            let children =
                Directory.EnumerateFileSystemEntries path
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> Seq.map (fun child ->
                    fingerprintAt false child
                    |> Result.map (fun value ->
                        Path.GetFileName child |> Option.ofObj |> Option.defaultValue String.Empty,
                        value))
                |> Seq.toArray

            match
                children
                |> Array.tryPick (function
                    | Error error -> Some error
                    | Ok _ -> None)
            with
            | Some error -> Error error
            | None ->
                use stream = new MemoryStream()
                use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                CanonicalBinary.writeSection writer "directory" children.Length

                for name, value in children |> Array.choose Result.toOption do
                    CanonicalBinary.writeValue writer name
                    CanonicalBinary.writeValue writer value

                writer.Flush()
                Ok $"d:{SHA256.HashData(stream.ToArray()) |> Convert.ToHexString}"
        else
            Ok "missing"

    let fingerprint path = fingerprintAt true path

    let rec copyNoFollow source destination =
        if isLink source then
            invalidOp "A symbolic link cannot be copied."
        elif File.Exists source then
            File.Copy(source, destination)
        elif Directory.Exists source then
            Directory.CreateDirectory destination |> ignore

            for child in
                Directory.EnumerateFileSystemEntries source
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)) do
                let name =
                    Path.GetFileName child |> Option.ofObj |> Option.defaultValue String.Empty

                copyNoFollow child (Path.Combine(destination, name))
        else
            raise (FileNotFoundException("The source artifact does not exist.", source))

    let move source destination =
        if isLink source || File.Exists source then
            File.Move(source, destination)
        else
            Directory.Move(source, destination)

    let remove path =
        if isLink path || File.Exists path then
            File.Delete path
        elif Directory.Exists path then
            Directory.Delete(path, true)

    let deletePermanent path recursive =
        if isLink path || File.Exists path then
            File.Delete path
        elif Directory.Exists path then
            Directory.Delete(path, recursive)

    let nonEmptyDirectory path =
        Directory.Exists path
        && not (isLink path)
        && Directory.EnumerateFileSystemEntries path |> Seq.isEmpty |> not

    let isCaseOnlyRename source destination =
        try
            let sourcePath = Path.GetFullPath source
            let destinationPath = Path.GetFullPath destination

            let caseSemantics = HostFileSystemCaseDetector.DetectFromExistingPath sourcePath

            not (String.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            && String.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
            && caseSemantics = HostFileSystemCaseSemantics.Insensitive
        with _ ->
            false

    let identity path =
        let full = Path.GetFullPath path

        let rec nearestExisting candidate =
            if File.Exists candidate || Directory.Exists candidate then
                candidate
            else
                match Path.GetDirectoryName candidate |> Option.ofObj with
                | Some parent when parent <> candidate -> nearestExisting parent
                | _ -> candidate

        match HostFileSystemCaseDetector.DetectFromExistingPath(nearestExisting full) with
        | HostFileSystemCaseSemantics.Insensitive -> full.ToUpperInvariant()
        | _ -> full

    let temporaryBeside (path: string) (kind: string) =
        let directory =
            Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue "."

        let name = Path.GetFileName path |> Option.ofObj |> Option.defaultValue "artifact"
        Path.Combine(directory, $".{name}.dotnet-plus-{kind}-{Guid.NewGuid():N}")
