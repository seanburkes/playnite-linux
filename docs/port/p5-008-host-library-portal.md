# p5-008 — Host Steam library access through the desktop portal

- Unit: `p5-008` (criterion 6 evidence; the launch half of p5-008 remains separate)
- Date: 2026-11-19
- Scope: AppImage and Flatpak read a real host Steam library with narrow permissions. Discovery
  only; no launch, no Steam process, no network.

## Chosen route — portal FileChooser, one `steamapps` directory per library

The Flatpak sandbox keeps the unchanged manifest allowlist
(`wayland`, `fallback-x11`, `ipc`, `dri`, enforced by `packaging/verify-flatpak.py`). It therefore
has **no host filesystem access**. The only way to read the host Steam library is the desktop
portal:

1. `org.freedesktop.portal.FileChooser.OpenFile` with `directory=true` asks the user to select one
   `steamapps` directory. The dialog is the host's own chooser; the app is not granted the home
   directory or any other path.
2. The portal returns a document URI (`/run/user/<uid>/doc/<document-id>`). Only that directory is
   readable from inside the sandbox, and only by this app id.
3. The app reads `libraryfolders.vdf` from the selected directory. Each declared library path in
   that file is a *host* Steam root; its `steamapps` directory is the path the user must select
   next. The app prompts once per declared library and rejects a selection whose
   `org.freedesktop.portal.Documents.Info` host path is not exactly `<declared root>/steamapps`.
4. Grants (`host path` ↔ `document id` ↔ portal path) are persisted in the app's own config
   directory (`steam-portal-grants.json`, mode 0600, atomic replace). The next run re-resolves the
   stored document paths and reads the app set without prompting.
5. The imported app set is the set of `appmanifest_<appid>.acf` manifests in every granted
   `steamapps` directory, parsed with the merged p5-001 manifest parser.

The document portal keeps a granted document id only while the document exists. A deleted,
unmounted or revoked grant no longer resolves to a readable directory; the importer drops it and
asks the user to select that `steamapps` directory again. Nothing is re-granted implicitly.

### Why this route

- **Least authority.** The grant is one directory chosen by the user, revocable in the portal's
  own store, and limited to the requesting app id. The manifest allowlist does not change.
- **No host-command authority.** `flatpak-spawn --host` is not used and the manifest still has no
  `org.freedesktop.Flatpak` talk-name. The app never runs a host command.
- **Honest failure.** Without a grant the read fails; there is no silent fallback to the host
  filesystem.
- **Same code on both formats.** AppImage is unsandboxed and reads the library directly; the
  Flatpak uses the portal. Both produce the same app set through the same manifest parser.

### Considered and rejected

| Option | Why not |
| --- | --- |
| Add `--filesystem=home` or `--filesystem=host` | Violates the narrow allowlist and criterion 6's "narrow permissions"; `verify-flatpak.py` rejects it. |
| Add a fixed narrow `--filesystem=~/.local/share/Steam` | Still a broad, non-revocable package-level grant; breaks the accepted manifest and a Steam library can live anywhere. |
| `flatpak-spawn --host` to run `ls`/`cat` | Host-command authority; explicitly out of scope and blocked by ADR-020's scope for launch only. |
| `Documents.Add` with an fd passed out of band | The sandbox cannot open the host path to obtain the fd; the fd channel would be another privilege escalation. |
| Hard-coded `/run/user/<uid>/doc` scraping | Without the portal there is no grant; the mount point is discovered through `Documents.GetMountPoint`. |

### Fallback when the portal is genuinely unavailable

If `org.freedesktop.portal.FileChooser` is not present or the portal refuses the request, the app
reports `portal-unavailable` and imports nothing. The fallback is **an explicit user-supplied
narrow read-only filesystem override** for one `steamapps` directory at a time
(`flatpak override --user --filesystem=<path>/steamapps:ro io.github.playnite_linux.Playnite`),
after which the importer's direct mode can read that path. This is **not** used for the criterion
6 evidence and it is not done by the app: it changes effective permissions, so it is visible in
`flatpak info --show-permissions` and must be removed afterwards. What is lost without the
portal: no persisted per-directory grant, no user-driven selection, no revocation through the
portal, and the manifest's "no filesystem access" property no longer holds. The app never adds
that override itself.

## Scope and non-claims

- The portal proves **read/discovery**. It does not launch Steam, select native/Proton, or track a
  game process. Criterion 3 (Flatpak host launch through `flatpak-spawn`) remains deferred and is
  not touched here.
- The manifest and `packaging/verify-flatpak.py` are unchanged.
- AppImage is unsandboxed; its half of the evidence is a packaged-build discovery read, not a
  permission boundary.
- The desktop-portal route is user-driven: a run with no grant necessarily prompts. The evidence
  script drives the host chooser as the session user; the app itself only receives the grant.

## Evidence

Recorded in `docs/port/units/p5-008.ledger.d/` and in the pull request body: packaged artifact
names, sha256, real linux-ci run, effective permissions, the pre-grant negative control and the
post-grant app set.
