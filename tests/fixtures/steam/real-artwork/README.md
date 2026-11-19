# Real Steam artwork cache — identity manifest (no image bytes)

Captured on 2026-11-19T03:40Z from one Linux host with a native Steam client. This
directory records **only names, byte sizes and SHA-256 digests** of files Steam had already
written under `<root>/appcache/librarycache/<appid>/`. It contains **no image bytes and no
derived thumbnails**, because that artwork is copyrighted and no redistribution licence is
recorded.

The capture exists to prove one narrow thing: the artwork **identities** (app id plus the
documented file name) are the ones a real Steam cache uses. It does **not** prove that
artwork is available, that a remote URL exists, or that any byte equals what Steam would
serve.

## Scope: identity only

- **UNKNOWN and out of v1 scope:** image bytes, their artistic content, the Steam CDN
  header URL, content-addressed remote ids, and any mapping from an app id to a remote
  artwork id.
- **Proven by this manifest:** the local names and app-directory layout observed for a
  fixed set of installed apps, including the content-addressed hashed-name form.
- The product still resolves artwork only from explicitly configured sources. Nothing in
  this capture may be used to derive a URL, a remote id, or a fallback source.

## Source and fixed app set

- Source Steam root: `$HOME/.local/share/Steam` (not a symlink on the capture host).
- Source cache: that root's `appcache/librarycache` directory.
- Capture date: 2026-11-19T03:40Z.

| App id | Why included | Observed cache shape |
| --- | --- | --- |
| 219150 | required installed app | five canonical names plus one `<40-hex>.jpg` |
| 770100 | required installed app | four canonical names plus hashed file and hashed-directory children |
| 975370 | required installed app | `logo.png` plus hashed file and hashed-directory children |
| 1091500 | installed app whose cache has **only** hashed-name entries | six files, every one under or named by a 40-hex hash |
| 1145360 | required installed app | five canonical names plus one `<40-hex>.jpg` |

App `1091500` is the "hashed-name only" case: it has no `header.jpg`,
`library_600x900.jpg`, `library_hero.jpg`, `logo.png` or `library_hero_blur.jpg` directly
under the app directory. `LC_ALL=C` makes the collation locale-independent, so the sort is
byte-ordinal on every host.

`manifest.tsv` is sorted by app id (numeric) and then by relative path (ordinal). It has a
stable header:

```
appid	relative_path	bytes	sha256
```

Paths are relative to the Steam root and always use `/`. Every entry is a regular file
observed under the app directory; no directory is recorded and no file may be a symlink.

## Exact capture commands

The manifest was produced with these commands, from the repository root. They only read the
cache. `STEAM_ROOT` and `CACHE` must point at the host Steam install being captured.

```sh
STEAM_ROOT="$HOME/.local/share/Steam"
CACHE="$STEAM_ROOT/appcache/librarycache"
OUT="tests/fixtures/steam/real-artwork/manifest.tsv"

{
  printf 'appid\trelative_path\tbytes\tsha256\n'
  for app in 219150 770100 975370 1091500 1145360; do
    find "$CACHE/$app" -type f -print0 | LC_ALL=C sort -z |
      while IFS= read -r -d '' file; do
        printf '%s\t%s\t%s\t%s\n' \
          "$app" "${file#"$STEAM_ROOT"/}" \
          "$(stat -c %s "$file")" "$(sha256sum "$file" | cut -d' ' -f1)"
      done
  done
} > "$OUT"
```

The self-check hash is the SHA-256 of `manifest.tsv` itself, stored in `manifest.sha256`:

```sh
cd tests/fixtures/steam/real-artwork
sha256sum manifest.tsv > manifest.sha256
sha256sum -c manifest.sha256
```

Current self-check:

```
d561743a972469c6dcd10e5a7a70ffce35297cffc6ae4723d5e6ee3eff16ac25  manifest.tsv
```

Re-running the capture on the same cache must reproduce `manifest.tsv` byte for byte:

```sh
# after re-running the capture block with OUT=/tmp/manifest.tsv
diff -u tests/fixtures/steam/real-artwork/manifest.tsv /tmp/manifest.tsv
```

The cache was never modified. The source files' digests were read again after the capture
and matched the recorded values; only the source `appcache/librarycache` subtrees were read.

## What the test proves

`tests/Playnite.Core.Tests/Artwork/RealArtworkIdentityTests.cs`:

1. Verifies `manifest.sha256` is the SHA-256 of `manifest.tsv`, and that the manifest is
   well-formed, sorted, duplicate-free, and contains no file type other than the three
   documents in this directory.
2. Materialises an **empty-file** tree from the manifest in a temporary directory (the
   recorded sizes are not reproduced; sizes and digests are only checked in the manifest)
   and calls `ArtworkIdentity.TryReadCachePath` on every materialised path.
3. Asserts the extractor yields exactly the recorded canonical identities — app id plus
   one of the five documented names — and no identity for the hashed-name entries.
4. Asserts unknown names, unknown app ids, malformed paths and case/extension variants are
   rejected.

The test never reads the real Steam cache, performs no network I/O, and has no skips.

## What this corpus does not cover

- Any image content, decoding, or byte equality.
- Steam CDN or remote artwork ids/mapping.
- Per-title artwork availability (every documented name is optional).
- Writer behaviour, cache invalidation, or file modification times.
- Windows Steam roots or `steamapps/librarycache` (the spec records that layout as absent on
  the host).
