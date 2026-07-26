namespace Dotnet.CLI.Plus.Core

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text

module internal Validation =
    let nonEmpty argumentName (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg argumentName "A non-empty value is required."

        value

module internal Hash =
    let sha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString

[<AbstractClass; Sealed>]
type WorkspaceContract private () =
    static member MajorVersion = 1

type HostFileSystemCaseSemantics =
    | Sensitive = 0
    | Insensitive = 1

[<AbstractClass; Sealed>]
type HostFileSystemCaseDetector private () =
    /// Detects case comparison behaviour using the resolved, existing target itself.
    static member DetectFromExistingPath(existingPath: string) =
        existingPath |> Validation.nonEmpty (nameof existingPath) |> ignore

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
            | None -> HostFileSystemCaseSemantics.Sensitive
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
                        HostFileSystemCaseSemantics.Sensitive
                    else
                        HostFileSystemCaseSemantics.Insensitive
                else
                    HostFileSystemCaseSemantics.Sensitive
        | _ -> HostFileSystemCaseSemantics.Sensitive

type WorkspaceFormat =
    | Sln = 0
    | Slnx = 1
    | Slnf = 2

type WorkspaceAccess =
    | ReadOnly = 0
    | ReadWrite = 1

type WorkspaceNodeKind =
    | Workspace = 0
    | SolutionFolder = 1
    | Project = 2
    | ProjectItem = 3
    | SolutionItem = 4
    | Configuration = 5
    | Platform = 6
    | Placeholder = 7

type WorkspaceTargetPath private (value: string) =
    member _.Value = value

    member internal _.IdentityValue(caseSemantics: HostFileSystemCaseSemantics) =
        match caseSemantics with
        | HostFileSystemCaseSemantics.Insensitive -> value.ToUpperInvariant()
        | _ -> value

    static member Create(targetPath: string) =
        targetPath
        |> Validation.nonEmpty (nameof targetPath)
        |> Path.GetFullPath
        |> WorkspaceTargetPath

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? WorkspaceTargetPath as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type WorkspaceId private (value: string) =
    member _.Value = value

    static member Create
        (targetPath: WorkspaceTargetPath, caseSemantics: HostFileSystemCaseSemantics)
        =
        if isNull (box targetPath) then
            nullArg (nameof targetPath)

        $"workspace-contract:{WorkspaceContract.MajorVersion}\n{targetPath.IdentityValue caseSemantics}"
        |> Hash.sha256
        |> WorkspaceId

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? WorkspaceId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type NodeSemanticIdentity private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> ignore

        let normalized =
            value.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> String.concat "/"

        normalized |> Validation.nonEmpty (nameof value) |> NodeSemanticIdentity

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? NodeSemanticIdentity as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type NodeId private (value: string) =
    member _.Value = value

    static member Create
        (workspaceId: WorkspaceId, kind: WorkspaceNodeKind, semanticIdentity: NodeSemanticIdentity)
        =
        if isNull (box workspaceId) then
            nullArg (nameof workspaceId)

        if isNull (box semanticIdentity) then
            nullArg (nameof semanticIdentity)

        $"{workspaceId.Value}\n{kind}\n{semanticIdentity.Value}"
        |> Hash.sha256
        |> NodeId

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? NodeId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type WorkspaceRevision private (value: int64) =
    member _.Value = value

    member _.Next() =
        if value = Int64.MaxValue then
            invalidOp "The workspace revision cannot advance beyond Int64.MaxValue."

        WorkspaceRevision(value + 1L)

    static member Create(value: int64) =
        if value < 0L then
            invalidArg (nameof value) "A workspace revision cannot be negative."

        WorkspaceRevision value

    override _.ToString() =
        value.ToString CultureInfo.InvariantCulture

    override _.Equals other =
        match other with
        | :? WorkspaceRevision as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() = value.GetHashCode()
