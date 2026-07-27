# CLI grammar and compatibility

`dotnet-plus` is an executable compatibility layer for selected `dotnet` command families. It selects targets and verifies operations where it owns an additional safety boundary; it does not reimplement the .NET SDK.

## Grammar

Every direct form may begin with `--json`:

```text
[--json] solution|sln [<SLN_FILE>] add|list|remove|migrate [options]
[--json] package add|list|remove|update|search|download [options]
[--json] reference add|list|remove [options]
[--json] new [<template-name>|create|list|search|details|install|uninstall|update] [options]
[--json] restore|build|test|run [options]
[--json] solution <SLN_FILE> launch list|set|remove [options]
```

`solution` and `sln` are aliases. The legacy `solution <solution> add directory <path>` import form remains available for adding an existing directory hierarchy as nested solution folders.

Targets may be classic `.sln` or XML `.slnx`. A `.slnf` filter resolves its backing solution for inspection but is read-only: mutation requests against the selected filter are rejected.

## JSON results and failures

With `--json`, the command writes a single JSON envelope. It has schema version `1`, the command ID, a `success` flag, optional workspace `revision`, result summary/child arguments/standard output/standard error, a `diagnostics` array, and optional `externalExitCode`. On failure the process returns non-zero and diagnostics are safe messages rather than an alternate output shape.

A diagnostic carries severity, code, safe message, optional artifact location, retryability, and a correlation ID. Common machine codes include `invalid_input`, `unsupported_capability`, `ambiguous_target`, `workspace_conflict`, `cancelled`, `external_tool_failed`, and `partial_recovery_required`. Consumers must not infer additional persistence or recovery semantics from a successful or failed response.

## Mutation and delegation boundaries

Solution, package, reference, and template mutations are checked against the selected workspace. For mutating pipe commands, clients obtain a preview and execute with its confirmation/expected revision. Direct commands perform their documented operation and verify postconditions where possible. A failed mutation may receive narrowly scoped in-process compensation; there is no durable recovery guarantee.

`restore`, `build`, `run`, and `test` select a workspace or project where appropriate, then invoke one ordinary `dotnet` child. The installed SDK owns option validation and all command semantics; `dotnet-plus` does not interpret or model lifecycle/test output.

Launch profiles stored in `.slnLaunch` are configuration data. The tool can list, set, and remove that data; it never executes `.slnLaunch`.

## Filters and compatibility

Options and arguments intended for the delegated command are preserved, including tokens following `--`. For package and reference commands, SDK options can precede operands. `new` recognizes the literal subcommands `list`, `search`, `details`, `install`, `uninstall`, `update`, and `create`; any other first positional token is the template name for creation. It inspects output and dry-run options only to maintain its verification boundary. Unsupported or ambiguous inputs are reported rather than guessed.
