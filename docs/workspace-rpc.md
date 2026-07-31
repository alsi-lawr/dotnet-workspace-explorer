# `dotnet-workspace-explorer/workspace` v1.0

Start the public endpoint with `dotnet-workspace-explorer workspace <target> --pipe`. Export evaluation
defaults to three workers; a client may select a process-local positive bound with
`--pipe --export-workers <positive-integer>`. This startup option does not add an initialization or
RPC field. The stream is a sequence of framed MessagePack-RPC values. A client sends `initialize`
first; it supplies protocol version `{ major: 1, minor: ... }`, non-empty `clientInfo.name`,
capabilities, and optional limits. Only major version 1 is supported. The initialization result
reports protocol version 1.0, server and workspace descriptors, negotiated capabilities, and
limits.

## Requests

The v1.0 allowlist is exactly:

1. `initialize`
2. `workspace/root`
3. `workspace/children`
4. `workspace/file/resolve`
5. `workspace/git/status`
6. `workspace/export/start`
7. `workspace/refresh`
8. `workspace/create/options`
9. `workspace/commands/list`
10. `workspace/commands/describe`
11. `workspace/commands/preview`
12. `workspace/commands/execute`
13. `workspace/operations/cancel`
14. `shutdown`

`workspace/root` returns exactly one `workspace` node named from the opened workspace.
`workspace/children` pages a parent by node ID, page size, and continuation token. The semantic
hierarchy uses `solutionFolder`, `solutionItem`, and `project` beneath that root. Hydrated projects
contain `projectFolder`, `projectFile`, `dependencyContainer`, `dependency`, and
`dependencyProperty` nodes. Dependency properties are compact read-only reference details such as
type, package or assembly version, assembly identity, runtime version, resolved path, and available
MSBuild reference flags. Missing values are omitted. Evaluated project properties, configurations,
platforms, and arbitrary MSBuild items are not navigation rows.
`workspace/export/start` emits the same semantic node ID/kind set as a flat stream without parent or
index metadata. `workspace/refresh` compares or refreshes a revision. `workspace/delta` and reset
notifications let clients reconcile change instead of assuming a cached tree remains current.

Each public node carries its workspace ID and revision, plus `id`, `kind`, `name`, `loadState`, and
`capabilities`. Capabilities determine which commands a node may expose. The node shape has no
general properties field; dependency property values use read-only node names and may include a
core-resolved local path. Other ordinary nodes remain path-free. Property mutation is exposed by
descriptors such as `project.property.set`. Filtered and `.slnf` workspaces can contain
read-only/excluded views. Clients must use revisions and must tolerate paging, export chunks, and
reset notifications.

`workspace/file/resolve` accepts exactly `{ targetNodeId, expectedRevision }`. At the current
revision, project nodes resolve to their exact `.csproj`, `.fsproj`, or `.vbproj` path, and project
files and solution items resolve to their exact existing file. Its exact result is
`{ revision, targetNodeId, path }`. Other node kinds are not openable and stale revisions conflict.

`workspace/commands/list` discovers descriptors, `workspace/commands/describe` obtains one descriptor,
and `workspace/commands/preview` creates a revision-bound plan. A mutating or destructive
`workspace/commands/execute` requires that confirmation token and expected revision. Clients must
handle conflicts rather than retrying against an unknown workspace state.
For workspace commands, omitting `targetNodeId`, supplying the initialized workspace descriptor ID,
or supplying the single root node ID selects the same workspace target, except that
`workspace.rename`, `workspace.move`, and `workspace.copy` require `targetNodeId` for describe,
preview, and execute.
`workspace/operations/cancel` requests cancellation for an operation ID; it does not promise that
already-completed work can be undone. `shutdown` accepts an orderly session shutdown.

## Contextual New and Delete

Clients that negotiate `workspace.create.options` may send `workspace/create/options` with exactly
`{ targetNodeId, expectedRevision }`. The response is exactly `{ revision, options }`. Each option
contains `selectionId`, `kind`, `displayName`, `description`, optional `language`, and `execution`.
The fixed kinds are `empty`, `itemTemplate`, and `projectTemplate`; execution is `transaction` or
`operation`. Selection IDs are opaque, catalog-bound values and must not be cached across catalog
changes.

The `workspace.create` command accepts exactly text arguments `selectionId` and `name`.
`workspace.delete` accepts no arguments. Both use the ordinary command list, describe, preview, and
execute methods with a semantic `targetNodeId`. New targets the nearest physical project directory.
Project templates always create `<solution-root>/<name>` and join the nearest logical solution
folder. Delete removes or trashes according to the selected node: project files and physical
folders update project membership and use native trash, projects and logical solution folders
change solution membership, and solution-item deletion changes membership and uses native trash.

