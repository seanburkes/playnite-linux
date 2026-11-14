# Oracle lock

The frozen v10 oracle depends on `Playnite.Deps` 10.56.0, a binary NuGet package containing
DiscordRPC, HtmlRenderer, SQLNado, SharpCompress, and `Windows.winmd`. It is served from
`nuget.playnite.link`, a mutable third-party feed.

This directory pins the package by SHA-256 and provides a fetch script. It does **not** vendor
the binaries, because their redistribution terms have not been reviewed. If the feed
disappears, the fallback is a checked-in copy after a license review, or a Windows machine with
a cached package.

```sh
./oracle-lock/fetch.sh
```

The script downloads to `oracle-lock/packages/`, verifies the hash, and exits non-zero on a
mismatch. CI uses the same script for the Windows oracle job.

## Why not just trust the feed?

The oracle is only useful if it is reproducible. A package that can change under the same
version is not a pinned dependency. The hash is the contract; the feed is a transport.

## License review

Before vendoring or redistributing any of these binaries, confirm each upstream license
(DiscordRPC, HtmlRenderer, SQLNado, SharpCompress) and the terms of the Playnite.Deps package.
Until then, fetch-and-verify only.
