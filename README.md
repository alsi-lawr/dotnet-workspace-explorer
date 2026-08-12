<div align="center">

<img
  src="assets/dotnet-workspace-explorer.svg"
  width="128"
  height="128"
  alt="dotnet-workspace-explorer logo">

# dotnet-workspace-explorer

**The shared .NET core for terminal and editor solution explorers.**

<a
  href="https://github.com/alsi-lawr/dotnet-workspace-explorer/blob/master/CONTRIBUTING.md">
  <img src="https://img.shields.io/badge/status-experimental-f59e0b" alt="Status: experimental">
</a>
<a href="https://github.com/alsi-lawr/dotnet-workspace-explorer/blob/master/global.json">
  <img
    src="https://img.shields.io/badge/runtime-.NET_10-512bd4?logo=dotnet&logoColor=white"
    alt="Runtime: .NET 10">
</a>
<a href="https://github.com/alsi-lawr/dotnet-workspace-explorer/blob/master/LICENSE">
  <img src="https://img.shields.io/badge/license-MIT-22c55e" alt="License: MIT">
</a>

</div>

Workspace Explorer reads and edits `.sln` and `.slnx` workspaces containing C#, F#, and Visual
Basic projects. It provides the workspace tree, contextual edits, dependency details, launch
profiles, and change notifications used by editor integrations. Solution filters (`.slnf`) are
available as read-only views.

Editor clients receive complete Git decorations through one canonical status contract. Each
workspace node can report staged, unstaged, renamed, deleted, unmerged, untracked, and ignored
states in deterministic order.

## Install

```console
dotnet tool install --global ALSI.WorkspaceExplorer
```

Or install it from the Nix flake:

```console
nix profile install github:alsi-lawr/dotnet-workspace-explorer
```

Both command forms work:

```console
dotnet-we workspace ./Demo.slnx --pipe
dotnet we workspace ./Demo.slnx --pipe
```

The Neovim integration is
[`dotnet-workspace-explorer.nvim`](https://github.com/alsi-lawr/dotnet-workspace-explorer.nvim).
It starts the workspace service automatically.

Package clients can use the same tool as a backend:

```console
dotnet we packages ./Demo.slnx --pipe
```

This starts the package service without opening a terminal interface. The Visual Studio-style
terminal interface is the separate
[`dotnet-package-explorer`](https://github.com/alsi-lawr/dotnet-package-explorer) project.

## Commands

Workspace Explorer only provides commands that add behavior beyond the .NET SDK.

Manage `.slnLaunch` profiles:

```console
dotnet-we solution ./Demo.slnx launch list
dotnet-we solution ./Demo.slnx launch set Web ./src/Web/Web.csproj
dotnet-we solution ./Demo.slnx launch remove Web
```

Import an existing directory as nested solution folders:

```console
dotnet-we solution ./Demo.slnx add directory ./src
```

Use the normal `dotnet` commands for packages, references, templates, builds, tests, restores,
runs, and ordinary solution changes. Workspace Explorer does not wrap those commands.

Add `--json` when another program needs the result:

```json
{
  "commandId": "solution.launch",
  "diagnostics": [
    {
      "artifactPath": null,
      "code": "solution.not_found",
      "correlationId": "6756aa70-e14f-4036-b828-d94ef49fcfa7",
      "retryable": false,
      "safeMessage": "The solution or filter file was not found.",
      "severity": "Error"
    }
  ],
  "result": {
    "output": null
  },
  "revision": null,
  "schemaVersion": 1,
  "success": false
}
```

## Editor service

Editor clients start the MessagePack-RPC service with:

```console
dotnet-we workspace ./Demo.slnx --pipe
```

Project export uses three workers by default. Set a different capacity when needed:

```console
dotnet-we workspace ./Demo.slnx --pipe --export-workers 4
```

See the [command reference][commands], [workspace RPC reference][workspace-rpc], and
[package RPC reference][package-rpc] for the complete interfaces.

## Contributing

See [CONTRIBUTING.md][contributing].

## License

MIT. See [LICENSE](https://github.com/alsi-lawr/dotnet-workspace-explorer/blob/master/LICENSE).

[commands]: https://github.com/alsi-lawr/dotnet-workspace-explorer/blob/master/docs/commands.md
[contributing]: https://github.com/alsi-lawr/dotnet-workspace-explorer/blob/master/CONTRIBUTING.md
[package-rpc]:
  https://github.com/alsi-lawr/dotnet-workspace-explorer/blob/master/docs/package-rpc.md
[workspace-rpc]:
  https://github.com/alsi-lawr/dotnet-workspace-explorer/blob/master/docs/workspace-rpc.md
