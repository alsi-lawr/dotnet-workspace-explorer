# Command reference

`dotnet-we` provides the workspace service and the small set of solution operations that are not
available from the .NET SDK.

## Grammar

```text
[--json] solution|sln <SLN_FILE> launch list
[--json] solution|sln <SLN_FILE> launch set <NAME> [<PROJECT>...]
[--json] solution|sln <SLN_FILE> launch remove <NAME>
[--json] solution|sln <SLN_FILE> add directory|dir <DIRECTORY>
workspace <TARGET> --pipe
workspace <TARGET> --pipe --export-workers <POSITIVE_INTEGER>
packages <TARGET> --pipe
```

`solution` and `sln` are aliases. `<TARGET>` may be an `.sln`, `.slnx`, `.slnf`, or project file.
Solution filters are read-only.

Use the stock `dotnet` CLI for ordinary SDK commands:

```console
dotnet package add Newtonsoft.Json --project ./src/App/App.csproj
dotnet reference add ../Library/Library.csproj --project ./src/App/App.csproj
dotnet new webapi --output ./src/Api
dotnet solution ./Demo.slnx add ./src/Api/Api.csproj
dotnet build ./Demo.slnx
dotnet test ./Demo.slnx
```

Workspace Explorer does not recognize or forward those commands.

## Launch profiles

Launch profile commands read and write the `.slnLaunch` file beside the selected solution.

```console
dotnet-we solution ./Demo.slnx launch list
dotnet-we solution ./Demo.slnx launch set Web ./src/Web/Web.csproj
dotnet-we solution ./Demo.slnx launch remove Web
```

Listing is allowed through an `.slnf` target. Changes through a solution filter are rejected.

## Directory import

Directory import adds an existing physical directory hierarchy as nested solution folders without
calling `dotnet`.

```console
dotnet-we solution ./Demo.slnx add directory ./src
```

The `dir` alias is also accepted.

## JSON results

Add `--json` to a launch-profile or directory-import command for a machine-readable result:

```console
dotnet-we --json solution ./Demo.slnx launch list
```

```json
{
  "commandId": "solution.launch",
  "diagnostics": [],
  "result": {
    "output": "Web\n"
  },
  "revision": null,
  "schemaVersion": 1,
  "success": true
}
```

Failures include diagnostics:

```json
{
  "commandId": "solution.launch",
  "diagnostics": [
    {
      "artifactPath": null,
      "code": "solution.not_found",
      "correlationId": "6756aa70-e14f-4036-b828-d94ef49fcfa7",
      "location": null,
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

## Workspace service

```console
dotnet-we workspace ./Demo.slnx --pipe
```

The service exchanges MessagePack-RPC frames over standard input and output. It is intended for
editor and TUI integrations rather than interactive shell use. See
[`workspace-rpc.md`](workspace-rpc.md) for the protocol.

Project export uses three workers by default. `--export-workers` accepts a positive integer and
changes the process-local concurrency bound for that invocation.

## Package service

```console
dotnet we packages ./Demo.slnx --pipe
```

The target is required. It may be an `.sln`, `.slnx`, `.slnf`, C#, F#, or Visual Basic project, or
a directory whose projects should be included. The package service has its own MessagePack-RPC
profile and does not accept workspace RPC requests.

This command starts only the backend. It does not open an interactive package explorer or forward
ordinary `dotnet package` commands. See [`package-rpc.md`](package-rpc.md) for the protocol.
