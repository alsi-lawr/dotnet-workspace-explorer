namespace Dotnet.WorkspaceExplorer.Workspaces

open System
open System.Collections.Immutable

type WorkspaceEditIntent =
    | PermanentDelete = 0
    | RecursiveDelete = 1
    | Overwrite = 2
    | RemoveIncomingReferences = 3
    | AccessExternalPath = 4

type WorkspaceEditConfirmation private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> WorkspaceValue.nonEmpty (nameof value) |> WorkspaceEditConfirmation

    override _.ToString() = value

type WorkspaceEditPreviewRequest =
    { CommandId: CommandId
      Targets: ImmutableArray<WorkspaceArtifactPath>
      Arguments: CommandArguments
      ExpectedRevision: WorkspaceRevision
      Intents: ImmutableHashSet<WorkspaceEditIntent>
      AuthorizedRoots: ImmutableArray<WorkspaceArtifactPath> }

type WorkspaceEditPreview =
    { Confirmation: WorkspaceEditConfirmation
      ExpiresAtUtc: DateTimeOffset }

type WorkspaceEditResult =
    | Applied
    | RolledBack of WorkspaceFailure
