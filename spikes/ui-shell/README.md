# `ui-shell` — standalone library-shell evidence

Question: can Avalonia 12.1.6 on Linux express ADR-013's library grid, search/filter and
selection-driven details using ADR-007's resource-dictionary pipeline, and render it
reproducibly without a display?

Result (2026-11-15): **yes for this spike**. Nine xunit v3 facts pass, zero skips. No
Playnite product assemblies are referenced. All six titles, playtimes, dates, installed
flags and geometric covers are **invented fake data**, not real game metadata/artwork.
There is no animation code, networking, timer, DB, launching, settings or packaging.
This spike is **not in the product solution and is not executed by the Linux CI job**.
Its nine facts and the cross-process runner are manually invoked evidence, not CI coverage.

## Reproduce

From the repository root, with .NET 11 and the same native Skia prerequisites as
`spikes/avalonia-headless/`:

```sh
evidence="$(mktemp -d /tmp/opencode/ui-shell.XXXXXX)"
env -u DISPLAY -u WAYLAND_DISPLAY SPIKE_EVIDENCE_DIR="$evidence" \
  dotnet run --project spikes/ui-shell/UiShellSpike.csproj -c Release
```

`SPIKE_EVIDENCE_DIR` is mandatory and must be **new or empty**. A reused/nonempty
directory fails before window creation; do not overwrite previous evidence. Both display
variables must be absent or empty (the command above actually unsets them). Expect
`Total: 9, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`.

To automate **two separate processes**, restore the locked packages, then run:

```sh
dotnet restore spikes/ui-shell/UiShellSpike.csproj --locked-mode
comparison="$(mktemp -d /tmp/opencode/ui-shell-processes.XXXXXX)"
python3 spikes/ui-shell/compare_processes.py "$comparison"
```

The runner unsets both display variables, starts the full suite twice in separate
processes, requires at least nine tests per process with zero errors/failures/skips/not-run,
and compares `render-1.png` against `render-2.png` and `render-3.png` within each process.
It then compares `process-1/render-1.png` against `process-2/render-1.png` byte-for-byte
and checks each process's first PNG hash against its `render.json` hash. Together those
comparisons establish equality of all six captures; it does not perform nine cross-pairs.
It also compares `library-1200.semantic.json` byte-for-byte across the two processes
and records each tree's SHA-256 and `treeByteIdentical` in the report.
It writes `process-{1,2}/` evidence, `process-{1,2}.log`, and `comparison.json`. It fails
on mismatching bytes/hashes; failed runs retain their logs for investigation. The runner
also requires a new or empty directory and never overwrites previous evidence.

Packages follow the existing headless spike: the Avalonia family is explicitly pinned
to the central policy's 12.1.6, and xunit v3 to 4.0.3, with a checked-in lock file.
Spikes are excluded from central package management; no central pins were changed and
no package beyond the existing headless spike's package set was added.

Artifacts:

- `render-1.png`, `render-2.png`, `render-3.png`: independent 1200×800 windows,
  scaling 1, bundled Inter, real Skia rendering (`UseHeadlessDrawing = false`).
- `render.json`: dimensions, scaling, PNG byte length and SHA-256, decoded RGBA pixel
  SHA-256, distinct colour count, repetition count and renderer/font metadata.
  `sha256` hashes the **PNG file**, not just the pixel buffer.
- `layout-640.png`, `layout-1200.png`: 640×800 and 1200×800 layout probes. At the
  narrow breakpoint details move below the grid and cover height becomes compact.
- `library-1200.semantic.json`: rendered control-tree projection for the initial wide
  shell, emitted beside the PNGs and compared with the committed baseline below.

Observed reference PNG: **69,092 bytes**, **1200×800**, 4,642 distinct RGBA colours.

```text
SHA-256: f5bde1e9872e6a44ee7fb4d1a1dd409cdb577b7e6c0588314c5096f36a5d10e5
```

**Evidence provenance:** no PNGs or evidence directories are tracked in Git. The earlier
external `run-1/` is a pre-final, uncommitted source-state capture (before `bfea250`),
**67,175 bytes**, SHA-256
`e37599d7a767839e020d5c7eb5a896b25f1ce320f71da040103fe234377527d6`.
It lacks the selected-card border and predates compact narrow covers; it is **not**
byte-identical to final captures and is not part of the final evidence set. There is no
source commit for that intermediate state. External `run-2/` and `run-3/` were the two
final-source runs compared manually with `cmp` (exit 0), both matching the 69,092-byte
digest above. The original xunit fact proved three windows **within one process**, not
cross-process identity; the initial cross-process observation was manual, not test-proven.

