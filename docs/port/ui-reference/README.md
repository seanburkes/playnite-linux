# v10 UI references — p1-004

**Reference-only and non-blocking.** p1-004 is satisfied by the curated public
reference index in [`SOURCES.md`](SOURCES.md). That index is the deliverable: official
and public Playnite 10.x screenshot, manual and video links with slide numbers, section
anchors, image filenames and timestamps, retrieved 2026-11-14 UTC.

No images are captured, downloaded, committed or embedded here. The references are
**human comparison material only** — never a pixel gate, never a test oracle, never a
baseline for snapshot acceptance, and never a claim that the Linux port is visually or
behaviourally equivalent to v10. See
[`03-EXECUTION-PLAN.md` §Phase 1](../03-EXECUTION-PLAN.md) and
[`04-VERIFICATION.md` §6](../04-VERIFICATION.md).

The capture procedure below is **optional** and retained only in case the captain later
wants a genuine local image set from a frozen 10.64 Windows build. It is not required for
Phase 1 exit, it is not scheduled, and no Phase 1 exit item depends on it.

## Capture access audit (2026-11-14 UTC)

- This worker runs on Linux, not a Windows desktop.
- [`PORT-STATUS.md` Q2](../PORT-STATUS.md) identifies GitHub Actions Windows as the
  existing Windows environment. No interactive Windows desktop/capture endpoint
  or previously captured UI set is documented in this repository.
