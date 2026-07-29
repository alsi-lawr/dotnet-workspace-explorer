namespace Dotnet.WorkspaceExplorer.Workspaces

open System

type WorkspaceOperationId private (value: Guid) =
    member _.Value = value
    static member New() = WorkspaceOperationId(Guid.NewGuid())
    override _.ToString() = value.ToString "N"

    override _.Equals other =
        match other with
        | :? WorkspaceOperationId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() = value.GetHashCode()

type WorkspaceOperationState =
    | Queued = 0
    | Running = 1
    | Succeeded = 2
    | Failed = 3
    | Cancelled = 4
