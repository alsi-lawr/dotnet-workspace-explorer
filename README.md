<div align="center">

<img src="assets/dotnet-cli-plus.svg" width="128" height="128" alt="dotnet-cli-plus logo">

# dotnet-cli-plus

**Solution operations beyond the standard .NET CLI.**

[![Status: experimental](https://img.shields.io/badge/status-experimental-f59e0b)](#development)
[![Runtime: .NET 10](https://img.shields.io/badge/runtime-.NET_10-512bd4?logo=dotnet&logoColor=white)](#development)

</div>

`dotnet-cli-plus` is an experimental .NET tool for solution and project operations that complement
the standard `dotnet` CLI. It selects and verifies workspace targets, supports `.sln` and `.slnx`
solutions, treats `.slnf` filters as read-only views, and delegates ordinary lifecycle commands to
`dotnet`.

## Install

Install the package from a NuGet source:

```console
dotnet tool install --global Dotnet.CLI.Plus
dotnet-plus --json solution ./Demo.slnx list
```

The executable name is `dotnet-plus`. Where the .NET tool muxer discovers the installed tool, `dotnet plus` is an equivalent convenience form; scripts and examples should use `dotnet-plus` directly.

## Use

Pass a solution or project target where the command requires one:

```console
# Add a project to an XML solution and receive a machine-readable result.
dotnet-plus --json solution ./Demo.slnx add ./src/Demo/Demo.csproj

# Inspect a solution, including a classic .sln.
dotnet-plus solution ./Demo.sln list

# A solution filter is a read-only target.
dotnet-plus solution ./Demo.slnf list

# Select a target, then delegate one ordinary lifecycle command to dotnet.
dotnet-plus build ./Demo.sln --configuration Release
```

`--json` writes one JSON envelope with `schemaVersion`, `commandId`, `success`, optional `revision`, result output, diagnostics, and an optional external exit code. Failed operations use the same envelope and return a non-zero exit code; diagnostic codes include `invalid_input`, `unsupported_capability`, `ambiguous_target`, `workspace_conflict`, `external_tool_failed`, and `partial_recovery_required`.

Mutating commands are verified against the selected target. For pipe mutations, a preview ID and expected revision are required before execution. The tool makes narrowly scoped in-process compensation attempts when a mutation fails, but does not promise durable recovery.

## Pipe clients

For editor and automation clients, start the framed MessagePack-RPC endpoint with:

```console
dotnet-plus solution ./Demo.slnx --pipe

# Optional process-local export concurrency; the default is 3.
dotnet-plus solution ./Demo.slnx --pipe --export-workers 4
```

The public profile is `dotnet-cli-plus/workspace` v1.0. It is initialized before other requests and supports workspace discovery, command preview/execution, operation progress, and orderly shutdown. See the repository-only protocol reference:

- [CLI grammar and compatibility](https://github.com/alsi-lawr/dotnet-cli-plus/blob/master/docs/cli.md)
- [MessagePack-RPC workspace profile](https://github.com/alsi-lawr/dotnet-cli-plus/blob/master/docs/rpc.md)

Those references are intentionally not packaged: this README is the package's complete installation and representative-use guide.

## Development

The repository targets .NET 10. Restore the repository tools, build, and run the native test apphosts from their project outputs:

```console
dotnet tool restore
dotnet restore Dotnet.CLI.Plus.slnx
dotnet build Dotnet.CLI.Plus.slnx --configuration Release --no-restore
```

Pull-request continuous integration runs restore, Debug and Release builds, configured formatting,
and the ordinary native test apphosts. Additional concern-specific commands are intentionally
manual:

- [F# diagnostic review](review/README.md)
- [release package smoke](release/README.md)
- [performance benchmarks](benchmarks/README.md)