- [The successful oracle run](https://github.com/seanburkes/playnite-linux/actions/runs/43604176690)
  has one artifact named `legacy-oracle` (artifact listing checked with `gh-axi`).
  [Its workflow](../../../.github/workflows/legacy-oracle.yml) uploads build logs,
  test results/counts and manifest goldens; it neither launches the desktop UI nor
  captures images nor publishes a runnable desktop distribution. A successful
  Windows build/test run is not desktop capture access.
- Missing: an authorised interactive Windows session with screenshot export and a
  runnable frozen 10.64 desktop distribution with source/build provenance, or a
  genuine capture bundle from an authorised operator meeting the checklist below.
  No remote connection, credentials, new runner, or capture job was attempted.
- Since p1-004 is reference-only, this missing capture access is **not a blocker**. The
  audit is retained only for any future optional local capture.

The procedure below is **not exercised and is not required**. It is optional
preparation for a future local capture, not evidence and not a Phase 1 gate. No images
are expected in this directory under the current p1-004 decision.

## Source and boundaries

Use the genuine WPF Windows app from the frozen Playnite **10.64** source:
[`legacy/FROZEN.md`](../../../legacy/FROZEN.md) records upstream tag `10.64`,
commit `f122c55b`. Do not substitute a newer release, a design-time preview,
Avalonia output, Wine output, a web screenshot or a generated/mock image.

Read-only v10 references used for this procedure:

- `legacy/source/Playnite/App/CmdLineOptions.cs`: `--userdatadir`,
  `--nolibupdate`, `--forcedefaulttheme`.
- `legacy/source/Playnite/Settings/PlaynitePaths.cs`: user-data directory mapping.
- `legacy/source/Playnite.DesktopApp/Windows/MainWindow.xaml`: main shell,
  default 1360 × 900 WPF device-independent units.
- `legacy/source/Playnite.DesktopApp/Themes/Desktop/Default/Views/LibraryDetailsView.xaml`:
  games list and selected-game overview.
- `legacy/source/Playnite.DesktopApp/Windows/SettingsWindow.xaml`: settings shell,
  default 800 × 620 device-independent units, General initially selected.
- Existing parity rows: `ui-002`, `ui-010` in `docs/port/surface-parity.tsv`.

Do not write to `legacy/`, `oracle-lock/`, or any protected oracle paths. Keep
capture scratch/user data outside the checkout. Future reviewed PNGs and their
provenance may live in this directory, not `tests/golden/` or `tests/fixtures/`.
Do not wire these references into CI image comparison or snapshot acceptance.

## Optional human-run capture procedure (not required)

1. Obtain approval for an existing interactive Windows capture session and a
   parent-supplied runnable build from the frozen source. Record the full source
   revision, build command/run URL, dependency-lock hash, binary hashes and actual
   application file/product versions. If only an upstream release is available,
   stop for source/provenance approval; do not assume it matches the frozen build.
   This task does not provision remote access or request secrets.
2. Use a fresh disposable Windows account/session, no linked accounts or personal
   libraries, and a writable runtime copy outside the checkout. Close all other
   Playnite instances. Disable notifications. Use one monitor, preferably
   1920 × 1080 at 100% scaling, English (en-US), the built-in default desktop theme
   and default fonts. Record actual OS build, .NET Framework runtime, display
   resolution/scaling, ClearType and rendering settings; these affect pixels.
3. In PowerShell, set `$exe` to the absolute path of that runtime's
   `Playnite.DesktopApp.exe`. Set `$data` to a **new, empty, absolute scratch path**
   outside the runtime and checkout, for example `C:\PlayniteCapture\user-data`.
   Launch with the source-supported arguments (do not invent a portable flag):

   ```powershell
   & $exe --userdatadir $data --nolibupdate --forcedefaulttheme
   ```

   Verify that the session is empty before adding records. Skip all integration
   setup/imports, metadata downloads and sign-ins. Do not enable auto-updates,
   extensions or third-party themes. Never point this run at an existing library.
4. Using the app's manual Add Game UI, add exactly these synthetic records. Do not
   import a database or edit serialized library bytes. Leave every unspecified
   field empty/default, including actions, installation paths, links, artwork and
   playtime. Do not download cover art. Record any UI-imposed deviations.

   | Name | Description | Genre | Release date |
   | --- | --- | --- | --- |
   | Reference Alpha | Synthetic library entry Alpha. | Adventure | 2020-01-02 |
   | Reference Beta | Synthetic library entry Beta. | Puzzle | 2021-03-04 |
   | Reference Gamma | Synthetic library entry Gamma. | Strategy | 2022-05-06 |

5. Set sort to Name ascending, grouping off, clear search/filters, and select
   Reference Alpha. Keep the default panel arrangement. Use a non-maximised main
   window approximately 1360 × 900 at 100%; record the actual bounds, not an assumed
   size. Capture these scenarios in order after layout has settled:

   | File | Scenario |
   | --- | --- |
   | `main-library.png` | Grid view, all three synthetic records visible, no menus/dialogs open. Missing-art presentation is intentional. |
   | `details.png` | Switch to Details view; Reference Alpha selected, list and its overview/description visible, scroll at top. |
   | `settings.png` | Open Settings from the main menu; General selected, default-sized dialog fully visible, no changed options. |

6. Capture the active application window using Windows Snipping Tool window mode
   (or Alt+PrintScreen, then save the clipboard image as PNG). For Settings capture
   the complete dialog. Record the tool/version, capture mode, window/client bounds
   and any included border/shadow. Keep the cursor out of the content. Save lossless
   PNGs without resizing, retouching, compositing or annotations. If personal data
   appears, discard that capture and repeat in a clean synthetic session; do not
   publish it or hide it with image edits.
7. Close Settings without saving changes. Review all three images for authenticity,
   privacy, completeness and scenario match. Copy only the reviewed PNGs and a
   completed `provenance.md` into this directory in a follow-up change. Do not copy
   the user-data directory, logs, account details or raw system diagnostics.

## Provenance required with the images

Create `provenance.md` only when real captures exist. Record actual observations;
do not fill unknowns with the preferred environment above. Include:

- Operator identifier, capture UTC timestamp, authorised capture environment
  identifier (no credentials/private endpoint), and capture method/tool version.
- Upstream version/tag/commit and full port-repository revision; build command or
  full CI run URL; SHA-256 of the dependency lock and runnable binaries
  (`Playnite.DesktopApp.exe`, `Playnite.dll`). Actual executable file/product
  versions and the About-dialog version observed by the operator.
- Windows edition/version/build and .NET Framework runtime version; display pixel
  resolution, scaling/DPI, locale, theme, fonts/ClearType, software/hardware render
  mode, and remote-session scaling/compression if applicable.
- Exact launch command using a public synthetic path, the synthetic-data recipe
  above with any deviations, and all view/filter/sort/selection/scroll settings.
- Per image: filename, scenario, UTC timestamp, PNG width × height in **pixels**,
  window/client bounds, crop/border convention, SHA-256, and confirmation that the
  image is an unaltered capture of frozen v10 on Windows using synthetic data only.
- Human privacy/authenticity reviewer and outcome. Human comparison is not a
  claim of machine-verifiable provenance or a substitute for behavioural tests.

The operator can compute hashes with PowerShell `Get-FileHash -Algorithm SHA256
-LiteralPath <path>` and read each PNG's pixel dimensions in Windows Properties →
Details. An adversarial reviewer must open every PNG, check that it decodes, compare its
dimensions/hash with provenance and inspect the stated scenario. Hashes establish
file identity, not authenticity; source/build and operator evidence are required.

## Optional capture completion checklist

This checklist applies **only** to the optional local capture. It is not a Phase 1
gate, and no item here is required for p1-004 or Phase 1 exit.

- [ ] Genuine `main-library.png`, `details.png`, `settings.png` captured and decoded.
- [ ] Actual version/runtime/dimensions/scenarios and hashes recorded in provenance.
- [ ] Synthetic-only/privacy and authenticity review complete.
- [ ] Two adversarial blind diff-only reviews arranged by firstmate; reviewers do
  not edit. The owner decides the merge.
- [ ] Queue/ledger updated with genuine capture evidence. No image or behavioural
  verification is claimed by this document.
