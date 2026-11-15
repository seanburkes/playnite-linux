# Phase 2 Linux packaging smoke (x86-64 only)

These artifacts contain the current Avalonia desktop **shell** plus the version CLI,
not the real application payload. No signing, update channel, or ARM64 support is provided.
The icon is a packaging-only shell mark, not a final product asset.

`playnite --version` dispatches to the packaged CLI (expected `1.0.0.0`);
other arguments dispatch to the packaged desktop shell. Both are self-contained
.NET **11.0.0**, published with the **11.0.100** SDK from `global.json`.
RID-specific restore happens in a source export, never in the tracked lockfiles.

Run `bash packaging/assemble.sh /absolute/new/scratch-directory` from the checkout.
The result contains `AppDir/`, `payload/`, and the staged Flatpak manifest.
Assemble AppDir with **appimagetool 1.9.1** and an explicitly supplied
**type2-runtime 20251108** (SHA-256 checks are in the workflow).
AppImages run without installation; `--appimage-extract-and-run` supports hosts without FUSE.

Flatpak uses **flatpak-builder 1.4.2** (Ubuntu `1.4.2-1build2`) and
**Flatpak 1.14.6** (Ubuntu `1.14.6-1ubuntu0.1`), and host **elfutils 0.190**
(Ubuntu `0.190-1.1ubuntu0.1`) for native library stripping. The manifest pins both
Freedesktop runtime and SDK to **25.08**. Their exact OSTree commits, the
bundled .NET runtime versions, tool versions and artifact byte sizes are recorded
in each successful workflow summary. Runtime branch updates are not byte-reproducible;
the summary records precisely which commits were tested.

The host SVG export validator needs `librsvg2-common=2.58.0+dfsg-1ubuntu0.1`;
the workflow also installs `libgdk-pixbuf2.0-bin=2.42.10+dfsg-3ubuntu3.3` and
decodes the icon with GDK before building, so missing loaders or invalid icons fail early.

The Flatpak has display/IPC/GPU access only: no network, home, host filesystem,
or unrestricted D-Bus permissions. Per-app Flatpak storage remains available.
CI builds a bundle, installs that bundle into the user installation, verifies its
installed desktop paths, runs the sandboxed version CLI and headless desktop smoke,
then uploads the bundle. AppImage CI similarly tests the actual assembled image,
not just the pre-packaging payload. Missing artifacts or failed smoke tests fail the job.

Local Flatpak execution may be prohibited by container user-namespace restrictions;
the Ubuntu CI sandbox smoke is authoritative. No broad sandbox permissions are added
to bypass such restrictions. Neither format has yet been tested across Linux distributions
or with native display-server interaction as a packaged app.
