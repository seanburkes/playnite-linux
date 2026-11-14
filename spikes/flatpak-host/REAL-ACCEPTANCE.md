# p1-010 real Flatpak sandbox acceptance

Date: 2026-11-14. Status: **real sandbox run completed**. This note records what
actually ran; it does not claim Steam, native-game, Proton-game or ADR-020
acceptance. The companion probe's synthetic/local suite is unchanged.

## Environment

| Item | Observed value |
| --- | --- |
| Host OS | CachyOS, kernel `7.3.0-3-cachyos`, x86_64 |
| `flatpak` | 1.18.4 (pacman `1:1.18.4-2.1`) |
| `flatpak-builder` | 1.4.12 (pacman `1.4.12-1.1`) |
| `flatpak-spawn` | bundled with Flatpak 1.18.4; it has no `--version` flag |
| Runtime | `org.freedesktop.Platform` 24.08, version `freedesktop-sdk-24.08.37`, commit `0ad7bef7a8e69b26b7e0d666a11b60dca10461c23cc0d2ee5391991c559c28c7` |
| SDK | `org.freedesktop.Sdk` 24.08, version `freedesktop-sdk-24.08.37`, commit `f840a6835a5d303866d8309ca512833fb516910f6994662cbe1c298371ef6873` |
| Install scope | user (`--user`), host user Flathub remote present before and after |
| Probe Python | `/usr/bin/python3` 3.14.10 on the host; 3.12.14 inside the runtime |

**EOL caveat.** org.freedesktop.Platform/Sdk 24.08 is end-of-life; flatpak-builder
and flatpak both printed EOL notices. It is acceptable for this one-shot probe
only. Final packaging must use a supported runtime. No extra runtime was
installed, downloaded or selected for this acceptance.

## Build

The app is the existing probe plus a one-line identity emitter, under
`spikes/flatpak-host/`:

- `org.playnite.FlatpakHostProbe.yaml` — minimal manifest.
- `sandbox-entrypoint.sh` — prints `sandbox_identity`, then execs
  `probe.py --host-child`.
- `probe.py` — unchanged.

The only permission is `--talk-name=org.freedesktop.Flatpak`, which
`flatpak-spawn --host` requires. It is probe-only, not a proposed production
permission. No filesystem, network or device permission was added.

Exact build command (build directory deliberately outside the repository):

```sh
flatpak-builder --user --install --force-clean --disable-rofiles-fuse \
  /home/sean/src/firstmate/data/p1-010-live/build \
  spikes/flatpak-host/org.playnite.FlatpakHostProbe.yaml
```

Result: exit 0; app commit
`f1c4545605ac38025637f370abd64ba1c05d5e5cc9433d8e1558f0c85e35bc3c`
installed for the user.

## Primary run

Exact command:

```sh
flatpak run org.playnite.FlatpakHostProbe
```

Observed output, verbatim:

```text
sandbox_identity pid=2 namespace=pid:[4026533530]
{
  "capabilities": {
    "flatpak_on_path": false,
    "flatpak_spawn_on_path": true,
    "in_flatpak": true,
    "steam_on_path": false
  },
  "host_child": {
    "caller_proc_visibility": "not-visible-or-exited",
    "child_identity": {
      "namespace": "pid:[4026531836]",
      "pid": 435688,
      "start_ticks": 199472051
    },
    "exit_code": 23,
    "exit_status_forwarded": true,
    "result": "observed",
    "stderr": ""
  },
  "local_child": {
    "caller_proc_visibility": "same-identity",
    "child_identity": {
      "namespace": "pid:[4026533530]",
      "pid": 4,
      "start_ticks": 199472048
    },
    "exit_code": 23,
    "exit_status_forwarded": true,
    "result": "observed",
    "stderr": ""
  },
  "real_steam_native": "not-run: user games are outside this probe",
  "real_steam_proton": "not-run: user games are outside this probe"
}
```

`flatpak run` exited 0. The probe JSON is an observation report, not a verdict.

Host-side reference, taken directly in the invoking shell:

```text
host namespace  pid:[4026531836]
```

### Identity facts

| Fact | Inside the sandbox | On the host |
| --- | --- | --- |
| App/entrypoint PID | 2 | — |
| PID namespace | `pid:[4026533530]` | `pid:[4026531836]` |
| Host child PID | not addressable | 435688 |
| Host child start ticks (stat field 22) | 199472051 | 199472051 |
| Host child PID namespace | not visible | `pid:[4026531836]` |
| `flatpak-spawn --host` transport | worked; result `observed` | — |

