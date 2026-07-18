namespace Dotnet.CLI.Plus.Core

open System
open System.Collections.Immutable

/// An affirmative permission for a destructive or externally-scoped mutation.
type MutationIntent =
    | PermanentDelete = 0
    | RecursiveDelete = 1
    | Overwrite = 2
    | RemoveIncomingReferences = 3
    | AccessExternalPath = 4

/// An opaque value returned by mutation preparation and consumed once by execution.
type MutationConfirmationToken private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> MutationConfirmationToken

    override _.ToString() = value

/// Transport-neutral input used to bind a mutation preview to its subsequent execution.
type MutationPreviewRequest =
    { CommandId: CommandId
      Targets: ImmutableArray<WorkspaceArtifactPath>
      Arguments: CommandArguments
      ExpectedRevision: WorkspaceRevision
      Intents: ImmutableHashSet<MutationIntent>
      AuthorizedRoots: ImmutableArray<WorkspaceArtifactPath> }

/// The complete, non-secret preview returned to a caller. The token itself is opaque.
type MutationPreview =
    { Token: MutationConfirmationToken
      ExpiresAtUtc: DateTimeOffset
      ExpectedRevision: WorkspaceRevision }

[<RequireQualifiedAccess>]
type MutationRecoveryDisposition =
    | Ready
    | PartialRecoveryRequired
