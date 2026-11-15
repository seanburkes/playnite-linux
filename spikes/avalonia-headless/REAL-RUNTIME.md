# Real runtime: Wayland, X11/XWayland and AT-SPI

Question: the headless spike proves a synthetic window renders under Avalonia's headless
platform and that its managed automation peers and keyboard semantics work. Does the same
window open as a real GUI window on Wayland and X11, and does the Linux accessibility bus
(AT-SPI) expose it?

Result (2026-11-14, corrected after review): **yes for a real window on both stacks;
accessibility is backend-specific.** The X11 backend exposes an AT-SPI tree in this
environment. The Wayland backend exposes **nothing** on the accessibility bus, including when
the process is started with an explicit accessibility environment and a reachable bus address.
This is the finding, not a probe bug: the Wayland process never joins the AT-SPI bus, and
`Avalonia.Wayland.dll` contains no AT-SPI bridge.

## Environment

| Item | Value |
| --- | --- |
| Session | Hyprland 0.56.2, Wayland (`WAYLAND_DISPLAY=wayland-1`, `XDG_SESSION_TYPE=wayland`) |
| X11 | XWayland on `DISPLAY=:0` (a bare Xorg server was not tested) |
| Accessibility | at-spi2-core: `at-spi-bus-launcher`, `at-spi2-registryd`; bus at `$XDG_RUNTIME_DIR/at-spi/bus_0` |
| .NET / Avalonia | .NET 11.0.100, Avalonia 12.1.6 (`Avalonia.Desktop`, `Avalonia.X11`, `Avalonia.Wayland`) |
| AT-SPI client | system `/usr/bin/python3` + GObject `Atspi` bindings, plus `gdbus` for raw text reads |

Accessibility environment used for **both** app runs and logged by the app itself
(`app.log`, `atspi-app.log`):

```
AT_SPI_BUS_ADDRESS=unix:path=/run/user/1000/at-spi/bus_0
ACCESSIBILITY_ENABLED=1
GTK_A11Y=atspi
NO_AT_BRIDGE=0
DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus
```

`gdbus call --session --dest org.a11y.Bus --object-path /org/a11y/bus
--method org.a11y.Bus.GetAddress` returned the same `unix:path=/run/user/1000/at-spi/bus_0`,
so the address was live and valid. The negative on Wayland therefore is **not** explained by a
missing bus address or enable variable; the reviewer's confound was retested explicitly.

## How the window was launched

`RealRuntime/` is a minimal GUI host that compiles the same `SyntheticWindow.axaml` and
code-behind as the headless test, with `UsePlatformDetect()` (X11) or `UseWayland()`
(`SPIKE_PLATFORM=wayland`). It only adds environment/event logging and an auto-exit timer; it
does not change the window under test.

```sh
dotnet build spikes/avalonia-headless/RealRuntime/RealRuntime.csproj -c Release
spikes/avalonia-headless/RealRuntime/run-evidence.sh wayland <evidence-dir>
spikes/avalonia-headless/RealRuntime/run-evidence.sh x11     <evidence-dir>
```

The harness launches the app twice on X11: phase 1 for the window/screenshot/keyboard evidence
and passive AT-SPI discovery, phase 2 for the AT-SPI actions on a fresh window, so action
postconditions are not polluted by the keyboard pass. It uses Hyprland's Lua dispatchers only
to float the window and inject keys on Wayland; it changes no compositor or accessibility
configuration. Evidence directories are created outside the repository.

## Proven on real Wayland

- Native window: `impl=Avalonia.Wayland.WindowImpl`; Hyprland reports `xwayland=false`,
  `mapped=true`, `title="Synthetic library spike"`.
- The window is **640x480** once floated (the tiler initially stretches it to 920x1060;
  Avalonia then resizes with it).
- The compositor screenshot of the window region is a real 640x480 render showing every
  control (`window.png` in the evidence directory).
- Keyboard traversal and activation matched the headless test order:
  `Search` → Tab → `Installed` → Space → checked off → Tab → list item → Tab → `Play` →
  Space → invoked ("Invoked: Orbit (no process launched)").
- `TryGetPlatformHandle()` returns `null` on the Wayland backend (`handle=none`); the evidence
  for the real window is the compositor state plus the screenshot, not an Avalonia handle.
- After close: `phase1_LEFTOVER_PROCESS no`, `phase1_LEFTOVER_WINDOW no`.

## Proven on X11/XWayland

- `impl=Avalonia.X11.X11Window`, `handle=XID`; `xdotool` geometry `640x480` at `665,300`;
  Hyprland reports `xwayland=true`.
- The same Tab/Space sequence, driven with real XTEST events, produced the same focus/action
  sequence as headless and as Wayland.
- The two stack screenshots are both 640x480 and show the same controls; **no pixel-level
  comparison was performed**, so they are not claimed to be pixel-identical.
- After each phase: `LEFTOVER_PROCESS no`, `LEFTOVER_WINDOW no`, `LEFTOVER_X11_WINDOW no`.
- This is **XWayland**, not a bare Xorg server.

## Proven via AT-SPI (what an AT-SPI client observes)

### X11/XWayland: exposed

With the accessibility environment above, the app registers as `Avalonia Application`. The
probe walks the tree and reads `AccessibleId` from each node, so the automation-id provenance
is reproduced rather than asserted by hand. The ids below come directly from
`AutomationProperties.AutomationId` in `SyntheticWindow.axaml`:

