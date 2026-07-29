# F# diagnostic review

This optional review command opens exactly one repository `.fs`, `.fsi`, or `.fsx` file directly
through the pinned FsAutoComplete LSP server. Run it once for each touched F# file. It waits for
that file's published diagnostics and `fsharp/documentAnalyzed`, then requires a quiet diagnostic
window before reporting the result. Analysis has no internal deadline; terminating the command
cancels the server.

Restore the isolated review tool and run the BCL-only client:

```console
dotnet tool restore --tool-manifest review/.config/dotnet-tools.json
dotnet fsi review/verify-fsharp-diagnostics.fsx -- path/to/TouchedFile.fs
```

FsAutoComplete is not restored or invoked by pull-request continuous integration. The review
command creates only disposable state below `.agent-workspace/review/` and removes its run directory
when it exits.