Project contexts offer an empty file, matching or language-neutral item templates, and installed
project templates. Workspace roots and solution folders/items offer installed project templates.
Dependencies and dependency properties inherit their nearest project for New but cannot be
deleted. Filtered placeholders and non-navigation rows reject. `.slnf` workspaces may read options
but cannot preview or execute these write commands.

Every command preview contains exactly `confirmationToken`, `expiresAtUtc`, `summary`, and
`effects`. Each effect contains only `operation`, `target`, and boolean `recursive`. Operations are
`create`, `modify`, `trash`, `addToProject`, `removeFromProject`, `addToSolution`, or
`removeFromSolution`. Empty creation and deletion complete synchronously. Item and project
templates return `{ operationId, revision }`; `workspace/operations/completed` is their definitive
outcome.

## Contextual Rename, Move, and Copy

`workspace.rename` is a write command for one project, project file, physical project directory,
solution item, or logical solution folder. Its generic `targetNodeId` is the source and its
arguments are exactly `{ name }`, where `name` is text and one valid path segment.

`workspace.move` and `workspace.copy` use the generic `targetNodeId` as the selected destination.
Their arguments are exactly `{ sourceNodeIds }`; the public descriptor type of `sourceNodeIds` is
`nodeIdArray`. Duplicate and parent-child selections normalize to one effective source. Move
supports applicable physical project files/directories and logical solution projects, items, and
folders. Copy supports only physical project files and directories.

All sources and the destination resolve against the requested workspace revision. A preview rejects
unsupported members, cycles, overlaps, invalid names, and existing or duplicate destinations before
issuing a token. Physical moves can cross projects and compose both project memberships. Logical
moves compose one solution document. A batch is one no-overwrite transaction: execution either
applies every filesystem and membership action or compensates actions already applied. Relocated
path-derived nodes may receive new semantic IDs; unrelated IDs remain stable.

Describe uses exactly `{ commandId, targetNodeId }`. Preview uses exactly
`{ commandId, targetNodeId, arguments, expectedRevision }`. Execute is an exact deep copy of that
preview request with only `confirmationToken` added. Synchronous execution returns exactly
`{ applied: true, revision }`.

## Git status

A client must negotiate `workspace.git.status` before calling `workspace/git/status`. The request is
exactly `{ expectedRevision }`; absent negotiation returns `unsupported_capability`, and a stale
workspace revision returns `workspace_conflict`. The exact result is:

```text
{
  available,
  workspaceRevision,
  statusRevision,
  decorations: [{ nodeId, state }]
}
```

`state` is only `added` or `changed`. Added and untracked paths use `added`; all other working-tree
changes use `changed`, with `added` taking precedence. Decorations are unique, deterministic, and
aggregate to visible semantic ancestors. `statusRevision` is monotonic within one live session. It
advances only when normalized availability or decorations change and is reused for an identical
snapshot.

The server inspects only the active solution's containing Git worktree through bounded
`git status --porcelain=v1 -z`. A target outside Git returns `available=false` and no decorations.
Git launch, output-bound, parse, and mapping failures are structured errors. Status inspection never
changes workspace revision, emits a workspace delta/reset, mutates files, scans nested
repositories, or starts a poller.

## Notifications

The v1.0 notification allowlist is exactly:

1. `workspace/delta`
2. `workspace/reset`
3. `workspace/export/chunk`
4. `workspace/operations/progress`
5. `workspace/operations/output`
6. `workspace/operations/completed`

Clients should treat notifications as progress and workspace synchronization data, not as a test or lifecycle event stream. In particular, there are no specialized `test/update` or `test/attachment` notifications.

## Lifecycle commands

`dotnet.restore`, `dotnet.build`, `dotnet.run`, and `dotnet.test` are command descriptors obtained through `workspace/commands/list`; they are not RPC methods and do not create specialized lifecycle or test events. When executed, each selects its target and invokes one ordinary `dotnet` child. The SDK owns execution semantics and output.

## Compatibility and limits

Responses match request IDs and carry either a nil error or a structured error with code and safe message. The transport accepts only the profile allowlist after initialization. Requested frame and page limits are negotiated within server limits; clients must respect the returned values. The profile does not promise durable operation recovery, a general schema language, or an alternate test explorer.