- The sandbox PID namespace differs from the host namespace, and the app's own
  PID is 2 in the sandbox.
- The host child reported the host namespace `pid:[4026531836]`, matching the
  invoking host shell.
- `caller_proc_visibility` is `not-visible-or-exited`: from inside the sandbox the
  host child's `/proc/<pid>` entry is absent. The probe computes visibility
  **before** it writes `release\n`, so the child was still alive at that instant;
  the result here is namespace isolation, not a child that had already exited.
- The local child stayed inside the sandbox namespace, matched its own identity,
  and exited 23.

### Exit path

The host child is the probe's fixed inline Python. After reporting identity it
waits up to 3 seconds on stdin; the probe sent `release\n`, the child exited 23,
and `flatpak-spawn` forwarded that code (`exit_code: 23`,
`exit_status_forwarded: true`, empty stderr). No host process was enumerated,
signalled, launched or killed other than that one controlled host child.

## Negative / boundary case

Cheap and safe: remove the one D-Bus permission for a second run of the same
installed app.

```sh
flatpak run --no-talk-name=org.freedesktop.Flatpak org.playnite.FlatpakHostProbe
```

Observed:

```text
sandbox_identity pid=2 namespace=pid:[4026533530]
...
  "host_child": {
    "exit_code": 1,
    "result": "launch-failed",
    "stderr": "Portal call failed: org.freedesktop.DBus.Error.ServiceUnknown\nHint: --host only works when the Flatpak is allowed to talk to org.freedesktop.Flatpak\n"
  },
...
```

`flatpak run` still exited 0, but the host-child result is `launch-failed` with
exit 1. This is a permission-denied transport outcome, recorded as failure rather
than success. The same denial appears for any sandbox without
`--talk-name=org.freedesktop.Flatpak`; the probe therefore does not silently
fall back to a different mechanism.

Not tested: sending a signal from the sandbox to a live host PID. The primary
run already shows the host PID is not present in the sandbox `/proc`, but no
signal was attempted, to avoid touching host processes.

## Known limitations

- Only `org.freedesktop.Platform` 24.08 (EOL) was probed; no other runtime,
  branch or supported runtime was tested.
- The manifest grants `org.freedesktop.Flatpak` D-Bus access. Whether production
  should hold that permission is an ADR-020/captain decision; this probe neither
  proposes nor implements a helper.
- `steam_on_path=false` inside this sandbox. No Steam client, native game or
  Proton game was launched, enumerated or tracked. Steam/native/Proton acceptance
  remains open.
- The host child requires host `/usr/bin/python3`; that is an experiment
  dependency, not a production dependency.
- The sandbox PID-namespace inode was `pid:[4026533530]` in both runs. Inode
  reuse across successive short-lived sandboxes was not investigated; it does not
  affect the host/sandbox mismatch observed here.
- Reparenting, launcher-PID-versus-game-PID, already-running Steam behaviour and
  game attribution are outside this transport probe and remain p1-007 work.
- This acceptance does not consume or validate the p1-003 fixture data.

## Narrow checks

```sh
PYTHONDONTWRITEBYTECODE=1 /usr/bin/python3 -m unittest discover -s spikes/flatpak-host -v
```

26 tests ran, zero failures, zero errors, zero skips.

## Reproducibility and cleanup

The manifest, entrypoint and unchanged probe are committed under
`spikes/flatpak-host/`. Raw build/run logs are retained outside the repository at
`/home/sean/src/firstmate/data/p1-010-live/{build.log,run-primary.log,run-negative.log,tests.txt,uninstall.log}`.

Cleanup performed after capturing the evidence:

```sh
flatpak uninstall --user -y org.playnite.FlatpakHostProbe
rm -rf /home/sean/src/firstmate/data/p1-010-live/build
rm -rf <worktree>/.flatpak-builder
```

Confirmed afterwards: `flatpak list --app` is empty; user remotes are still only
Flathub; installed runtimes are exactly the six that were present before the
build (Platform 24.08, its GL.default/openh264/Locale extensions, Sdk 24.08 and
the adw-gtk3 theme). No runtime was added or removed by this work; the EOL 24.08
runtime predates it.
