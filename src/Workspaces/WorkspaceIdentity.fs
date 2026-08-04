namespace Dotnet.WorkspaceExplorer.Workspaces

open System
open System.Globalization
open System.IO

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
    | ProjectFolder = 8
    | ProjectFile = 9
    | DependencyContainer = 10
    | Dependency = 11
    | DependencyProperty = 12

type WorkspacePath private (value: string) =
    member _.Value = value

    member internal _.IdentityValue(caseSemantics: FileSystemCaseSensitivity) =
        match caseSemantics with
        | FileSystemCaseSensitivity.Insensitive -> value.ToUpperInvariant()
        | _ -> value

    static member Create(targetPath: string) =
        targetPath
        |> WorkspaceValue.nonEmpty (nameof targetPath)
        |> Path.GetFullPath
        |> WorkspacePath

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? WorkspacePath as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type WorkspaceId private (value: string) =
    member _.Value = value

    static member Create(targetPath: WorkspacePath, caseSemantics: FileSystemCaseSensitivity) =
        if isNull (box targetPath) then
            nullArg (nameof targetPath)

        $"workspace-contract:{WorkspaceIdentityVersion.MajorVersion}\n{targetPath.IdentityValue caseSemantics}"
        |> WorkspaceIdentityHash.sha256
        |> WorkspaceId

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? WorkspaceId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type WorkspaceNodeIdentity private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> WorkspaceValue.nonEmpty (nameof value) |> ignore

        let normalized =
            value.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> String.concat "/"

        normalized |> WorkspaceValue.nonEmpty (nameof value) |> WorkspaceNodeIdentity

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? WorkspaceNodeIdentity as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type WorkspaceNodeId private (value: string) =
    member _.Value = value

    static member Parse(value: string) =
        value |> WorkspaceValue.nonEmpty (nameof value) |> WorkspaceNodeId

    static member Create
        (workspaceId: WorkspaceId, kind: WorkspaceNodeKind, semanticIdentity: WorkspaceNodeIdentity)
        =
        if isNull (box workspaceId) then
            nullArg (nameof workspaceId)

        if isNull (box semanticIdentity) then
            nullArg (nameof semanticIdentity)

        $"{workspaceId.Value}\n{kind}\n{semanticIdentity.Value}"
        |> WorkspaceIdentityHash.sha256
        |> WorkspaceNodeId

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? WorkspaceNodeId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type WorkspaceRevision private (value: int64) =
    member _.Value = value

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
