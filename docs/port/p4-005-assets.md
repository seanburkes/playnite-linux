# p4-005 — asset copy/hash

## Contract and oracle

The v10 oracle is `legacy/source/Playnite/Database/GameDatabase.cs`:
`GetFullFilePath` resolves a stored reference under `files/`, and `AddFile`
uses `<parent-GUID>/<filename>` (796–798, 833–935). The port copies existing
local Game Icon/CoverImage/BackgroundImage and Platform Icon/Cover/Background
references for imported records, never downloads external references and never
opens source files for writing. Skipped records remain explicitly reported by
the importer and retained in its intermediate JSON.

For database `/library/library.sqlite`, blobs live at
`/library/library.sqlite.assets/sha256/<first-two-hex>/<64-lowercase-hex>`.
SHA-256 over all source bytes is the deduplication key, independent of filename,
record or field. Every per-reference result retains its full original path
(including original filename), provenance, expected hash and size. Image formats
are detected by signature, not extension, so these migrated image references do
not require a filename suffix. Typed model references become POSIX paths relative
to the database's parent; original documents remain unchanged in `LegacyJson`.

The raw LiteDB reader captures hashes by streaming assets instead of buffering
their full contents. Only root database/marker files need immutable byte copies.
Unreadable asset entries are retained in its manifest with `status=unreadable`
and null hash/size; marker/database read failures still refuse the import.
That unreadable snapshot status is preserved per reference even if the source
lock/permission failure clears before copying: without a captured hash the writer
reports unreadable instead of accepting new, unverified bytes or aborting with a
missing-hash error. A readable asset with no manifest hash still refuses explicitly.
The reader has no Core/SQLite reference; the writer has no LiteDB reference.

## Dimensions and accounting

Only PNG (IHDR), GIF87a/GIF89a (logical screen) and JPEG (SOF, including progressive)
dimensions are decoded. The decoder uses a 24-byte header buffer, seeks over
JPEG metadata segments, and stops at SOF, SOS or EOI; no bitmap/pixel decoder,
decompression or UI dependency is used. Dimensions are positive signed 32-bit
integers. Unsupported formats (including BMP, ICO, WebP, SVG) are copied with
null dimensions and `unsupported`. Recognized malformed/truncated headers give
null dimensions and `invalid-header`; this is not pixel-stream validation.

The migration report includes one AssetResult per nonempty imported asset field:

| Outcome | Meaning |
| --- | --- |
| copied / reused | Verified complete blob published / already present and verified |
| planned | Dry-run verifies source hash and reads dimensions, without writes |
| missing | FileNotFoundException or DirectoryNotFoundException opening the reference |
| unreadable | Permission/sharing/source-read failure, not absence |
| empty | Readable zero-byte file, not absence or unsupported image |
| external | Absolute Windows/POSIX path or URI retained, not accessed/downloaded |

MissingAssets, UnreadableAssets and EmptyAssets count **references**, not distinct
blobs; each result includes record file/collection/identity, field and asset path.
Missing/empty/unreadable/external typed references remain unchanged and the
complete report is written beside the destination. Null/empty fields mean no
reference and produce no asset result. Traversal and symbolic-link components
are refused rather than followed. Unexpected destination I/O errors fail loudly.

## Publication, retries and tests

Copying uses a 64 KiB buffer and a random CreateNew `.tmp` sibling; it verifies
manifest size/SHA-256, flushes and closes the file, checks cancellation, then
renames without overwrite. An existing hash-named blob is rehashed and never
rewritten. Source bytes that disagree with the captured hash or a corrupt stored
blob cause an explicit hash-mismatch refusal. Staging ownership still binds
retry to the same source snapshot. A changed snapshot cannot take over staging.
Database re-import still refuses an existing database, as before; the asset
copier itself is idempotent and interrupted migration retries reuse verified blobs.

SIGKILL at `AssetPartialCopy` occurs after one 64 KiB write of a 1 MiB source.
The test verifies only a nonempty, incomplete `.tmp` exists, kills the child
without managed cleanup, then retries and verifies the complete published hash.
An abrupt crash can leave an invisible/unreferenced `.tmp` orphan; retry does not
delete foreign files. This contract promises atomic visibility, not directory
fsync/power-loss durability or automatic orphan garbage collection.

`AssetCopyTests` additionally covers both byte-exact frozen-v10 PNGs and their
IHDR dimensions, synthetic missing-reference supplement with honest provenance,
unchanged source bytes, typed Game/Platform remapping, original legacy documents,
cross-filename deduplication, no-op second copy, both mismatch directions, locked
reader/source accounting, empty/missing distinctions, dry-run, cancellation and
handle disposal, external paths, links/traversal, JPEG/GIF header cases and a
256-input seeded malformed-header fuzz/property smoke check. Existing importer
crash/restart tests remain enabled. Additive fixtures and the eventual actual-CI
floor capture require `oracle-change`; no existing oracle is redefined.

Validation and independent adversarial review evidence is recorded in the
`p4-005` unit shard; aggregate queue/ledger and protected parity TSV are untouched.

Actual linux-ci [43952540098](https://github.com/seanburkes/playnite-linux/actions/runs/43952540098)
on implementation head `3d0815a62fdfb64305b49e97405d658d2b003bd8` passed
warning-as-error locked restore/build, full suites, bounded serialization fuzz and
native smoke. Downloaded JUnit case elements and suite totals, test-counts.json
and exit files confirm Core **1736**, Architecture **173**, Smoke **4**, all passed,
zero failed/skipped and all MTP exits 0. Only the Core floor rises from 1706 to the
actual 1736 union. The floor commit is evidence-only; final pushed-head linux-ci
and two firstmate-arranged adversarial diff reviews remain readiness gates.

Review A's released-lock fix was validated by actual linux-ci
[43954161343](https://github.com/seanburkes/playnite-linux/actions/runs/43954161343)
on `723cf8e396a7af73038db170f0885db734347632`: downloaded artifacts confirm
Core **1737**, Architecture **173**, Smoke **4**, all passed with zero failures/skips
and all exits 0. The Core floor now protects that additional regression; previous
captures remain intact. This evidence-only delta still requires final-head CI
and A re-review/B closure before readiness.
