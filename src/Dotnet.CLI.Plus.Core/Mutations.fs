namespace Dotnet.CLI.Plus.Core

open System
open System.Collections.Immutable

type MutationIntent =
    | PermanentDelete = 0
    | RecursiveDelete = 1
    | Overwrite = 2
    | RemoveIncomingReferences = 3
    | AccessExternalPath = 4

type MutationConfirmationToken private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> MutationConfirmationToken

    override _.ToString() = value

type MutationPreviewRequest =
    { CommandId: CommandId
      Targets: ImmutableArray<WorkspaceArtifactPath>
      Arguments: CommandArguments
      ExpectedRevision: WorkspaceRevision
      Intents: ImmutableHashSet<MutationIntent>
      AuthorizedRoots: ImmutableArray<WorkspaceArtifactPath> }

type MutationPreview =
    { Confirmation: MutationConfirmationToken
      ExpiresAtUtc: DateTimeOffset }

type MutationApplyResult =
    | Applied
    | RolledBack of WorkspaceFailure
