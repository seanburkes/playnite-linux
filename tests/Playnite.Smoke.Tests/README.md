# Host smoke contract (p2-004)

The Desktop scaffold creates exactly one `Playnite` window through
`AppBuilder.StartWithClassicDesktopLifetime`, with Fluent as the base theme. There
is no product UI or data access. `--smoke` posts a close only after `Opened`, and
returns failure unless a visible single window opened and then raised `Closed`.
Without `--smoke`, closing the main window ends the desktop lifetime normally.
`--headless` selects Avalonia's headless platform instead of platform detection.

The CLI prints its assembly version with no arguments or `--version` and exits
zero. Unsupported arguments produce usage on stderr and exit one.

After a Release solution build:

```sh
dotnet test --project tests/Playnite.Smoke.Tests -c Release --no-build --no-restore --fail-skips on --fail-warns on
timeout 30s xvfb-run -a dotnet src/Playnite.Desktop/bin/Release/net11.0/Playnite.Desktop.dll --smoke
```

The four mandatory test cases launch the built hosts as subprocesses, with both
`DISPLAY` and `WAYLAND_DISPLAY` removed. They assert window lifecycle messages,
exit codes, and version output, and kill the process tree on a 30-second timeout.
Linux CI discovers this suite with all other test projects, and separately runs
the native X11 lifecycle under Xvfb. Neither path skips on startup failure.

Reference: `legacy/source/Playnite.DesktopApp/App.xaml{,.cs}` establishes the
application/resource entry point; `spikes/avalonia-headless/RealRuntime/Program.cs`
provides the already-proven Avalonia lifecycle shape. This is infrastructure,
not a claim of parity for any frozen product surface.
