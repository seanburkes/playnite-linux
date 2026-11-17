# p5-002 synthetic discovery fixtures

Every file here was authored from `docs/port/08-STEAM-SPEC.md` §§1–2 and the approved
p5-001 parser decision table, not captured or copied from any Steam install. All paths,
appids and sizes are invented; `${ROOT}`, `${SECOND}`, `${LINK}`, `${LIBRARY}` and
`${MISSING}` are template tokens that `SteamDiscoveryTests` substitutes with temporary
directories before writing the file. No real corpus, account data or protected fixture was
read or added.

| File | Spec-derived coverage |
| --- | --- |
| `libraryfolders.modern.vdf` | Two modern libraries, duplicate appid across libraries (first wins), 64-bit sizes, optional fields. |
| `libraryfolders.legacy.vdf` | Legacy `"1" "/path"` index form (§2.2). |
| `libraryfolders.duplicates.vdf` | Root library, a symlink alias that resolves to the same realpath, an unmounted library whose apps stay visible, and a non-absolute entry that is skipped. |
| `libraryfolders.unavailable.vdf` | Root plus an unmounted removable-drive library with app ids (the observed §2.1 shape, synthesised). |
| `libraryfolders.no-steamapps.vdf` | A library path that exists but has no `steamapps` directory. |
| `libraryfolders.invalid-apps.vdf` | Invalid optional values and a malformed apps map: the library survives with an empty apps map and every problem is reported. |
| `libraryfolders.malformed.vdf` | Unbalanced braces; the file is skipped atomically and the root library is kept. |

Directory-shape branches cannot be expressed as committed files, so `SteamDiscoveryTests`
materialises them in one temporary directory and asserts the result:

- explicit `$STEAM_ROOT` override;
- the home `.steam/steam` and `.local/share/Steam` paths as one symlinked realpath (duplicate candidate);
- Flatpak root `.var/app/com.valvesoftware.Steam/.local/share/Steam` under the test home;
- candidate present without `steamapps`, candidate that is a file, symlink loop;
- `steamapps` symlinked outside the candidate root or the library root (never followed);
- duplicate libraries by realpath;
- `config/libraryfolders.vdf` fallback when `steamapps/libraryfolders.vdf` is absent;
- oversized and unreadable library files;
- read-only snapshot: discovery leaves the synthetic tree byte-identical.

`SHA256SUMS` pins the exact fixture bytes; a test fails on drift or an unlisted `.vdf`.
