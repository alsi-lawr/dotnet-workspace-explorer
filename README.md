# dotnet-cli-plus

`dotnet-cli-plus` is a .NET tool for solution and project operations that complement the standard `dotnet` CLI. It selects and verifies workspace targets, supports `.sln` and `.slnx` solutions, treats `.slnf` filters as read-only views, and delegates ordinary lifecycle commands to `dotnet`.

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

`--json` writes one JSON envelope with `schemaVersion`, `commandId`, `success`, optional `revision`, result output, diagnostics, and an optional external exit code. Failed operations use the same envelope and return a non-zero exit code; diagnostic codes include invalid input, unsupported capability, ambiguous target, workspace conflict, external-tool failure, and partial-recovery-required.

Mutating commands are verified against the selected target. The pipe protocol additionally requires a preview ID and expected revision before execution. The tool makes narrowly scoped in-process compensation attempts when a mutation fails, but does not promise durable recovery.

## Pipe clients

For editor and automation clients, start the framed MessagePack-RPC endpoint with:

```console
dotnet-plus solution ./Demo.slnx --pipe
```

The public profile is `dotnet-cli-plus/workspace` v1.0. It is initialized before other requests and supports workspace discovery, command preview/execution, operation progress, and orderly shutdown. See the repository-only protocol reference:

- [CLI grammar and compatibility](https://github.com/alsi-lawr/dotnet-cli-plus/blob/master/docs/cli.md)
- [MessagePack-RPC workspace profile](https://github.com/alsi-lawr/dotnet-cli-plus/blob/master/docs/rpc.md)

Those references are intentionally not packaged: this README is the package's complete installation and representative-use guide.

## Development

The repository targets .NET 10. Restore the repository tools, build, and run the native test apphosts from their project outputs:

```console
dotnet tool restore
dotnet build --configuration Release
dotnet fsi scripts/verify-package.fsx --configuration Release
```

The package qualification command creates a fresh isolated package, installs that exact tool, smokes direct and pipe use, and removes its temporary package, tool, and fixture paths.
