# Contributing

Keep changes focused and verify the smallest relevant part of the solution before running broader
checks.

## Set up

Use the .NET SDK selected by [`global.json`](global.json). The Nix development shell provides the
SDK and formatting tools:

```console
nix develop
```

Without Nix, install the selected .NET SDK and restore the repository tools:

```console
dotnet tool restore
dotnet restore Dotnet.WorkspaceExplorer.slnx
```

## Choose a target

Build the project you changed and run its nearest test target while iterating:

| Change | Test target |
| --- | --- |
| RPC framing and sessions | `tests/unit/Rpc` |
| Package models, adapters, previews, and mutations | `tests/unit/PackageExplorer` |
| Package processes and package RPC sessions | `tests/integration/PackageExplorer` |
| Solution projection and edit planning | `tests/unit/Workspaces` |
| SDK and project evaluation | `tests/integration/ProjectEvaluation` |
| CLI, workspace RPC, indexing, and editing | `tests/integration/Workspaces` |

For example:

```console
dotnet build tests/unit/Rpc/Dotnet.WorkspaceExplorer.Rpc.UnitTests.fsproj --configuration Debug
dotnet tests/unit/Rpc/bin/Debug/net10.0/Dotnet.WorkspaceExplorer.Rpc.UnitTests.dll --fail-skips on
```

For package backend work, build and run these focused targets:

```console
dotnet build tests/unit/PackageExplorer/Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests.fsproj
dotnet tests/unit/PackageExplorer/bin/Debug/net10.0/\
Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests.dll --fail-skips on

dotnet build tests/unit/Rpc/Dotnet.WorkspaceExplorer.Rpc.UnitTests.fsproj
dotnet tests/unit/Rpc/bin/Debug/net10.0/\
Dotnet.WorkspaceExplorer.Rpc.UnitTests.dll --fail-skips on

dotnet build \
  tests/integration/PackageExplorer/\
Dotnet.WorkspaceExplorer.PackageExplorer.IntegrationTests.fsproj
dotnet tests/integration/PackageExplorer/bin/Debug/net10.0/\
Dotnet.WorkspaceExplorer.PackageExplorer.IntegrationTests.dll --fail-skips on
```

The package unit target covers adapters, installed state, previews, and mutations. The RPC unit
target covers the package schema and golden frames. The package integration target covers stock
`dotnet package` processes, restore behavior, and package RPC sessions.

Use both Debug and Release for cross-cutting changes. The
[build workflow](.github/workflows/build-and-test.yml) is the canonical list of full CI targets.

Workspace behavior must remain valid for C#, F#, and Visual Basic projects. `.sln` and `.slnx`
files are writable; `.slnf` files are read-only views. Avoid platform assumptions because CI runs
on Linux, macOS, and Windows.

## Format

Format F# and C# with the pinned repository tools:

```console
dotnet fantomas .
dotnet csharpier format src/ProjectEvaluation
```

Check formatting without changing files:

```console
dotnet fantomas --check .
dotnet csharpier check src/ProjectEvaluation
```

## Clear F# diagnostics

The Nix development shell includes FsAutoComplete. Before submitting an F# change, use
FsAutoComplete to inspect every F# file you touched and clear its applicable warnings and
suggestions.

Use one FsAutoComplete process for the workspace and stop it when the check is complete. Do not run
multiple language-server processes in parallel. FsAutoComplete is a local review tool, not a CI
dependency; the compiler remains the deterministic warning gate.

Check touched package backend files in dependency order: `src/Packages`, `src/PackageExplorer`,
`src/Rpc`, then `src/WorkspaceExplorer`. Wait for diagnostics for one file before opening the next
one. Clear warnings and suggestions that apply to the change, then stop the single server and check
that no FsAutoComplete process remains.

## Tests and pull requests

- Add focused tests for behavior changes.
- Use FsUnit assertions and full scenario identifiers in F# tests.
- Update user documentation when commands or public behavior change.
- Keep unrelated cleanup and formatting out of the change.
- State which build, test, and formatting targets you ran.

Benchmarks are optional and are not pull-request gates. See
[`benchmarks/README.md`](benchmarks/README.md) for commands and
[`docs/benchmarking`](docs/benchmarking) for recorded methods and results.
