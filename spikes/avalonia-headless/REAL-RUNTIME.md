# Real runtime: Wayland, X11/XWayland and AT-SPI

Question: the headless spike proves a synthetic window renders under Avalonia's headless
platform and that its managed automation peers and keyboard semantics work. Does the same
window open as a real GUI window on Wayland and X11, and does a screen reader see it through
AT-SPI?

Result (2026-11-14): **yes for a real window on both stacks; accessibility is backend-specific.**
The X11 backend exposes a full AT-SPI tree (names, roles, states, automation ids, toggle/click/
select actions and editable text). The Wayland backend exposes **nothing** on the accessibility
bus. This is the finding, not a gap in the probe: the Wayland process never connects to the
AT-SPI bus at all.

## Environment

| Item | Value |
| --- | --- |
| Session | Hyprland 0.56.2, Wayland (`WAYLAND_DISPLAY=wayland-1`) |
| X11 | XWayland on `DISPLAY=:0` (no bare Xorg server was tested) |
| Accessibility | at-spi2-core: `at-spi-bus-launcher`, `at-spi2-registryd`; bus at `$XDG_RUNTIME_DIR/at-spi/bus_0` |
| .NET / Avalonia | .NET 11.0.100, Avalonia 12.1.6 (`Avalonia.Desktop` + `Avalonia.Wayland`) |
| Probe client | system `/usr/bin/python3` + GObject `Atspi` bindings, plus `gdbus` for raw text reads |

## How the window was launched

`RealRuntime/` is a minimal GUI host that compiles the same `SyntheticWindow.axaml` and
code-behind as the headless test, with `UsePlatformDetect()` (X11) or `UseWayland()`
(`SPIKE_PLATFORM=wayland`). It only adds logging and an auto-exit timer; it does not change the
window under test.

```sh
dotnet build spikes/avalonia-headless/RealRuntime/RealRuntime.csproj -c Release

# Wayland (native; explicit backend)
SPIKE_PLATFORM=wayland SPIKE_EXIT_AFTER_SECONDS=90 WAYLAND_DISPLAY=wayland-1 DISPLAY=:0 \
  dotnet spikes/avalonia-headless/RealRuntime/bin/Release/net11.0/RealRuntime.dll

# X11/XWayland (no Wayland backend available to this project on this host)
env -u WAYLAND_DISPLAY SPIKE_EXIT_AFTER_SECONDS=90 DISPLAY=:0 \
  dotnet spikes/avalonia-headless/RealRuntime/bin/Release/net11.0/RealRuntime.dll
```

`RealRuntime/run-evidence.sh wayland|x11 <dir>` reproduces the capture below. It uses Hyprland's
Lua dispatchers only to float the window (so the requested 640x480 is not tiled) and, on Wayland,
to inject keys; it changes no compositor or accessibility configuration.

## Proven on real Wayland

- The app opened a **native Wayland window**: Avalonia reported
  `impl=Avalonia.Wayland.WindowImpl`, and Hyprland reported `xwayland=false`,
  `mapped=true`, `title="Synthetic library spike"`.
- The window is **640x480** (client and compositor size) once floated; initial tiling resized it
  to 920x1060, which is compositor behaviour, not an app failure.
- The compositor screenshot of the window region is a real 640x480 render and shows every
  control (`window.png` in the evidence directory).
- Keyboard traversal and activation matched the headless test exactly:
  `Search` → Tab → `Installed` → Space → checked off → Tab → list item → Tab → `Play` →
  Space → invoked ("Invoked: Orbit (no process launched)").
- The process closed via the window manager and left no process behind.
- `TryGetPlatformHandle()` returns `null` on the Wayland backend (`handle=none`), so the proof
  of the real window comes from the compositor and the screenshot, not from an Avalonia handle.

## Proven on X11/XWayland

- The app opened an **X11 window**: `impl=Avalonia.X11.X11Window`, `handle=XID`,
  `xdotool` geometry `640x480` at `665,300`; Hyprland reported `xwayland=true`.
- The X11 window screenshot is pixel-identical in content to the Wayland one (same 640x480,
  all controls present).
- Keyboard traversal and activation again matched the headless test exactly, driven with
  `xdotool` XTEST events (search → installed → uncheck → list item → play → invoke).
- The process closed via the window manager and left no process behind.
- This is **XWayland**, not a bare Xorg server; a bare-X11 run was not performed.

## Proven via AT-SPI (screen-reader path)

### X11/XWayland: exposed

The app registers as `Avalonia Application` and a screen reader discovers a full tree. Observed
values (automation ids survive as AT-SPI `AccessibleId`, and all component extents are valid):

