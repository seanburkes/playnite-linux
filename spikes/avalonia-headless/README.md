# `avalonia-headless`

Question: can a Linux test process drive Avalonia 12 under the headless platform with real Skia
rendering, capture a deterministic frame, and exercise the managed automation-peer and keyboard
semantics that the port depends on?

Result (2026-11-14): **yes** for a synthetic 640×480 window. Three xunit v3 facts pass with zero
skips: three byte-identical Skia captures that decode back to identical pixels, a managed
automation-peer tree with working value/toggle/invoke providers, and Tab traversal plus Space
press/release activation.

Run from the repository root with `DISPLAY` and `WAYLAND_DISPLAY` unset:

```sh
rm -rf /tmp/playnite-spike-evidence
DISPLAY= WAYLAND_DISPLAY= SPIKE_EVIDENCE_DIR=/tmp/playnite-spike-evidence \
  dotnet run --project spikes/avalonia-headless/HeadlessSpike.csproj -c Release
```

`SPIKE_EVIDENCE_DIR` is mandatory and must name a new or empty directory; the tests fail rather
than write into a reused one. Expect `Total: 3, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0` in
the xunit v3 summary. The run writes:

- `render-1.png`, `render-2.png`, `render-3.png` — the three captures;
- `render.json` — size, scaling, repetition count, SHA-256 of the first capture, and distinct
  colour count;
- `automation-tree.json` — the managed `AutomationPeer` tree (id, name, role, enabled, focusable,
  offscreen, value, toggle state, invoke availability, children).

What the artifacts do **not** prove: `render-*.png` shows that Skia drew this synthetic window in
the headless platform, not that a real X11/Wayland window, the Playnite shell, or a Windows build
renders identically. `automation-tree.json` is Avalonia's managed automation-peer model, not
AT-SPI, a screen reader, or any other Linux accessibility bridge. Nothing here launches Steam or a
game process, and this spike is not a visual-parity, performance, or acceptance gate.

Real-runtime follow-up: `REAL-RUNTIME.md` records the separate GUI launch on Wayland and
X11/XWayland, including what the AT-SPI screen-reader path does and does not expose.