Review follow-up evidence is fresh in the external evidence directory `review-final-3/`:
the automated runner compared `render-{1,2,3}.png` within each process and then
`render-1.png` between `process-1/` and `process-2/`. All six match the
69,092-byte digest above, and each process passed all six facts with zero skips.
`comparison.json` records the separate-process result. Only these named final captures
are claimed identical; the pre-final `run-1/` is explicitly excluded. This is not a
promise of identical output across CPU architectures, native Skia versions, OS/font
rasterizers or future package versions. Reference hashes are evidence,
**not checked-in pixel gates**.

## Semantic tests

| xunit fact | Assertions |
| --- | --- |
| `Rendered_library_tree_matches_committed_semantic_baseline` | Baseline-independent checks for a nonempty tree, header/fake-data caption, six library cells, count, search placeholder, details heading/fields/caption; recursive comparison with the committed structural baseline. |
| `Semantic_comparison_rejects_structural_drift_and_only_tolerates_one_pixel` | Mutation checks reject a missing cell, renamed control/automation name, changed type/text/value/enabled/visibility and a lost details panel; bounds drift of 1 pixel passes, 1.001 fails. |
| `Required_library_semantics_rejects_shorter_self_matching_trees` | Removing a required header/caption, one library cell, count, placeholder, details panel or field yields a tree that matches itself but fails the independent required-content assertions. |
| `Grid_binds_every_fake_cover_title_playtime_and_last_played` | Six catalog items and six realized cells; each cell has its bound title, playtime, last-played text and geometric-cover motif; bound count reads six. |
| `Search_and_installed_filter_narrow_the_bound_grid_and_clear_empty_details` | Editing the actual search TextBox filters case-insensitively; checkbox combines installed filtering; zero results clear details and count reads zero; clearing search restores four installed entries and bound count reads four. |
| `Selecting_a_cell_updates_all_bound_details` | Changing ListBox selection updates the model and visible title, playtime and last-played details. |
| `Narrow_and_wide_layouts_keep_cells_search_and_details_inside_the_frame` | Both sizes retain six realized cells, positive in-frame search/grid/details bounds, multiple columns, all cell text inside the grid viewport, and narrow details below the grid; asserts actual cover heights of 100/138 at narrow/wide sizes and captures both. |
| `Replacing_a_theme_dictionary_updates_resource_driven_controls` | Replaces the merged colour dictionary and adds a spacing dictionary override; checks live window/card backgrounds, actual cover fill/accent brushes, and cover style/layout height changing from 138 to 110, then restores both dictionaries. No product theme loader is implied. |
| `Skia_captures_are_nonblank_decodable_and_byte_identical` | Three fresh windows yield identical PNG bytes and RGBA buffers; PNG decode preserves dimensions/pixels; >32 distinct whole-frame colours rejects blank output. |

The colour-count threshold is only a **loose full-frame nonblank guard**. There is no
legacy-image comparison, snapshot acceptance, or pixel-parity threshold. Semantic
assertions, not screenshot resemblance, prove the interactions and tested layout.

## Committed semantic baseline

`Baselines/library-1200.semantic.json` is a **structural reference for this fake-data
shell**, captured from the shown, laid-out, Skia-rendered 1200×800 window at scale 1.
It is not generated from the catalog alone. Its 46 nodes include the window/header,
named search and installed filter, count, library list, six realized cells with six
cover motifs and all title/playtime/last-played text, and the details panel with its
initial selection. This JSON is the committed companion to the external screenshot
evidence; PNG files themselves remain untracked.

`SemanticTree.cs` projects the actual visual tree in depth-first visual-child order:
Window, TextBlock (including AccessText), TextBox, CheckBox, ListBox, ListBoxItem,
CoverArt, and the named Details container. Other layout/template wrappers are flattened,
preserving child order and semantic ancestry. Text templates are included, including
the hidden floating search placeholder (marked `Visible: false`). This is deliberately
a **semantic control-tree projection, not the Avalonia automation-peer tree**.
`Type` is the runtime control type; `Name` is the control name, `AutomationName` the
explicit attached automation name (empty if absent, not an inferred accessible name).
Each node also records effective enabled/visible state, meaningful text/value, and
window-relative `[x, y, width, height]` bounds rounded to 0.001 logical pixels. Values
include search text, checkbox state and cover motif; no paths, clocks or object IDs occur.

