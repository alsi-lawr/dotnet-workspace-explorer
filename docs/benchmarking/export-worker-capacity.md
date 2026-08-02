# Export evaluator worker capacity

This focused benchmark measures how isolated MSBuild evaluator-worker capacity affects the private
evaluation phase of a complete 500-project export. It is planning evidence for DOTNET-028, not the
formal end-to-end performance qualification.

## Environment and source

- Product commit: `752e613d65433d984d906e402d22e23bc393fe59`
- Release apphost SHA-256:
  `b13b3d0622a4be89060aae3a21f1c05d5c71d030511beb858a9824f42de78dbf`
- Release MSBuild assembly SHA-256:
  `42b3d6deded4de64309e4297dd000f053ea0145a50af1a4ab2c9e2c7d566e172`
- Host: Linux 6.18.39, x86-64
- CPU: AMD Ryzen 7 5700X3D, 8 cores/16 logical processors
- Memory: 49,252,252 KiB
- SDK/toolset: .NET SDK 10.0.302

## Method

Each capacity ran once in a fresh probe process against the unchanged Release apphost and private
worker protocol. The probe generated 500 SDK projects with 500 explicit `Compile` items each,
distributed projects round-robin across the configured number of isolated workers, decoded every
evaluation response, and verified all 250,000 expected items.

Immediately after each response, the probe invalidated that project to model an export-only,
non-retaining lifetime. This adds an extra private RPC and unload operation, so the phase timings are
conservative relative to a purpose-built non-retaining worker path.

The probe sampled recursive client-plus-descendant `VmRSS` from Linux `/proc` every 10 ms. It
disposed and reaped every worker and removed the generated corpus before accepting a result.
"Worker phase" includes evaluation, response decoding, and immediate invalidation. "Total" also
includes worker shutdown. Both exclude workspace projection, export chunk construction/encoding,
and public emission.

## Results

| Capacity | Worker phase | Total | Speedup | Peak RSS bytes | Peak RSS | Additive export estimate |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 63.598 s | 63.661 s | 1.00x | 651,567,104 | 0.607 GiB | 81.531 s |
| 2 | 35.652 s | 35.743 s | 1.78x | 825,925,632 | 0.769 GiB | 53.584 s |
| 3 | 26.442 s | 26.570 s | 2.41x | 1,011,937,280 | 0.942 GiB | 44.375 s |
| 4 | 21.824 s | 21.955 s | 2.91x | 1,221,623,808 | 1.138 GiB | 39.756 s |
| 5 | 19.695 s | 19.839 s | 3.23x | 1,417,363,456 | 1.320 GiB | 37.627 s |
| 6 | 18.415 s | 18.583 s | 3.45x | 1,628,061,696 | 1.516 GiB | 36.348 s |
| 8 | 18.038 s | 18.240 s | 3.53x | 1,950,707,712 | 1.817 GiB | 35.971 s |

Capacity 7 was not measured. The additive estimate adds 17.932456 seconds of other work from the
existing complete-export attribution. It does not model overlap or CPU contention between worker
evaluation and projection/emission, and is not an end-to-end measurement.

The original 12.067544-second worker-phase planning target was a derived residual:

```text
71.614324 s measured complete export
- 53.681868 s measured evaluator/client/worker calls
= 17.932456 s other observed export work

30.000000 s original complete-export gate
- 17.932456 s other observed export work
= 12.067544 s residual worker-phase budget
```

This probe showed that the original 30-second gate was not tenable under the retained complete-graph
and non-retaining worker decisions. The replacement default-configuration gate is 50 seconds. Under
the same additive assumption, its residual worker-phase budget is 32.067544 seconds and the
capacity-3 estimate has 5.625 seconds of headroom. These residuals are useful only while the observed
other work remains additive and unchanged.

## Analysis

Capacity 3 provides the strongest first default in this matrix: 2.41x worker-phase speedup for
approximately 55% more measured peak RSS than capacity 1, while remaining below 1 GiB. Capacity 4
continues to improve time, but scaling then saturates sharply: capacity 5 saves 2.129 seconds over
capacity 4, capacity 6 saves 1.280 seconds over capacity 5 while crossing 1.5 GiB, and capacity 8
saves only another 0.377 seconds while reaching 1.817 GiB.

No measured capacity met the original 12.067544-second residual. A configurable default of 3 is a
balanced resource policy and its 44.375-second additive estimate is consistent with the replacement
50-second gate. This is still not end-to-end qualification: acceptance requires the formal public
export run and its graph, ordering, lifecycle, time, and aggregate-RSS checks at the default.
