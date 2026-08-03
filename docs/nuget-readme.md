# Workspace Explorer

Workspace Explorer provides the .NET workspace service used by terminal and editor solution
explorers.

It supports `.sln` and `.slnx` solutions containing C#, F#, and Visual Basic projects. Solution
filters (`.slnf`) are available as read-only views.

## Install

```console
dotnet tool install --global ALSI.WorkspaceExplorer
```

Run it as either `dotnet-we` or `dotnet we`.

## Editor integration

Editor integrations start the workspace service automatically. The Neovim client is
[dotnet-workspace-explorer.nvim][neovim].

The service can also be started directly:

```console
dotnet-we workspace ./Demo.slnx --pipe
```

## Commands

Workspace Explorer provides a few solution operations that are not available from the stock
`dotnet` CLI.

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
runs, and ordinary solution changes.

See the [repository][repository] and [wiki][wiki] for more information.

## License

MIT.

[neovim]: https://github.com/alsi-lawr/dotnet-workspace-explorer.nvim
[repository]: https://github.com/alsi-lawr/dotnet-workspace-explorer
[wiki]: https://github.com/alsi-lawr/dotnet-workspace-explorer/wiki
