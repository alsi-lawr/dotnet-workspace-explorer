namespace Dotnet.CLI.Plus.Core

open System.Collections.Immutable

type WorkspaceCapabilityProfile =
    | Full = 0
    | ReadOnly = 1
    | UnknownProjectSystem = 2

type WorkspaceCapabilityId private (value: string) =
    static let read = WorkspaceCapabilityId "workspace.read"
    static let write = WorkspaceCapabilityId "workspace.write"
    member _.Value = value
    static member Read = read
    static member Write = write
    override _.ToString() = value

    override _.Equals(other) =
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
    static let readOnly = ImmutableArray.Create(WorkspaceCapabilityId.Read)

    static let readWrite =
        ImmutableArray.Create(WorkspaceCapabilityId.Read, WorkspaceCapabilityId.Write)

    static member For
        (workspace: WorkspaceDescriptor, kind: WorkspaceNodeKind, capabilityProfile: WorkspaceCapabilityProfile)
        =
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
          Capabilities: ImmutableArray<WorkspaceCapabilityId> }

    member this.NodeId = this.Id
    member this.NodeKind = this.Kind
    member this.Identity = this.SemanticIdentity
    member this.Name = this.DisplayName
    member this.Profile = this.CapabilityProfile
    member this.AvailableCapabilities = this.Capabilities
    member this.Supports(capability: WorkspaceCapabilityId) = this.Capabilities.Contains capability

    static member Create
        (
            workspace: WorkspaceDescriptor,
            kind: WorkspaceNodeKind,
            semanticIdentity: NodeSemanticIdentity,
            displayName: string,
            capabilityProfile: WorkspaceCapabilityProfile
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
          Capabilities = WorkspaceNodeCapabilities.For(workspace, kind, capabilityProfile) }

type NodeReplacement = { OldId: NodeId; NewId: NodeId }

type ContinuationToken private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> ContinuationToken

    override _.ToString() = value

    override _.Equals(other) =
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
