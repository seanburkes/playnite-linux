# `ui-shell` — standalone library-shell evidence

Question: can Avalonia 12.1.6 on Linux express ADR-013's library grid, search/filter and
selection-driven details using ADR-007's resource-dictionary pipeline, and render it
reproducibly without a display?

Result (2026-11-15): **yes for this spike**. Six xunit v3 facts pass, zero skips. No
Playnite product assemblies are referenced. All six titles, playtimes, dates, installed
flags and geometric covers are **invented fake data**, not real game metadata/artwork.
There is no animation code, networking, timer, DB, launching, settings or packaging.

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
`Total: 6, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`.

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

Observed reference PNG: **69,092 bytes**, **1200×800**, 4,642 distinct RGBA colours.

```text
SHA-256: f5bde1e9872e6a44ee7fb4d1a1dd409cdb577b7e6c0588314c5096f36a5d10e5
```

Three captures are byte-identical within each run; two fresh process runs also produced
that identical PNG (`cmp` exit 0). No discrepancy was observed. This is not a promise of
identical output across CPU architectures, native Skia versions, OS/font rasterizers or
future package versions. Reference hashes are evidence, **not checked-in pixel gates**.

## Semantic tests

| xunit fact | Assertions |
| --- | --- |
| `Grid_binds_every_fake_cover_title_playtime_and_last_played` | Six catalog items and six realized cells; each cell has its bound title, playtime, last-played text and geometric-cover motif. |
| `Search_and_installed_filter_narrow_the_bound_grid_and_clear_empty_details` | Editing the actual search TextBox filters case-insensitively; checkbox combines installed filtering; zero results clear details; clearing search restores four installed entries and bound count. |
| `Selecting_a_cell_updates_all_bound_details` | Changing ListBox selection updates the model and visible title, playtime and last-played details. |
| `Narrow_and_wide_layouts_keep_cells_search_and_details_inside_the_frame` | Both sizes retain six realized cells, positive in-frame search/grid/details bounds, multiple columns, all cell text inside the grid viewport, and narrow details below the grid; captures both sizes. |
| `Replacing_a_theme_dictionary_updates_resource_driven_controls` | Replaces the merged colour dictionary, checks live window background/accent brush changes, then restores it. No product theme loader is implied. |
| `Skia_captures_are_nonblank_decodable_and_byte_identical` | Three fresh windows yield identical PNG bytes and RGBA buffers; PNG decode preserves dimensions/pixels; >32 distinct whole-frame colours rejects blank output. |

The colour-count threshold is only a **loose full-frame nonblank guard**. There is no
legacy-image comparison, snapshot acceptance, or pixel-parity threshold. Semantic
assertions, not screenshot resemblance, prove the interactions and tested layout.

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

**Placeholders / not implemented:** `theme.yaml` is a proposed documented manifest, not
parsed at runtime; `apiVersion: 0` is experimental, not a product API commitment. Arbitrary
user-directory discovery/loading, validation, compatibility/version negotiation, a full
light/dark palette pair and final design polish are outside this spike. The default palette
is dark; the swap test uses an in-memory alternate palette, not a second shipped theme.
Cover art is deliberately geometric code with theme brushes; its geometry is not a theme
API. App's file includes are the current theme selection mechanism.

## Scope of the evidence

The PNG proves that a real themed library-shaped view draws through headless Skia at
the recorded dimensions. Tests prove the bound controls, search/filter, selection and
layout at the two tested sizes. It does **not** prove v10 visual/behaviour parity, a real
X11/Wayland desktop, AT-SPI/screen-reader accessibility, production virtualization or
large-library performance, keyboard/pointer end-to-end interaction, localization, other
scalings/window sizes, product integration or a finished user-theme format. This is
evidence for a shape, not integration or an acceptance gate. Independent blind reviews
remain a parent review step; this worker has not self-certified two reviews.
