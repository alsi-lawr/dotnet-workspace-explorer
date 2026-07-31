namespace Dotnet.WorkspaceExplorer.Workspaces

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
        { WorkspaceIdValue: WorkspaceId
          WorkspacePathValue: WorkspacePath
          WorkspaceFormatValue: WorkspaceFormat
          WorkspaceRevisionValue: WorkspaceRevision
          WorkspaceAccessValue: WorkspaceAccess }

    member this.Id = this.WorkspaceIdValue
    member this.Path = this.WorkspacePathValue
    member this.Format = this.WorkspaceFormatValue
    member this.Revision = this.WorkspaceRevisionValue
    member this.Access = this.WorkspaceAccessValue
    member this.IsReadOnly = this.WorkspaceAccessValue = WorkspaceAccess.ReadOnly

    static member Create
        (
            targetPath: WorkspacePath,
            caseSemantics: FileSystemCaseSensitivity,
            format: WorkspaceFormat,
            revision: WorkspaceRevision,
            access: WorkspaceAccess
        ) =
        if isNull (box targetPath) then
            nullArg (nameof targetPath)

        if isNull (box revision) then
            nullArg (nameof revision)

        { WorkspaceIdValue = WorkspaceId.Create(targetPath, caseSemantics)
          WorkspacePathValue = targetPath
          WorkspaceFormatValue = format
          WorkspaceRevisionValue = revision
          WorkspaceAccessValue =
            if format = WorkspaceFormat.Slnf then
                WorkspaceAccess.ReadOnly
            else
                access }

[<AbstractClass; Sealed>]
type WorkspaceNodeCapabilityPolicy private () =
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
            || kind = WorkspaceNodeKind.DependencyContainer
            || kind = WorkspaceNodeKind.Dependency
            || kind = WorkspaceNodeKind.DependencyProperty
            || capabilityProfile <> WorkspaceCapabilityProfile.Full
        then
            readOnly
        else
            readWrite

type WorkspaceNode =
    private
        { NodeIdValue: WorkspaceNodeId
          NodeKindValue: WorkspaceNodeKind
          NodeIdentityValue: WorkspaceNodeIdentity
          NodeNameValue: string
          CapabilityProfileValue: WorkspaceCapabilityProfile
          LoadStateValue: WorkspaceNodeLoadState
          CapabilitiesValue: ImmutableArray<WorkspaceCapabilityId> }

    member this.Id = this.NodeIdValue
    member this.Kind = this.NodeKindValue
    member this.Identity = this.NodeIdentityValue
    member this.Name = this.NodeNameValue
    member this.CapabilityProfile = this.CapabilityProfileValue
    member this.LoadState = this.LoadStateValue
    member this.Capabilities = this.CapabilitiesValue

    member this.Supports(capability: WorkspaceCapabilityId) =
        this.CapabilitiesValue.Contains capability

    static member private CreateCore
        (
            workspace: WorkspaceDescriptor,
            kind: WorkspaceNodeKind,
            semanticIdentity: WorkspaceNodeIdentity,
            displayName: string,
            capabilityProfile: WorkspaceCapabilityProfile,
            loadState: WorkspaceNodeLoadState
        ) =
        if isNull (box workspace) then
            nullArg (nameof workspace)

        if isNull (box semanticIdentity) then
            nullArg (nameof semanticIdentity)

        displayName |> WorkspaceValue.nonEmpty (nameof displayName) |> ignore

        { NodeIdValue = WorkspaceNodeId.Create(workspace.Id, kind, semanticIdentity)
          NodeKindValue = kind
          NodeIdentityValue = semanticIdentity
          NodeNameValue = displayName
          CapabilityProfileValue = capabilityProfile
          LoadStateValue = loadState
          CapabilitiesValue = WorkspaceNodeCapabilityPolicy.For(workspace, kind, capabilityProfile) }

    static member Create
        (
            workspace: WorkspaceDescriptor,
            kind: WorkspaceNodeKind,
            semanticIdentity: WorkspaceNodeIdentity,
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
            semanticIdentity: WorkspaceNodeIdentity,
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
