# Linux packaging (AppImage x64/ARM64; Flatpak x64)

AppImage packaging supports both **`linux-x64` (`x86_64`)** and **`linux-arm64` (`aarch64`)**;
Flatpak remains **`linux-x64`** only (tracked separately by `p8-002`). No signing or update
channel is provided. The icon is a packaging-only shell mark, not a final product asset.

`playnite --version` dispatches to the packaged CLI (expected `1.0.0.0`);
`playnite steam-library-import` dispatches to the packaged Steam portal importer;
`playnite launch <appid>` dispatches to the packaged launch command; other arguments
dispatch to the packaged desktop application (`--headless --smoke` exercises the headless
desktop startup smoke). `AppRun --steam-acceptance` dispatches to the packaged Steam
acceptance harness. All binaries are self-contained .NET **11.0.0**, published with the
**11.0.100** SDK from `global.json`.

RID-specific restore happens in a source export, never in the tracked lockfiles.
Assembly rejects dirty checkouts and reads both product and packaging inputs from
one exported commit; the commit and target RID are emitted in `source-commit.txt` and
`runtime-identifier.txt`.

Run `bash packaging/assemble.sh /absolute/new/scratch-directory [linux-x64|linux-arm64]`
from the checkout (defaults to `linux-x64`).
The result contains `AppDir/`, `payload/`, and the staged Flatpak manifest.
Assemble AppDir with **appimagetool 1.9.1** (`appimagetool-x86_64.AppImage` or
`appimagetool-aarch64.AppImage`) and an explicitly supplied **type2-runtime 20251108**
(`runtime-x86_64` or `runtime-aarch64`; SHA-256 checks are in `.github/workflows/packaging.yml`).
`verify-entry.py --arch <x86_64|aarch64>` verifies the desktop entry, launcher, icon,
and ELF machine headers of the packaged apphost binaries.
AppImages run without installation; `--appimage-extract-and-run` supports hosts without FUSE.

Flatpak uses **flatpak-builder 1.4.2** (Ubuntu `1.4.2-1build2`) and
**Flatpak 1.14.6** (Ubuntu `1.14.6-1ubuntu0.1`), and host **elfutils 0.190**
(Ubuntu `0.190-1.1ubuntu0.1`) for native library stripping. The manifest pins both
Freedesktop runtime and SDK to **25.08**. Their exact OSTree commits, the
bundled .NET runtime versions, tool versions and artifact byte sizes are recorded
in each successful workflow summary. `flatpak-runtime-lock.json` pins both runtime
refs to exact OSTree commits; CI deploys those commits before building and verifies
them again after the installed artifact smoke.

The host SVG export validator needs `librsvg2-common=2.58.0+dfsg-1ubuntu0.1`;
the workflow also installs `libgdk-pixbuf2.0-bin=2.42.10+dfsg-3ubuntu3.3` and
decodes the icon with GDK before building, so missing loaders or invalid icons fail early.

The Flatpak has display/IPC/GPU access only: no network, home, host filesystem,
or unrestricted D-Bus permissions. Per-app Flatpak storage remains available.
CI validates the exact manifest finish-args and the installed effective permissions,
including rejecting additional filesystem access or D-Bus policy sections.
CI builds a bundle, installs that bundle into the user installation, verifies its
installed desktop paths, runs the sandboxed version CLI and headless desktop smoke,
then uploads the bundle. AppImage CI similarly tests the actual assembled image on
`ubuntu-24.04` (`linux-x64`) and `ubuntu-24.04-arm` (`linux-arm64`),
not just the pre-packaging payload. Missing artifacts or failed smoke tests fail the job.

Local Flatpak execution may be prohibited by container user-namespace restrictions;
the Ubuntu CI sandbox smoke is authoritative. No broad sandbox permissions are added
to bypass such restrictions. Validation covers Ubuntu 24.04 (`ubuntu-24.04` for x64 and
`ubuntu-24.04-arm` for ARM64) with `--version` and `--headless --smoke`; neither format
has been tested across other Linux distributions or with native display-server
interaction as a packaged app.
