# Benchmarks

Performance measurement is optional and threshold-free. It is not part of pull-request continuous
integration.

## Managed timing and allocation

The BenchmarkDotNet project measures repeatable encoding and decoding of small workspace-shaped
MessagePack-RPC payloads. Its `MemoryDiagnoser` reports allocations in the benchmark process.

```console
dotnet build benchmarks/Dotnet.CLI.Plus.Benchmarks/Dotnet.CLI.Plus.Benchmarks.fsproj --configuration Release
dotnet run --project benchmarks/Dotnet.CLI.Plus.Benchmarks --configuration Release -- --list flat
dotnet run --project benchmarks/Dotnet.CLI.Plus.Benchmarks --configuration Release
```

BenchmarkDotNet output is written beneath `BenchmarkDotNet.Artifacts/` unless an output location is
selected through BenchmarkDotNet options.

## End-to-end system capacity

Managed allocation diagnostics do not include the apphost's worker processes. The separate Linux
system-capacity runner uses the product `RpcCodec`, starts the built apphost and its export workers,
and samples recursive process-tree RSS through `/proc`. It generates a fresh small corpus for every
worker capacity, applies no pass/fail performance threshold, writes JSON to an explicit or
disposable output path, and removes each generated corpus.

```console
dotnet build Dotnet.CLI.Plus.slnx --configuration Release
dotnet run --project benchmarks/Dotnet.CLI.Plus.SystemCapacity --configuration Release -- \
  --configuration Release --projects 12 --items 40 --workers 1,3
```

Use `--output <path>` to retain a result outside `.agent-workspace/benchmarks/`.

The accepted formal qualification and its reference-hardware thresholds are durable history rather
than current gates. Do not replace or reinterpret
[`docs/benchmarking/export-worker-capacity.md`](../docs/benchmarking/export-worker-capacity.md) or
the retained `artifacts/performance/` results with these smaller exploratory measurements.