| Role | Name | AccessibleId | States | Actions |
| --- | --- | --- | --- | --- |
| frame | Synthetic library spike | `library` | active, enabled, sensitive, showing, visible | – |
| entry | Search games | `search` | enabled, focusable, focused, sensitive, editable, showing, visible | – (Text + EditableText) |
| check box | Installed only | `installed` | checked, enabled, focusable, sensitive, showing, visible | `toggle` |
| list | Games | `games` | enabled, sensitive, showing, visible | – (Selection) |
| list item | Orbit — synthetic game | – | enabled, focusable, selected, sensitive, showing, visible | `select` |
| list item | Meadow — synthetic game | – | enabled, focusable, sensitive, showing, visible | `select` |
| button | Play Orbit | `play` | enabled, focusable, sensitive, showing, visible | `click` |
| button | Details unavailable | `details` | focusable, showing, visible (no enabled/sensitive) | `click` (refused) |
| label | Ready: synthetic data only | `status` | enabled, sensitive, showing, visible | – |

Phase 2 starts from a fresh window and asserts each change on the same object reference:

- `initial`: checkbox `checked=true` (AccessibleId `installed`), entry text `Orbit`
  (AccessibleId `search`), status label `Ready: synthetic data only` (AccessibleId `status`).
- Checkbox action `toggle` (`installed`) → `checked_after_toggle=false` on that same object.
- Button action `click` (`play`) → the status label's name became
  `Invoked: Orbit (no process launched)`.
- Disabled button `click` (`details`) → refused with `Element not enabled`.
- Editable text on `search` → `text_after_set=Meadow` read back through
  `org.a11y.atspi.Text.GetText` (initial read was `Orbit`).

What is missing or noisy on X11:

- The entry has no AT-SPI `Value` interface; its content is reachable through
  `Text`/`EditableText`.
- Internal visual-tree containers (`Border`, `ContentPresenter`, `VisualLayerManager`, …) are
  exposed as unnamed panels between real controls. They do not prevent discovery by
  name/role/id.
- The list exposes `List` + `Selection` and per-item `select`; no table/row/column semantics.
- Labels carry their text as the accessible name and have no automation id unless one is set.

This is an observation of the AT-SPI tree and actions. It is **not** a claim about what a
screen reader would speak, nor a claim that the X11 bridge is complete.

### Wayland: not exposed

The same app, launched as a native Wayland window with the accessibility environment above:

- `Atspi.get_desktop(0)` listed only `nm-applet` and `xdg-desktop-portal-gtk`; the app was
  absent.
- The app pid was absent from the AT-SPI bus connection list (every bus name on `bus_0` was
  resolved to its Unix pid).
- The app's own log shows it received `AT_SPI_BUS_ADDRESS`,
  `ACCESSIBILITY_ENABLED=1`, `GTK_A11Y=atspi` and `NO_AT_BRIDGE=0`; the session bus still
  advertised the same a11y bus address.
- Therefore there are no names, roles, states, actions or text for the window through AT-SPI
  on this Wayland backend.

This is consistent with the binaries: `Avalonia.X11.dll` (12.1.6) contains
`Avalonia.X11.X11AtSpiAccessibility` and `Avalonia.FreeDesktop.AtSpi`, while
`Avalonia.Wayland.dll` has zero matches for `atspi`/`a11y`. The runtime observation is the
proof; the symbols explain it.

### Control: the probe is not the problem

The same probe was run against a known-accessible GTK app under the same session and
environment:

```
zenity 4.2.2 (GTK 4; libgtk-4.so.1.2400.1), native Wayland (xwayland=false)
/usr/bin/python3 spikes/avalonia-headless/RealRuntime/probe_atspi.py --pid <zenity-pid>
TARGET name='zenity' pid=<pid> bus=:1.x
  application  name='zenity' id='org.gnome.Zenity'
    dialog     name='AtspiControl' id='zenity_info_dialog' actions=['window.close','default.activate']
      ...
      button   name='OK' actions=['click']
```

So a Wayland-native GTK app is discovered and exposes roles, ids and actions through the same
bus from the same session. The Avalonia Wayland negative is specific to that backend.

## Still unproven

- A real screen reader (for example Orca) was not run. The observations are from an AT-SPI
  client; speech output, caret tracking and browse mode are untested.
- The real Playnite shell, library rendering, Steam launch and product code are untouched.
  This proves the synthetic spike window only.
- Wayland accessibility is absent for this Avalonia version in this environment. No claim is
  made about future versions or other Wayland toolkits.
- A bare Xorg session (not XWayland) was not tested.
- Keyboard automation on Wayland used Hyprland's compositor-specific `send_shortcut`; a
  generic (uinput/ydotool) path was not tested.
- Modifier shortcuts, IME, high-DPI scaling, focus-ring rendering and visual parity against
  the v10 Windows UI are out of scope.

## Evidence and checks

`run-evidence.sh` regenerates the evidence outside the repository. Key files per run:
`env.txt` (session/bus/env), `window-state.json`, `window.png`, `app.log` and
`atspi-app.log` (including logged env), `keyboard.txt`, `atspi-tree.json`,
`atspi-summary.txt`, `atspi-actions.json`, and `pids.txt` with per-phase process/window
cleanup results.

The existing headless spike still passes unchanged: `Total: 3, Errors: 0, Failed: 0,
Skipped: 0, Not Run: 0`.
