# p6-168 delta review (delta2)

- PR: https://github.com/seanburkes/playnite-linux/pull/168
- Range: `abddbf1..9eb4059`
- Head: `9eb4059d4bf0719432360a10fa98ce9027e1e715` (`chore(p6-012): cite the comparer-tiebreak linux-ci run`)
- Diff: `data/p6-168/delta2.diff`, sha256 `9821ba807c8cac355adfb42c93bed109db3b1fc8d718b98afbf3a281d0cfcd12`
- Diff identity: `git diff abddbf1 9eb4059` reproduces that file byte-for-byte (same sha256), 6 files, +46/-11.
- Scope: code head is `8850d12` (the source/test fix); `9eb4059` adds only the ledger fragment. Nothing but the review delta is considered.

## Verdict

One blocking finding, P2. Confirmations (1), (3) and (4) hold; confirmation (2) does **not**:
the new total-order test cannot fail when the comparer's identity key is removed or changed, so
the regression it is named for is unguarded. No P1 found. Hold for the captain's merge decision.

## P2 — the total-order test does not pin the comparer's identity key

`tests/Playnite.Core.Tests/Library/PagedLibraryReaderTests.cs:276-298` (helpers `:315-323`, `:519`)

The test derives `comparerOrder` by sorting `TieGames()` with `PagedLibraryComparer`
(`:286-289`) and compares it with the SQL page. That assertion is satisfied by accident:

- `TieGames()` (`:315-323`) is declared in ascending id order: 1, 2, 3, 4, 7, 9.
- Within each name tie group the declaration order is already the ordinal id order:
  `Tie A` → 2, 4, 9; `Tie B` → 1, 7.
- `Enumerable.OrderBy` is stable, so an implementation that returns only `nameResult`
  (`PagedLibraryComparer.cs:42-44`, i.e. delete the final `string.CompareOrdinal` branch)
  produces exactly the same `comparerOrder` and the test passes.

Concrete failure: change `src/Playnite.Core/Library/PagedLibraryComparer.cs:44` to
`return nameResult;` (drop the identity key) — the new test stays green although the comparer
no longer mirrors `game.Id ASC`. Replacing the key with `x.Id.CompareTo(y.Id)` also stays
green: every id comes from `Id(int)` (`:519`), which zero-pads to 12 hex digits, so Guid
binary order and `"D"`-text ordinal order coincide for this data set even though they diverge
for real Guids (e.g. a first field with the high bit set, where `Guid.CompareTo` compares
signed ints). The pre-existing `Text_orders_match_the_canonical_in_memory_comparer`
(`:255-273`) does not cover it either: its tied text groups (10/11/15, 11/15) are likewise
declared in identity order.

The stored-Id read at `:291-293` is correct in itself (TEXT, 36-char lowercase `"D"` form,
schema `LibrarySchema.cs:27`) but it is only used for a membership assertion, not for the
expected order. Consequently `docs/port/p6-012-paged-reader.md:81-82` ("a total-order test
that reads the stored `Id` text and pins the comparer's final ordinal identity key") and the
same claim in `docs/port/units/p6-012.ledger.d/168-paged-reader.tsv` overstate what is
enforced.

Suggested fix: make the fixture order-sensitive — seed one tie group in non-identity order
(e.g. reverse a dedicated array for this test) and/or include one id whose text order differs
from `Guid.CompareTo` order, then assert the comparer order against the stored `Id` strings
read back at `:292` instead of a separately reconstructed list.

## Confirmation (1) — comparer identity tie-break and its order: holds

`PagedLibraryComparer.Compare` ends with `string.CompareOrdinal(StoredIdentity(x),
StoredIdentity(y))` (`PagedLibraryComparer.cs:42-44`), `StoredIdentity` is
`game.Id.ToString("D")` (`:47`), and `PageSql` always ends with `game.Id ASC`
(`PagedLibraryReader.cs:109`) after the name tie-break (`:107`), with direction applied only
to the primary (`:95-108`), matching the comparer's direction handling
(`PagedLibraryComparer.cs:33-44`). The schema stores ids as fixed-width, lowercase, 36-char
TEXT (`LibrarySchema.cs:27`), so SQLite BINARY byte order on the stored id text equals .NET
ordinal order on the `"D"` string. The ordering shape and the identity key agree.

## Confirmation (2) — stored Id read as TEXT and ordinal order pinned: fails (see P2)

The read itself is a TEXT read (`QueryAsync<string>("SELECT Id FROM game;")`,
`PagedLibraryReaderTests.cs:292`) and the expected text order is built with
`StringComparer.Ordinal` on the id text (`:294-297`), but the comparer side is not actually
pinned: the test passes with the identity key removed or replaced by `Guid.CompareTo`.

## Confirmation (3) — no stale BINARY remark, canonical rule documented: holds

The reader remark now states NOCASE and the identity text tie-break
(`PagedLibraryReader.cs:11-13`). `docs/port/p6-012-paged-reader.md:18-21` states the
implemented order (primary, effective name ASC when the primary is not name, identity text
ordinal ASC; direction only on the primary), and `:36-40` states the canonical NOCASE rule.
The only remaining "BINARY" occurrence in the p6-012 material is
`docs/port/p6-012-paged-reader.md:80-81`, "a case-only test that fails under BINARY order",
which is an accurate description of that test's purpose, not a stale claim about the
implemented collation. No `BINARY` collation claim remains in the p6-012 source or docs.

## Confirmation (4) — nothing else changed: holds

The supplied diff equals `git diff abddbf1 9eb4059` exactly and touches only the six expected
files. The extra hunks are consequences of the fix: the reader remark (`PagedLibraryReader.cs:11-13`),
removal of the now-redundant `.ThenBy(game => game.Id)` from the existing text-order test
(`PagedLibraryReaderTests.cs:265-267`), and the doc/queue/ledger text updates.
