# Release package verification

After restoring and building Release, run the release-owned package smoke once:

```console
dotnet fsi release/verify-package.fsx --configuration Release
```

The command packs `Dotnet.CLI.Plus`, checks its identity and essential .NET tool layout, installs
that exact package into an isolated tool path, and executes one direct solution mutation. It has no
pipe client or independent MessagePack implementation and is not part of pull-request continuous
integration. Its repository-local run directory is removed on exit.
