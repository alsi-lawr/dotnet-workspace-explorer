# `dotnet-workspace-explorer/packages` v1.0

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
- `protocol/package-v1.schema.json`: the complete v1 field and value contract.
- `protocol/package-v1/golden/*.mpack`: example request, response, notification, and error frames.

The schema and golden frames are authoritative when this guide omits a field.

## Session

A client sends `initialize` first. It supplies protocol version 1.x, a non-empty client name, the
capabilities it wants, and its frame and page limits. The server returns:

- the negotiated protocol version;
- server and target details;
- the capabilities shared by the client and server;
- the negotiated limits.

Only major version 1 is supported. A client may then use only methods covered by a negotiated
capability. Send `shutdown` for an orderly exit.

Request frames use `[0, id, method, params]`. Responses use `[1, id, error, result]`.
Notifications use `[2, method, params]`.

## Capabilities

The v1 capabilities are:

| Capability | Purpose |
| --- | --- |
| `packages.sources.v1` | List configured package sources. |
| `packages.source-mapping.v1` | Inspect source-mapping policy for a package. |
| `packages.search.v1` | Search a source with bounded pages. |
| `packages.details.v1` | Read versions, dependencies, license, and safety metadata. |
| `packages.readme.v1` | Include package README CommonMark in details when available. |
| `packages.installed.v1` | Read direct, central, transitive, and framework package state. |
| `packages.restore.v1` | Refresh installed state in the background. |
| `packages.updates.v1` | Find available updates in the background. |
| `packages.consolidation.v1` | Find packages with versions that can be consolidated. |
| `packages.preview.v1` | Preview one install, update, remove, or consolidate operation. |
| `packages.batch-preview.v1` | Preview updates across more than one package or target. |
| `packages.execute.v1` | Apply a confirmed single-operation preview. |
| `packages.batch-execute.v1` | Apply a confirmed batch preview. |
| `packages.cancel.v1` | Request cancellation by request or operation identity. |
| `packages.partial-recovery.v1` | Receive per-target recovery details after a partial failure. |

## Methods

The v1 request methods are:

1. `initialize`
2. `package/sources`
3. `package/sourceMapping`
4. `package/search/start`
5. `package/details`
6. `package/installed`
7. `package/updates`
8. `package/consolidation`
9. `package/preview`
10. `package/previewBatch`
11. `package/execute/start`
12. `package/executeBatch/start`
13. `package/cancel`
14. `shutdown`

Search, update discovery, consolidation discovery, restore, and execution may continue after their
request response. The accepted response carries the request identity. Completion arrives through
notifications.

## Limits and paging

The server accepts MessagePack values up to 16 MiB and nesting up to 64 levels. Initialization may
negotiate a smaller outbound frame limit, but not one below 1,024 bytes. The largest page size is
200, and initialization may negotiate a smaller value.

Search, installed state, updates, and consolidation use opaque continuation values. A client must
send the returned continuation unchanged and must not infer meaning from it. A result that exceeds
the negotiated outbound limit becomes a stable `response_too_large` error; the session remains
usable.

## Installed state and restore

`package/installed` returns the best state already available before it starts a background restore.
The response has `restore: inProgress`. Each package target reports whether its graph is current,
missing, mismatched, unverifiable, or stale.

The refresh lifecycle is:

1. `package/restore/progress` reports that restore is running.
2. `package/installed/refreshed` publishes a complete refreshed page after success.
3. `package/restore/completed` reports `refreshed`, `cancelled`, or `failed`.

A failed or cancelled restore does not erase the immediate installed state. A client can keep
showing that state, display the terminal restore result, and request installed state again.

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
