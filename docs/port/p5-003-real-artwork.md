# p5-003 real artwork identity evidence

- Date: 2026-11-19
- Unit: `p5-003` (Steam artwork/metadata cache)
- Artifact: `tests/fixtures/steam/real-artwork/` and
  `tests/Playnite.Core.Tests/Artwork/RealArtworkIdentityTests.cs`
- Scope: **identity only.** Image bytes and Steam CDN/remote mapping remain **UNKNOWN** and
  are out of v1 scope.

## What is proven

A read-only capture of a real Linux Steam cache records the app directories and file names
Steam actually wrote for five installed apps:

| App id | Captured files | Canonical identities | Hashed-name entries |
| --- | ---: | ---: | ---: |
| 219150 | 6 | 5 | 1 |
| 770100 | 7 | 4 | 3 |
| 975370 | 6 | 1 | 5 |
| 1091500 | 6 | 0 | 6 |
| 1145360 | 6 | 5 | 1 |
| **Total** | **31** | **15** | **16** |

The manifest records, for every captured file, the app id, the path relative to the Steam
root, the byte size and the SHA-256 digest. It commits **no image bytes and no derived
thumbnails**. `manifest.sha256` is the self-check digest of `manifest.tsv`
(`d561743a972469c6dcd10e5a7a70ffce35297cffc6ae4723d5e6ee3eff16ac25`).

The test materialises an **empty-file** tree from the manifest and asserts that
`ArtworkIdentity.TryReadCachePath`:

- extracts exactly the 15 canonical identities: app id plus one of the five documented
  names (`library_600x900.jpg`, `header.jpg`, `library_hero.jpg`, `logo.png`,
  `library_hero_blur.jpg`);
- accepts the observed content-addressed hashed-name forms — `<40 lowercase hex>.jpg` and
  `<40 lowercase hex>/<child>` — while returning **no identity**, because their kind and
  remote id are UNKNOWN and must not be inferred;
- rejects unknown names, unknown app ids, non-canonical decimal app directories, case and
  extension variants, traversal and malformed paths, and the achievement JSON path (which
  is metadata, not artwork).

App `1091500` is the hashed-name-only case: its cache has no canonical named file at all.
App `975370` shows the mixed case: `logo.png` plus hashed file and hashed-directory
children.

With this artifact, the artwork-identity gap recorded for Phase 5 exit criterion 1 is
covered: the identities are asserted against captured real-cache names, not only against a
synthetic fixture. Recording the gate verdict itself (`docs/port/gates/P5-pending.md`) is a
separate record made from merged `main`, following the existing assessment pattern.

## What remains UNKNOWN

- **Image bytes.** The capture stores sizes and digests, not content. No test decodes an
  image, compares bytes, or proves that artwork is available.
- **Steam CDN and remote mapping.** No URL, hashed Steam artwork id, remote app-to-artwork
  mapping or fallback source is derived. `artwork acquisition` still has no production
  remote mapper; only explicitly configured sources resolve.
- **Per-title availability.** Every canonical name is optional; absence in this capture is
  not evidence about another app or another host.
- **Writer behaviour.** The capture does not exercise cache writes, invalidation, or how
  Steam refreshes these files.

## Provenance and reproduction

The capture is described in `tests/fixtures/steam/real-artwork/README.md`, including the
exact `find`/`stat`/`sha256sum` commands, the fixed app set and the source cache
(the host Steam root's `appcache/librarycache`). The cache was only read. The
manifest is sorted by app id and then by byte-ordinal relative path, and `LC_ALL=C` makes
the sort reproducible on any host.

The test reads only the committed manifest. It performs no network I/O, does not read the
real Steam cache, has no skips, and tolerates the source cache being absent or changed.
