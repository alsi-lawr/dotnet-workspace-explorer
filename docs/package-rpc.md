# `dotnet-workspace-explorer/packages` v2.0

The package service is a backend for independent clients. Start it with exactly:

```console
dotnet we packages <TARGET> --pipe
```

`<TARGET>` is required. It may be a solution, solution filter, C#, F#, or Visual Basic project, or
a directory whose projects should be included. The process reads MessagePack-RPC values from
standard input and writes responses and notifications to standard output. Consecutive frames are
consecutive MessagePack values; they do not have a separate length prefix.

This is not the workspace service. `dotnet we workspace <TARGET> --pipe` uses a different profile
and method set. The package command does not open a terminal interface. The Visual Studio-style
terminal client is the separate
[`dotnet-package-explorer`](https://github.com/alsi-lawr/dotnet-package-explorer) project.

## Contract files

The package contains these client-facing files:

- `docs/package-rpc.md`: this guide.
- `protocol/package-v2.schema.json`: the complete v2 field and value contract.
- `protocol/package-v2/golden/*.mpack`: example request, response, notification, and error frames.

The schema and golden frames are authoritative when this guide omits a field.

## Session

A client sends `initialize` first. It supplies protocol version 2.0, a non-empty client name, the
capabilities it wants, and its frame and page limits. The server returns:

- the negotiated protocol version;
- server and target details;
- the capabilities shared by the client and server;
- the negotiated limits.

Only major version 2 is supported. Version 1 discovery clients are rejected; there is no discovery
fallback or dual stack. A client may then use only methods covered by a negotiated capability.
Send `shutdown` for an orderly exit.

Request frames use `[0, id, method, params]`. Responses use `[1, id, error, result]`.
Notifications use `[2, method, params]`.

## Capabilities

The capabilities are:

| Capability | Purpose |
| --- | --- |
| `packages.sources.v1` | List configured package sources. |
| `packages.source-mapping.v1` | Inspect source-mapping policy for a package. |
| `packages.search.v2` | Stream bounded package search batches. |
| `packages.details.v1` | Read versions, dependencies, license, and safety metadata. |
| `packages.readme.v1` | Include package README CommonMark in details when available. |
| `packages.installed.v2` | Stream direct, central, transitive, and framework package inventory. |
| `packages.restore.v2` | Explicitly restore and stream fresh installed state. |
| `packages.updates.v2` | Stream available updates. |
| `packages.consolidation.v2` | Stream packages whose versions can be consolidated. |
| `packages.preview.v1` | Preview one install, update, remove, or consolidate operation. |
| `packages.batch-preview.v1` | Preview updates across more than one package or target. |
| `packages.execute.v1` | Apply a confirmed single-operation preview. |
| `packages.batch-execute.v1` | Apply a confirmed batch preview. |
| `packages.cancel.v1` | Request cancellation by request or operation identity. |
| `packages.partial-recovery.v1` | Receive per-target recovery details after a partial failure. |

## Methods

The request methods are:

1. `initialize`
2. `package/sources`
3. `package/sourceMapping`
4. `package/search/start`
5. `package/details`
6. `package/installed/start`
7. `package/installed/restore/start`
8. `package/updates/start`
9. `package/consolidation/start`
10. `package/preview`
11. `package/previewBatch`
12. `package/execute/start`
13. `package/executeBatch/start`
14. `package/cancel`
15. `shutdown`

Every discovery start responds with `{ accepted: true, requestId }` before background work can
publish output. Discovery then sends zero or more method-specific batches followed by exactly one
terminal. A batch carries the request identity and a zero-based consecutive `sequence`. A terminal
contains no rows: it reports `state`, `batchCount`, and `itemCount`; it omits `lastSequence` for an
empty stream and otherwise reports `lastSequence = batchCount - 1`.

## Limits and paging

The server accepts MessagePack values up to 16 MiB and nesting up to 64 levels. Initialization may
negotiate a smaller outbound frame limit, but not one below 1,024 bytes. The largest page size is
200, and initialization may negotiate a smaller value.

The negotiated page size is the maximum item count of each discovery batch. Every batch also fits
the negotiated frame limit. Output is awaited, so the writer provides producer backpressure rather
than an unbounded notification queue. Search alone retains its opaque continuation: the client sends
it unchanged on the next Search start and receives the next value in the metadata-only terminal.
An individually unencodable row produces a bounded failed terminal with `response_too_large`; the
session remains usable.

At most one Installed, Search, Updates, and Consolidation stream is active per session. Inventory
and restore share the Installed slot. A second start of the same kind is not queued; it receives the
retryable `discovery_in_progress` response.

## Installed state and restore

`package/installed/start` streams the best inventory already available through
`package/installed/batch` and `package/installed/completed`. It never starts restore work.

After the inventory terminal, a client that negotiated restore immediately sends
`package/installed/restore/start` with a new request identity. That ordinary request receives its
own accepted response before `package/installed/restore/batch` and
`package/installed/restore/completed`. Inventory and fresh restore rows never share an identity. A
failed or cancelled restore leaves the promoted inventory available to the client.

Search uses `package/search/batch` and `package/search/completed`; successful source failures are
data in a batch and do not turn the terminal into failure. Updates use `package/updates/batch` and
`package/updates/completed`. Consolidation uses `package/consolidation/batch` and
`package/consolidation/completed`. Search duplicates are legitimate rows and remain duplicated.

## Details and README

`package/details` returns the selected package summary, versions, authors, dependency groups,
deprecation, vulnerabilities, and available license and project links. When
`packages.readme.v1` is negotiated, `readmeCommonMark` contains the package README when the
configured source provides one safely. Missing README content does not remove the remaining
details.

## Preview and confirmation

Changes always start with `package/preview` or `package/previewBatch`. A preview records:

- the requested operation and every target change;
- current and proposed package ownership;
- package metadata, source mapping, and restore impact;
- owner files and their fingerprints;
- the workspace revision;
- a confirmation token.

The confirmation token identifies that exact preview. The token is not a general permission to
change the workspace. `package/execute/start` accepts a token from a single preview, and
`package/executeBatch/start` accepts a token from a batch preview. Execution rejects an expired,
unknown, wrong-kind, or stale token before changing files.

## Progress, cancellation, and recovery

Execution first returns an accepted response with a request identity. It then publishes
`package/operations/progress` with an operation identity and one of these stages: `preparing`,
`applying`, `restoring`, `refreshing`, or `completed`.

`package/cancel` accepts exactly one request identity or operation identity. Cancellation is
cooperative. Work that has already completed is not undone merely because cancellation was
requested.

`package/operations/completed` is the final execution result. A successful result lists each
target, changed files, and restore outcome. If compensation cannot return every target to a known
state, `DWE-PACKAGE-PARTIAL-RECOVERY` includes per-target entries marked `completed`,
`compensated`, `unchanged`, or `uncertain`. Clients should show uncertain entries and leave the
affected files available for user review.

## Stable errors

Every RPC error contains `code` and `message`; package errors may also contain bounded recovery and
retry data. Clients should branch on `code`, not message text.

| Code | Meaning |
| --- | --- |
| `invalid_request` | The MessagePack-RPC request shape is invalid. |
| `invalid_params` | Request parameters or their bounded values are invalid. |
| `not_initialized` | A method was called before `initialize`. |
| `unknown_method` | The method is not part of this profile. |
| `unsupported_capability` | The client did not negotiate the required capability. |
| `discovery_in_progress` | A stream of the same discovery kind is already active; retry later. |
| `response_too_large` | A response exceeds the negotiated outbound frame limit. |
| `internal_error` | The RPC session could not complete a request safely. |
| `DWE-PACKAGE-INVALID-REQUEST` | The package request is invalid. |
| `DWE-PACKAGE-NOT-FOUND` | The selected package resource was not found. |
| `DWE-PACKAGE-AMBIGUOUS-TARGET` | More than one package target matches. |
| `DWE-PACKAGE-UNSUPPORTED` | The requested package operation is unsupported. |
| `DWE-PACKAGE-AUTHENTICATION-REQUIRED` | A configured source needs credentials. |
| `DWE-PACKAGE-UNAUTHORIZED` | A configured source rejected the request. |
| `DWE-PACKAGE-SOURCE-AUTHENTICATION-REQUIRED` | A source read needs credentials. |
| `DWE-PACKAGE-SOURCE-UNAUTHORIZED` | A source rejected a read. |
| `DWE-PACKAGE-SOURCE-MALFORMED` | A source returned invalid package data. |
| `DWE-PACKAGE-SOURCE-UNAVAILABLE` | A configured source is unavailable. |
| `DWE-PACKAGE-STALE-STATE` | Files or workspace state changed after preview. |
| `DWE-PACKAGE-CANCELLED` | Package work was cancelled. |
| `DWE-PACKAGE-EXTERNAL-TOOL-FAILED` | The stock `dotnet package` command failed. |
| `DWE-PACKAGE-PARTIAL-RECOVERY` | Some targets need user review after compensation. |
| `DWE-PACKAGE-INTERNAL` | The package request could not complete safely. |

Source authentication errors never return configured credentials or dependency exception text.
