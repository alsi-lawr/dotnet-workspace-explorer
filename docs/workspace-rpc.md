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
4. `workspace/export/start`
5. `workspace/refresh`
6. `workspace/create/options`
7. `workspace/commands/list`
8. `workspace/commands/describe`
9. `workspace/commands/preview`
10. `workspace/commands/execute`
11. `workspace/operations/cancel`
12. `shutdown`

`workspace/root` returns exactly one `workspace` node named from the opened workspace.
`workspace/children` pages a parent by node ID, page size, and continuation token. The semantic
hierarchy uses `solutionFolder`, `solutionItem`, and `project` beneath that root. Hydrated projects
contain `projectFolder`, `projectFile`, `dependencyContainer`, and `dependency` nodes; evaluated
properties, configurations, platforms, and arbitrary MSBuild items are not navigation rows.
`workspace/export/start` emits the same semantic node ID/kind set as a flat stream without parent or
index metadata. `workspace/refresh` compares or refreshes a revision. `workspace/delta` and reset
notifications let clients reconcile change instead of assuming a cached tree remains current.

Each public node carries its workspace ID and revision, plus `id`, `kind`, `name`, `loadState`, and `capabilities`. Capabilities determine which commands a node may expose. The node shape has no general properties field; property mutation is exposed by descriptors such as `project.property.set`. Filtered and `.slnf` workspaces can contain read-only/excluded views. Clients must use revisions and must tolerate paging, export chunks, and reset notifications.

`workspace/commands/list` discovers descriptors, `workspace/commands/describe` obtains one descriptor,
and `workspace/commands/preview` creates a revision-bound plan. A mutating or destructive
`workspace/commands/execute` requires that confirmation token and expected revision. Clients must
handle conflicts rather than retrying against an unknown workspace state.
For workspace commands, omitting `targetNodeId`, supplying the initialized workspace descriptor ID,
or supplying the single root node ID selects the same workspace target.
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
Dependencies inherit their nearest project for New but cannot be deleted. Filtered placeholders
and non-navigation rows reject. `.slnf` workspaces may read options but cannot preview or execute
these write commands.

Every command preview contains exactly `confirmationToken`, `expiresAtUtc`, `summary`, and
`effects`. Each effect contains only `operation`, `target`, and boolean `recursive`. Operations are
`create`, `modify`, `trash`, `addToProject`, `removeFromProject`, `addToSolution`, or
`removeFromSolution`. Empty creation and deletion complete synchronously. Item and project
templates return `{ operationId, revision }`; `workspace/operations/completed` is their definitive
outcome.

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
