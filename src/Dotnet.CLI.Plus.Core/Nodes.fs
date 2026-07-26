namespace Dotnet.CLI.Plus.Core

open System.Collections.Immutable

type WorkspaceCapabilityProfile =
    | Full = 0
    | ReadOnly = 1
    | UnknownProjectSystem = 2

type WorkspaceNodeLoadState =
    | Hydrated = 0
    | Unhydrated = 1
    | FilteredOut = 2

type WorkspaceCapabilityId private (value: string) =
    static let read = WorkspaceCapabilityId "workspace.read"
    static let write = WorkspaceCapabilityId "workspace.write"
    member _.Value = value
    static member Read = read
    static member Write = write
    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? WorkspaceCapabilityId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        System.StringComparer.Ordinal.GetHashCode value

type WorkspaceDescriptor =
    private
        { Id: WorkspaceId
          TargetPath: WorkspaceTargetPath
          Format: WorkspaceFormat
          Revision: WorkspaceRevision
          Access: WorkspaceAccess }

    member this.WorkspaceId = this.Id
    member this.Path = this.TargetPath
    member this.WorkspaceFormat = this.Format
    member this.WorkspaceRevision = this.Revision
    member this.WorkspaceAccess = this.Access
    member this.IsReadOnly = this.Access = WorkspaceAccess.ReadOnly

    static member Create
        (
            targetPath: WorkspaceTargetPath,
            caseSemantics: HostFileSystemCaseSemantics,
            format: WorkspaceFormat,
            revision: WorkspaceRevision,
            access: WorkspaceAccess
        ) =
        if isNull (box targetPath) then
            nullArg (nameof targetPath)

        if isNull (box revision) then
            nullArg (nameof revision)

        { Id = WorkspaceId.Create(targetPath, caseSemantics)
          TargetPath = targetPath
          Format = format
          Revision = revision
          Access =
            if format = WorkspaceFormat.Slnf then
                WorkspaceAccess.ReadOnly
            else
                access }

[<AbstractClass; Sealed>]
type WorkspaceNodeCapabilities private () =
    static let readOnly = ImmutableArray.Create WorkspaceCapabilityId.Read

    static let readWrite =
        ImmutableArray.Create(WorkspaceCapabilityId.Read, WorkspaceCapabilityId.Write)

    static member For
        (
            workspace: WorkspaceDescriptor,
            kind: WorkspaceNodeKind,
            capabilityProfile: WorkspaceCapabilityProfile
        ) =
        if isNull (box workspace) then
            nullArg (nameof workspace)

        if
            workspace.IsReadOnly
            || kind = WorkspaceNodeKind.Placeholder
            || capabilityProfile <> WorkspaceCapabilityProfile.Full
        then
            readOnly
        else
            readWrite

type WorkspaceNode =
    private
        { Id: NodeId
          Kind: WorkspaceNodeKind
          SemanticIdentity: NodeSemanticIdentity
          DisplayName: string
          CapabilityProfile: WorkspaceCapabilityProfile
          LoadState: WorkspaceNodeLoadState
          Capabilities: ImmutableArray<WorkspaceCapabilityId> }

    member this.NodeId = this.Id
    member this.NodeKind = this.Kind
    member this.Identity = this.SemanticIdentity
    member this.Name = this.DisplayName
    member this.Profile = this.CapabilityProfile
    member this.NodeLoadState = this.LoadState
    member this.AvailableCapabilities = this.Capabilities
    member this.Supports(capability: WorkspaceCapabilityId) = this.Capabilities.Contains capability

    static member private CreateCore
        (
            workspace: WorkspaceDescriptor,
            kind: WorkspaceNodeKind,
            semanticIdentity: NodeSemanticIdentity,
            displayName: string,
            capabilityProfile: WorkspaceCapabilityProfile,
            loadState: WorkspaceNodeLoadState
        ) =
        if isNull (box workspace) then
            nullArg (nameof workspace)

        if isNull (box semanticIdentity) then
            nullArg (nameof semanticIdentity)

        displayName |> Validation.nonEmpty (nameof displayName) |> ignore

        { Id = NodeId.Create(workspace.Id, kind, semanticIdentity)
          Kind = kind
          SemanticIdentity = semanticIdentity
          DisplayName = displayName
          CapabilityProfile = capabilityProfile
          LoadState = loadState
          Capabilities = WorkspaceNodeCapabilities.For(workspace, kind, capabilityProfile) }

    static member Create
        (
            workspace: WorkspaceDescriptor,
            kind: WorkspaceNodeKind,
            semanticIdentity: NodeSemanticIdentity,
            displayName: string,
            capabilityProfile: WorkspaceCapabilityProfile
        ) =
        WorkspaceNode.CreateCore(
            workspace,
            kind,
            semanticIdentity,
            displayName,
            capabilityProfile,
            WorkspaceNodeLoadState.Hydrated
        )

    static member CreateWithLoadState
        (
            workspace: WorkspaceDescriptor,
            kind: WorkspaceNodeKind,
            semanticIdentity: NodeSemanticIdentity,
            displayName: string,
            capabilityProfile: WorkspaceCapabilityProfile,
            loadState: WorkspaceNodeLoadState
        ) =
        WorkspaceNode.CreateCore(
            workspace,
            kind,
            semanticIdentity,
            displayName,
            capabilityProfile,
            loadState
        )

type NodeReplacement = { OldId: NodeId; NewId: NodeId }

type ContinuationToken private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> ContinuationToken

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? ContinuationToken as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        System.StringComparer.Ordinal.GetHashCode value

type WorkspaceRoot =
    { Revision: WorkspaceRevision
      Nodes: ImmutableArray<WorkspaceNode> }

type WorkspaceNodePage =
    { Revision: WorkspaceRevision
      ParentId: NodeId
      Nodes: ImmutableArray<WorkspaceNode>
      NextToken: ContinuationToken option }

type WorkspaceExport =
    { Revision: WorkspaceRevision
      Nodes: ImmutableArray<WorkspaceNode> }

type WorkspaceChange =
    | Added of node: WorkspaceNode * parentId: NodeId option * index: int
    | Removed of nodeId: NodeId * parentId: NodeId option * index: int
    | Updated of node: WorkspaceNode * parentId: NodeId option * index: int
    | Moved of
        nodeId: NodeId *
        oldParentId: NodeId option *
        oldIndex: int *
        newParentId: NodeId option *
        newIndex: int
    | Replaced of oldNodeId: NodeId * newNode: WorkspaceNode * parentId: NodeId option * index: int

type WorkspaceDelta =
    { WorkspaceId: WorkspaceId
      BaseRevision: WorkspaceRevision
      NewRevision: WorkspaceRevision
      Changes: ImmutableArray<WorkspaceChange>
      Diagnostics: ImmutableArray<WorkspaceDiagnostic> }

type WorkspaceReset =
    { WorkspaceId: WorkspaceId
      Revision: WorkspaceRevision
      Diagnostics: ImmutableArray<WorkspaceDiagnostic> }
