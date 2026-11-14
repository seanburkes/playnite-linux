# Steam subsystem specification (v1)

Steam is **greenfield** in this repo: v10's Steam library lives in a separate built-in plugin
that is not in `legacy/`. This document is the behavioural contract for v1, and the recorded
artifact corpus in `tests/fixtures/steam/` is its oracle.

## 1. Roots

Resolve, in order, the first root that exists and contains `steamapps`:

1. `$STEAM_ROOT` (explicit override)
2. `~/.steam/steam`
3. `~/.local/share/Steam`
4. `~/.var/app/com.valvesoftware.Steam/.local/share/Steam` (Flatpak)

Additional library folders come from `steamapps/libraryfolders.vdf`. A library entry is usable
only if `<path>/steamapps` exists.

## 2. Parsers

### `libraryfolders.vdf`
Valve KeyValues. Accept both the newer object form (`"0" { "path" "..." }`) and the legacy
index form (`"1" "/path"`). Ignore unknown keys. Reject a file with unbalanced braces or a
path that escapes the declared root; report it, do not crash.

### `appmanifest_<appid>.acf`
Valve KeyValues. Required fields: `appid`, `name`, `installdir`, `StateFlags`. Optional:
`SizeOnDisk`, `LastUpdated`, `buildid`, `UserConfig`. `StateFlags` bit 4 (`0x4`) means
fully installed; other states are reported, not silently treated as installed.

### `shortcuts.vdf`
Binary VDF (non-Steam shortcuts). **Not v1.** Listed here so the format is not mistaken for
text VDF; a later increment may parse it.

## 3. App model

```csharp
sealed record SteamApp(
    uint AppId,
    string Name,
    string InstallDir,
    string LibraryPath,
    bool IsInstalled,
    bool HasProtonPrefix,
    ulong SizeOnDisk,
    DateTimeOffset? LastUpdated);
```

Rules:
- `AppId` comes from the file name and the `appid` field; a mismatch is a warning, not a
  silent choice.
- Paths are absolute POSIX paths; no separator normalisation beyond `Path.GetFullPath`.
- A missing install directory with `StateFlags` installed is `Broken`, not `Installed`.
- Duplicate app ids across libraries: the first library in `libraryfolders.vdf` wins; report
  the duplicate.

## 4. Artwork

1. `steamapps/librarycache/<appid>_library_600x900.jpg` (portrait)
2. `steamapps/librarycache/<appid>_header.jpg` (landscape)
3. Steam CDN `https://cdn.cloudflare.steamstatic.com/steam/apps/<appid>/header.jpg`
4. Placeholder.

Cache by app id + source mtime/size; store under `${XDG_CACHE_HOME}/playnite/steam/`.
Never block the UI thread on network; failures degrade to the next source.

## 5. Launch

```
steam -applaunch <appid>
```

- Use an argument list, never a shell string.
- If `steam` is not on `PATH`, try the host path:
  `flatpak-spawn --host steam -applaunch <appid>`.
- If Steam itself is installed as a Flatpak, try:
  `flatpak-spawn --host flatpak run com.valvesoftware.Steam -applaunch <appid>`.
- If that fails, try `xdg-open steam://rungameid/<appid>`.
- A launch command returning 0 does **not** mean the game started; the session tracker decides.

## 6. Session tracking

The tracker observes the process tree. Strategies, in order:

1. **Direct child tree:** Steam may exec the game under the same process group.
2. **Proton/compatdata marker:** watch `steamapps/compatdata/<appid>/pfx` and the
   `SteamLinuxRuntime`/`pressure-vessel` processes for the app id.
3. **Process name heuristic:** the `installdir` basename or executable name from the manifest.
4. **Manual stop:** the user can mark the game stopped; this is always available.

Rules:
- A session starts only when a matching process is observed; not when `steam -applaunch` exits.
- A session ends when the process tree is gone for a debounce window (e.g. 5 s), or the user
  stops it.
- Playtime is monotonic; a restart does not reset it.
- Cancellation at any await leaves no tracked handle and no orphan watcher.
- Flatpak uses host PID visibility (`flatpak-spawn --host`) or a minimal host helper.

The TLA+ model is `docs/formal/GameSession.tla`; implementation tests cite its invariants.

## 7. Failure modes

| Failure | Behaviour |
| --- | --- |
| No Steam root | Empty library with a clear "Steam not found" state; fresh install still works |
| Unreadable VDF | Report the file and line; skip that library, keep others |
| App manifest missing | Skip the app; report `missing-manifest` |
| Install dir missing | Mark `Broken`; do not launch |
| Launch command fails | Surface the error; do not create a session |
| Game exits immediately | Session ends; report a short-session warning |
| Steam not running | Launch may start Steam; if it does not, surface the error |
| Multiple Steam roots | First valid root wins; report the others |

## 8. Fixtures and acceptance

Checked in:
- synthetic root with two libraries, native and Proton apps, missing install, broken manifest;
- redacted real `libraryfolders.vdf` / `appmanifest_*.acf` from a real install;
- `librarycache` images and a captured app-details JSON corpus.

Acceptance:
- fixture app set matches the expected list exactly;
- real install launches and stops a native and a Proton game;
- Flatpak build launches and tracks a host Steam game;
- no orphan processes after cancel/teardown;
- no network on the UI thread.

## 9. Out of scope for v1

- Non-Steam shortcuts (`shortcuts.vdf`), Steam family sharing, remote play, cloud saves.
- Steam Web API authentication, achievements, friends, workshop.
- SteamGridDB or other artwork providers.
- Launching a game when Steam is not installed (e.g. raw Proton invocation).
