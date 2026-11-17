# p5-001 synthetic Steam corpus

Every file here was authored from `docs/port/08-STEAM-SPEC.md` §§2–4 and the
approved p5-001 parser decision table, not captured or copied from any Steam install.
All names, paths, appids, depot ids, sizes and timestamps are invented test data.
No account data, credentials, real corpus, or protected fixture was accessed.

| File | Spec-derived coverage |
| --- | --- |
| `libraryfolders.vdf` | Modern and legacy libraries; external path; duplicate appid; unknown object; 64-bit sizes. |
| `appmanifest_42.acf` | Installed app, UTF-8 name, optional numerics, four-level depots, shared depots, escaped script metadata and empty mount config. |
| `appmanifest_43.acf` | StateFlags 6, differing build/target, pending bytes, nonempty mount config. |
| `escapes.vdf` | Tool-shaped nested objects, all supported quoted escapes. |
| `broken.vdf` | Unclosed objects; must never return partial libraries. |

`manifest.sha256` pins the exact bytes. Valid files round-trip structurally through
canonical quoted writing; whitespace, comments and BOM are not reproduced. The invalid
file is a rejection seed, not a round-trip input. Filesystem observations (unavailable
library, missing install, Proton state) are explicit test inputs, never host probes.

The additive `SteamFuzzTarget` uses the existing `FuzzOptions`, `FuzzMutator`,
`FuzzExecution` and `FuzzRunResult`. The unfiltered Core suite invokes it in linux-ci,
without editing the foreign-owned serialization harness or protected corpus. Budget:
45 seconds or 100,000 cases, whichever completes first; 2,000 ms/case; 65,536-byte
input limit; depth 64; 256 MiB retained-heap ceiling; per-parse transient allocation
ceiling `2 MiB + 128 × maxInputBytes`. Invalid UTF-8 reaches the parser as original
bytes. Structured invalid-UTF-8, depth-512 and oversized probes must be rejected.
