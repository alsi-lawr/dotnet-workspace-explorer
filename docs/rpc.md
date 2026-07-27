# `dotnet-cli-plus/workspace` v1.0

Start the public endpoint with `dotnet-plus solution <target> --pipe` (or `sln`). The stream is a sequence of framed MessagePack-RPC values. A client sends `initialize` first; it supplies protocol version `{ major: 1, minor: ... }`, non-empty `clientInfo.name`, capabilities, and optional limits. Only major version 1 is supported. The initialization result reports protocol version 1.0, server and workspace descriptors, negotiated capabilities, and limits.

## Requests

The v1.0 allowlist is exactly:

1. `initialize`
2. `workspace/root`
3. `workspace/children`
4. `workspace/export`
5. `workspace/refresh`
6. `command/list`
7. `command/describe`
8. `command/preview`
9. `command/execute`
10. `operation/cancel`
11. `shutdown`

`workspace/root` returns the current revision and root nodes. `workspace/children` pages a parent by node ID, page size, and continuation token. `workspace/export` starts an export operation; `workspace/refresh` compares or refreshes a revision. `workspace/delta` and reset notifications let clients reconcile change instead of assuming a cached tree remains current.

Each public node carries its workspace ID and revision, plus `id`, `kind`, `name`, `loadState`, and `capabilities`. Capabilities determine which commands a node may expose. The node shape has no general properties field; property mutation is exposed by descriptors such as `project.property.set`. Filtered and `.slnf` workspaces can contain read-only/excluded views. Clients must use revisions and must tolerate paging, export chunks, and reset notifications.

`command/list` discovers descriptors, `command/describe` obtains one descriptor, and `command/preview` creates a revision-bound plan. A mutating or destructive `command/execute` requires that preview ID and expected revision. Clients must handle conflicts rather than retrying against an unknown workspace state. `operation/cancel` requests cancellation for an operation ID; it does not promise that already-completed work can be undone. `shutdown` accepts an orderly session shutdown.

## Notifications

The v1.0 notification allowlist is exactly:

1. `workspace/delta`
2. `workspace/reset`
3. `workspace/exportChunk`
4. `operation/progress`
5. `operation/output`
6. `operation/completed`

Clients should treat notifications as progress and workspace synchronization data, not as a test or lifecycle event stream. In particular, there are no specialized `test/update` or `test/attachment` notifications.

## Lifecycle commands

`lifecycle.restore`, `lifecycle.build`, `lifecycle.run`, and `lifecycle.test` are command descriptors obtained through `command/list`; they are not RPC methods and do not create specialized lifecycle or test events. When executed, each selects its target and invokes one ordinary `dotnet` child. The SDK owns execution semantics and output.

## Compatibility and limits

Responses match request IDs and carry either a nil error or a structured error with code and safe message. The transport accepts only the profile allowlist after initialization. Requested frame and page limits are negotiated within server limits; clients must respect the returned values. The profile does not promise durable operation recovery, a general schema language, or an alternate test explorer.
