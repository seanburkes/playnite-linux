# Nested construction-trap derivative

Additive Linux-mutated derivative of `../v10-malicious/library`; existing fixture
bytes remain untouched. `Generate.cs` replaces only the nested adversarial document
with instrumented `_type` and `$type` payloads in both an object and an array. The
original top-level trap and nonexistent-assembly payload remain. The nested type's
constructor increments a counter and throws; the test invokes it explicitly as a
working control before proving neither reader nor writer invokes it for the data.
Counts remain 3 Steam games, 1 custom game, 5 metadata records.

Build the generator externally with the pinned LiteDB 4.1.4 assembly reference,
then pass `../v10-malicious/library` and a nonexistent output library directory.
SHA256SUMS pins these bytes. The root Windows provenance is recorded in
`docs/port/units/p1-003.ledger.tsv` (Windows run 43650013648, artifact 14135728206),
and the original generation source is `tools/V10LibraryFixture/Program.cs`.
This derivative is adversarial test data, not new independent Windows oracle evidence.