| Role | Name | AccessibleId | States | Actions |
| --- | --- | --- | --- | --- |
| frame | Synthetic library spike | `library` | active, enabled, sensitive, showing, visible | – |
| entry | Search games | `search` | enabled, focusable, focused, sensitive, editable, showing, visible | – (Text + EditableText interfaces) |
| check box | Installed only | `installed` | checked, enabled, focusable, sensitive, showing, visible | `toggle` |
| list | Games | `games` | enabled, sensitive, showing, visible | – (Selection interface) |
| list item | Orbit — synthetic game | – | enabled, focusable, selected, sensitive, showing, visible | `select` |
| list item | Meadow — synthetic game | – | enabled, focusable, sensitive, showing, visible | `select` |
| button | Play Orbit | `play` | enabled, focusable, sensitive, showing, visible | `click` |
| button | Details unavailable | `details` | focusable, showing, visible (no enabled/sensitive) | `click` (refused by AT-SPI: "Element not enabled") |
| label | Ready: synthetic data only | `status` | enabled, sensitive, showing, visible | – |

Driven through AT-SPI exactly as a screen reader would, all of these succeeded and the
application reacted:

- `check box` action `toggle` → app observed `installed=False`; the `checked` state cleared.
- `button` action `click` on `Play Orbit` → app observed the click and updated its status label.
- `button` action `click` on `Details unavailable` → AT-SPI refused with
  `Element not enabled`, matching the disabled control.
- Editable text on `Search games` → setting `Meadow` reached the app
  (`textbox search="Meadow"`), and `org.a11y.atspi.Text.GetText` returned `Orbit` with
  `CharacterCount=5`.

So on X11 the screen-reader path carries the same information the headless test asserts:
name, role, enabled/checked state, automation id, bounds, value-as-text, toggle and invoke.
The provider shape differs: the headless test uses `IValueProvider`, while AT-SPI uses
`Text`/`EditableText`; invoke is the `click` action, not a separate invoke interface.

Missing or noisy on X11:

- The entry has no AT-SPI `Value` interface; its content is only reachable through
  `Text`/`EditableText`.
- Internal visual-tree containers (`Border`, `ContentPresenter`, `VisualLayerManager`, …) are
  exposed as unnamed/anchored panels between real controls. They are harmless for discovery by
  name/role/id but make the tree tall.
- The list exposes `List` + `Selection` and per-item `select`; no table/row/column semantics.
- Labels carry their text as the accessible name and no automation id unless one is set.

### Wayland: not exposed

With the same app running as a native Wayland window:

- `Atspi.get_desktop(0)` listed only `nm-applet` and `xdg-desktop-portal-gtk`; the synthetic
  window was absent.
- The app pid was absent from the AT-SPI bus connection list (checked by resolving every bus
  name on `bus_0` to its Unix pid).
- Consequently there are **no names, roles, states, actions or text** for the window through
  AT-SPI on Wayland, and no screen reader can reach it through that path today.

The same probe, run against `zenity`, discovered the app and walked its dialog/actions, so the
negative result is not a broken probe.

## Still unproven

- A real screen reader (for example Orca) was not run; the probe uses the same AT-SPI bus and
  interfaces, but speech output, caret tracking and browse mode are untested.
- The real Playnite shell, library rendering, Steam launch, and any product code are untouched.
  This proves the synthetic spike window only.
- Wayland accessibility: nothing is exposed today. Any accessibility claim for the Wayland
  backend is unsupported until Avalonia adds an AT-SPI bridge there.
- A bare Xorg session (not XWayland) was not tested.
- Keyboard automation on Wayland used Hyprland's compositor-specific `send_shortcut`; a generic
  (uinput/ydotool) injection path was not tested.
- Modifier shortcuts, IME, high-DPI scaling, focus-ring rendering, and visual parity against
  the v10 Windows UI remain out of scope.

## Evidence

Proof artifacts (screenshots, compositor JSON, AT-SPI trees, logs) are outside the repository
at `/home/sean/src/firstmate/data/p1-008-native/{wayland,x11}/`. Key files:

- `window.png` — compositor screenshot of the 640x480 window.
- `window-state.json` — title, class, size, `xwayland`, floating/mapped/visible.
- `app.log` — backend impl, size, focus order, checkbox/click/text events.
- `atspi-tree.json` / `atspi-summary.txt` — desktop app list, tree, bus census.
- `atspi-actions.json` — X11 toggle/click/disabled/set-text results.
- `pids.txt` — exit code and `LEFTOVER_PROCESS no`.

The existing headless spike still passes unchanged: `Total: 3, Errors: 0, Failed: 0,
Skipped: 0`.
