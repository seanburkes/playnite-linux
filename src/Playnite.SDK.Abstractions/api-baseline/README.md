# SDK Abstractions preview baseline

`Playnite.SDK.Abstractions.dll` is a **reference assembly**, not an implementation or a
v6 compatibility shim. It freezes the v7 abstraction surface declared in
`../PublicAPI.Shipped.txt` and `../PublicAPI.Unshipped.txt`: 62 exported types and 603
declarations, with assembly version `7.0.0.0`, targeting `net11.0`.

SHA-256: `e79568cb2f30f404244c3a4db13fe7e731dc617f448a8789fcff591eead8beb8`.

It was generated with .NET SDK 11.0.100 from this repository's sources by building
`src/Playnite.SDK.Abstractions` in Release and copying
`obj/Release/net11.0/ref/Playnite.SDK.Abstractions.dll` here. Only the first baseline
bootstrap used `-p:ApiCompatValidateAssemblies=false` before a contract assembly existed;
ordinary builds enable validation unconditionally in the project. No target regenerates
or replaces this baseline.

Microsoft.DotNet.ApiCompat.Task 11.0.100 compares this left-hand contract against the
newly compiled implementation after `CoreCompile`, including on a normal `dotnet build`
(not only on pack). Missing baselines fail validation. Framework reference assemblies
come from the implementation's resolved reference set, as supported by the task.
No compatibility suppressions or disabled checks are committed.

`PublicAPI.Shipped.sha256` pins every byte of the reviewed shipped text. Tests reject
same-arity signature changes and enum renames even when text and binary baselines
are changed together. Both PublicAPI text files and this whole directory are in
`PROTECTED_PATHS` and `CODEOWNERS`; changing any baseline (including this checksum)
requires the `oracle-change` label and the protected-path gate; review is adversarial on the
owner's behalf.
`Playnite.SDK.Abstractions.sha256` separately pins the binary baseline bytes and is
asserted by the architecture suite. The binary baseline is regenerated only when a
reviewed surface change adds declarations. The p6-006 and p6-007 surface moves were merged
(`d1d57be`, `b80847b`), and p6-008 regenerated this baseline from those merged sources so the
pinned text and binary surfaces stay in step with the frozen v7 surface.

For compatible additions, declare the new API in `PublicAPI.Unshipped.txt`; do not
overwrite the baseline to silence a failure. Any intentional breaking change needs an
explicitly reviewed compatibility decision. PublicApiAnalyzers tracks the text surface
(including nullable annotations); ApiCompat independently catches binary breaks even
when somebody updates that text to match the breaking source.

This is **not** the complete v6→v7 report or a claim that the v6 SDK has been ported.
See `docs/port/ARCHITECTURE-CHECKS.md` for the seed scope, oracle paths and proofs.