Comparison requires exact types, names, text/values, states, hierarchy, ordering and
child counts. **Only each bounds component has an inclusive ±1 logical-pixel tolerance**
(one device pixel at the pinned scale), allowing small pixel-derived layout rounding;
this can also accept a **real one-pixel layout drift**, not only rounding noise.
There is no structural tolerance. The comparator's mutation test rejects a missing
cell, renamed control/automation name, changed control type, text/value or effective
state, and a lost details panel. Separate required-content assertions pin the header,
fake-data caption, six cells inside the named library, count, visible search placeholder,
and the details heading/title/playtime/last-played/caption without reading the baseline.
The omission test proves these assertions reject shortened trees even when each tree
is compared against an identical replacement baseline.
There is no automatic baseline acceptance/update mode: the test emits its actual tree
to the new external evidence directory before comparing, never overwriting the baseline.
Any intentional baseline change needs explicit review of that emitted diff.

Two fresh process runs each passed all nine facts with zero skips; emitted trees were
byte-identical to one another and to the committed baseline, SHA-256
`886a28797c9b0dd4d12271d26fac472513f7370703830ed8ce43b30b03226b5e`.
This byte-identity claim is evidenced by the matching recorded SHA-256 values for both
processes and the baseline, plus the runner's direct cross-process byte comparison;
the structural comparison itself permits the bounds tolerance and does not assert
byte identity. The initial external `bootstrap/` tree capture was **pre-final** (before
the visibility field and final serialization format), is not the committed baseline,
and is excluded from this equality claim. The original eight-fact final-source evidence
was the two runs under external `verified-processes/`; the nine-fact hardening runs are
under external `harden-processes/`. Both record their hashes in `comparison.json`.
All six PNG captures retained the existing reference hash above.

This proves the initial fake-data shell's selected structure and bound content, not
the real Playnite product UI, v10 parity, every visual wrapper, narrow-window tree
parity, focus/keyboard behavior, automation-peer correctness or an OS accessibility
bridge. **Accessibility remains deferred for v1**; this baseline does not change that
scope or claim screen-reader/AT-SPI support. The existing interaction/layout tests and
screenshots provide separate evidence, not a substitute for product integration.

## Proposed ADR-007 theme layout

```text
App.axaml                         # merges dictionaries and includes styles
Themes/Default/
  theme.yaml                      # proposed manifest, apiVersion: 0 (experimental)
  Colors.axaml                    # named palette values
  Brushes.axaml                   # dynamic brushes referencing palette values
  Spacing.axaml                   # padding, gaps, card sizing, breakpoint, selection border
  Typography.axaml                # bundled font and named type sizes
  Controls.axaml                  # control styles, compact/selected states
  Templates.axaml                 # GameCard DataTemplate, compiled Game bindings
LibraryWindow.axaml               # composition and compiled LibraryModel bindings
LibraryWindow.axaml.cs            # view-only responsive placement
LibraryModel.cs                  # fake catalog, filtering and selection notifications
CoverArt.cs                      # deterministic code-drawn placeholder art
```

This is real Avalonia resource resolution, not per-control hardcoded styling: App loads
merged dictionaries, brushes use dynamic palette resources, views consume named tokens,
and styles/templates live separately. The dictionary-swap test exercises the live
pipeline. Fluent supplies baseline control templates; the separate template file defines
the card data template, not a complete replacement Fluent control-template suite.
Card titles intentionally inherit the window's theme-provided `BodyFont` and `BodySize`
with default text styling; there is no dedicated title style. The unused `title` class
was removed rather than introducing a separate typographic role for this spike.

**Placeholders / not implemented:** `theme.yaml` is a proposed documented manifest, not
parsed at runtime; `apiVersion: 0` is experimental, not a product API commitment. Arbitrary
user-directory discovery/loading, validation, compatibility/version negotiation, a full
light/dark palette pair and final design polish are outside this spike. The default palette
is dark; the swap test uses an in-memory alternate palette, not a second shipped theme.
Cover art is deliberately geometric code with theme-bound, render-invalidating brush properties; its geometry is not a theme
API. App's file includes are the current theme selection mechanism.

## Scope of the evidence

The PNG proves that a library-shaped view draws through headless Skia at the recorded
dimensions; it does not itself prove resource provenance. The theme-swap semantic test
asserts that card/cover brush properties and styled cover height consume live theme
resources. Tests also prove the bound controls, search/filter, selection and layout at
the two tested sizes. It does **not** prove v10 visual/behaviour parity, a real
X11/Wayland desktop, AT-SPI/screen-reader accessibility, production virtualization or
large-library performance, keyboard/pointer end-to-end interaction, localization, other
scalings/window sizes, product integration or a finished user-theme format. This is
evidence for a shape, not integration or a product acceptance gate. The committed tree
is only a structural drift gate for this fake-data spike. Two adversarial agent reviews of
the initial head were supplied by firstmate; the follow-up changes require parent review.
