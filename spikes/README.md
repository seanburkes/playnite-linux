# Spikes

Time-boxed experiments that answer a Phase 1 question. They are not production code and do not
ship.

## `litedb-net11`

Question: can the unmaintained LiteDB 4.1.4 reader used by v10 run on `net11.0` on Linux?

Result (2026-11-14): **yes** for basic database create/open/insert/query/collection-name
enumeration. It restores and runs on `net11.0` with a critical-severity NuGet advisory, which
is exactly why the importer is a separate, sandboxed process and the main app never links it.

Still to verify with a real v10 fixture:

- v10's custom `BsonMapper` mappings and per-collection `.db` layout;
- the `ItemCollection` open/load/repair path;
- file locking and WAL behaviour under the importer's temp copy.

Run:

```sh
cd spikes/litedb-net11
dotnet run -c Release
```

## `avalonia-headless`

Question: can a Linux test process drive Avalonia's headless platform with real Skia rendering,
capture a deterministic frame, and exercise the managed automation-peer and keyboard semantics the
port depends on?

Result (2026-11-14): **yes** for a synthetic 640×480 window: three xunit v3 facts pass with zero
skips (three byte-identical Skia captures, a managed automation-peer tree, and Tab/Space keyboard
activation). The PNG and automation-tree artifacts are described in
[`avalonia-headless/README.md`](avalonia-headless/README.md); they do not prove real-window,
AT-SPI/Linux accessibility, Windows, or Playnite-shell parity.

Run:

```sh
rm -rf /tmp/playnite-spike-evidence
DISPLAY= WAYLAND_DISPLAY= SPIKE_EVIDENCE_DIR=/tmp/playnite-spike-evidence \
  dotnet run --project spikes/avalonia-headless/HeadlessSpike.csproj -c Release
```
