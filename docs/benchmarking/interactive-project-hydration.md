# Interactive project hydration

This benchmark measures the latency a Neovim user sees when expanding the first projects in a real
solution. It is acceptance evidence for the evaluator prewarm, watcher handoff, and in-place Lua
delta work; it is not an export benchmark.

## Environment and source

- Core baseline: `ecd524e252f0f08fda7cac6708f36367ae938330`
- Plugin baseline: `98f19af62ac81e8bd83fc2af004ce3d69aff2d14`
- Measured core candidate: `f267e1832d6bef314e0746fe33aed5c6e1ef1d9a`
- Measured plugin candidate: `375093415306dfc571a8b3bf54164e5b429e01e0`
- Release apphost SHA-256:
  `24ebb5f77273c4c88a63f98740464fae42425da456c26abc91aa47a2b3d8fdfa`
- Fixture: BlokeBot commit `f20b05940cf57c84946143f487bc87202f19b5ea`, with local
  `BlokeBot.slnx` and `global.json` changes retained
- Solution SHA-256:
  `3cf70e412507e76d189138b5e1b479ab0872f46c9363eeb35b1399fe40a040d0`
- Host: Linux 6.18.39, AMD Ryzen 7 5700X3D, 16 logical processors, 49,252,252 KiB memory
- Runtime: .NET SDK 10.0.302 and Neovim 0.12.4

## Method

Each candidate trial started a fresh headless Neovim process from the plugin's locked Nix shell.
The real Lua `Workspace` client started the exact Release apphost against `/home/alex/dev/BlokeBot`
with Git status disabled, expanded the workspace and its Solution Folders, and then expanded the
first three projects sequentially. The client stopped its core before the next trial.

The five trials therefore use fresh core and evaluator processes. They do not flush the operating
system filesystem cache. Timings use `vim.uv.hrtime()` around the asynchronous client callbacks.
The baseline is the initial single diagnostic trial from the same investigation, so its speedup is
directional; the candidate's absolute five-trial sub-second result is the acceptance measurement.

A separate `strace` probe inspected raw process creation and MessagePack writes. It observed one
public core, one evaluator host, one `dotnet --version`, one `dotnet --list-sdks`, and exactly three
`project-evaluation/evaluate` calls for the three projects. Trace timings were excluded because
instrumentation materially slowed the processes.

## Results

| Operation | Baseline | Candidate min | Candidate median | Candidate max | Baseline/median |
|---|---:|---:|---:|---:|---:|
| Start client and initialize | 281.1 ms | 285.9 ms | 296.4 ms | 303.5 ms | 0.95× |
| Expand workspace root | 57.7 ms | 197.7 ms | 201.6 ms | 213.3 ms | 0.29× |
| Hydrate BlokeBot.Commands | 2,593.7 ms | 450.2 ms | 459.9 ms | 474.5 ms | 5.64× |
| Hydrate BlokeBot.Core | 4,538.9 ms | 458.1 ms | 475.1 ms | 625.7 ms | 9.55× |
| Hydrate BlokeBot.Eventing | 4,452.8 ms | 503.7 ms | 552.0 ms | 721.3 ms | 8.07× |

All fifteen candidate project expansions completed below one second. The three-project median sum
fell from 11.585 seconds in the baseline diagnostic trial to 1.487 seconds, a directional 7.79×
improvement.

## Analysis

Starting evaluator discovery with the explorer shifts roughly 144 ms of median work into the first
workspace expansion, but removes multi-second work from the interactive project path. Snapshot
materialization now omits irrelevant SDK-authored plumbing, while retaining explorer properties and
arbitrary project/import declarations.

The larger correction was lifecycle-related. Hydration now establishes project and ancestor watcher
coverage before evaluation; SDK and NuGet imports, glob roots, and resolved toolchain items no
longer trigger a handoff invalidation; and ordinary uncovered paths use targeted invalidation
instead of rebuilding the workspace. The trace's one evaluation per project confirms that the warm
worker is retained on the measured path.

The Lua client applies compatible deltas to the existing tree, so the expanded path and selection
remain stable without a second render. Reset, stale, malformed, and incompatible deltas retain the
full last-good-tree reconciliation fallback.
