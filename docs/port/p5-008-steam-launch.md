# p5-008 — Steam launch and conservative external-process attribution

Implementation only. **No real Steam run, Flatpak acceptance, or Phase 5 exit is claimed.**
Frozen oracle: `legacy/source/Playnite/Common/ProcessStarter.cs:70–81` opens URLs; it supplies
no Steam discovery or process-attribution behaviour. The Linux-specific contract is
`08-STEAM-SPEC.md` §§5–7 plus the firstmate ruling of 2026-11-18 below.

## Adapter and launch matrix

`SteamProcessLauncher` implements the existing `IProcessLauncher` and `IProcessObserver`:
it passes an attributed **game** handle to `GameSessionTracker`, not the request process.
`IExternalProcessHost` is the injected runner/observation/signal seam;
`LinuxExternalProcessHost` reads stat and canonical exe links in the same PID namespace.
There are no changes to p5-004, no shell strings, environment reads, command-line scraping,
Steam configuration edits, prefix repairs, or live Steam in unit tests.

| Resolved executable | Native / Proton / UNKNOWN selection | Request |
| --- | --- | --- |
| Absolute Steam executable supplied | All three | `steam -applaunch <uint appid>` via `ArgumentList` |
| Steam executable unavailable (`null`) | All three | Absolute desktop protocol opener (`xdg-open`), one Steam rungameid URI argument (spec §6.3) |
| Steam request fails or times out | All three | UNKNOWN; no speculative URI retry (which could double-launch) |
| Sandbox / host Steam | Unverified | Plan below, not an enabled implementation |

Executable resolution belongs to the composition boundary. The acceptance tool resolves PATH
once and passes absolute executables. A zero request exit is acceptance of the request only.
Only installed/update-pending apps can be requested. Nonzero exit and attribution timeout
produce `SteamAttributionUnknownException`, not a successful session.

## Native/Proton evidence, without guessing

`SteamExecutionSelection.Evaluate` consumes the parsed manifest, the **explicitly selected
account's** parsed `platform_overrides` root object, and observed `pfx`/`config_info` presence.
It never chooses an account, invokes Wine, or chooses a Proton version:

1. `src=windows, dest=linux` for the manifest appid: Proton override evidence. Missing prefix
   stays explicitly unverified; Steam may create it, but Playnite never does.
2. Both `pfx/` and `config_info`: Proton marker evidence, not a tool/version guarantee.
3. Explicit `src=linux, dest=linux`, without conflicting Proton markers: Native evidence.
   This literal policy is synthetic-tested, **not observed in the real corpus**.
4. Otherwise UNKNOWN. A normal native manifest (Factorio/Terraria in the corpus) has no
   authoritative platform field. No override, no prefix, an empty compatdata directory,
   depot numbers, or a `.exe`-looking script path is not native/Proton proof.

`SteamApp.IsProton` is an older two-valued projection; it is **not used** to guess selection.
UNKNOWN launches through Steam identically and is surfaced as UNKNOWN. Owner-selected native
and Proton acceptance appids describe what the owner knows from Steam, not fabricated parser
evidence. Active-account selection, per-app tool/version and launch-option application remain
unverified. Prefix paths are supplied explicitly, including non-default prefix locations.

## Attribution and stop rule (approved option A)

1. Take a complete baseline before sending the request. Incomplete baseline means no launch.
2. Poll within a bounded window (default 30 seconds, including the request). A candidate must
   be live, absent from the baseline by **PID + raw start ticks**, and its canonical executable
   must be a path-component-bounded descendant of the canonical game installdir or that
   appid's canonical Proton `pfx`. Exactly **one** candidate is required.
3. Exclude Steam, steamwebhelper, pv-adverb, Wine loaders/wineserver and the listed runtime
   wrappers as candidate roots. Wineserver/wrappers never start or prolong playtime.
4. Its PGID must be greater than 1, different from every pre-existing live group (therefore
   different from the Steam client group), and contain only fresh game-path processes or
   recognised runtime wrappers. Missing executable evidence, a client/helper, an unrelated
   executable, pre-existing membership or incomplete coverage means UNKNOWN, not permission.
5. Once attributed, retain PID/start/PGID identities for all observed group members. Stop,
   cancel, disposal and natural exit revalidate complete coverage, group continuity and every
   member before signalling **individual identity-checked members of this group only**.
   Re-scan and stop fresh in-group children until the whole group is proven empty. No `kill`
   shell command, negative-PGID signal, arbitrary process-tree kill or unrelated group signal.
   A recycled PGID without a retained identity is not ownership.
6. The unchanged p5-004 tracker debounces game-process absence (two complete samples by
   default); adapter cleanup then reaps runtime helpers before a terminal transition.
   A known child escaping the group raises `ProcessSurvivorException` / degrades observation;
   a shared/unverifiable group refuses stop. Neither is falsely reported as stopped.

All host paths passed to the policy must be canonical, not symlink aliases; the production
host resolves executable links, p5-002 resolves library roots, and the acceptance tool checks
the full install root. An explicit canonical `ExecutableOverride` is an **owner-selected
heuristic**, not manifest evidence; it does not bypass uniqueness, infrastructure exclusions
or group safety. It is opt-in, not required by the normal attribution path.

### UNKNOWN / manual fallback and residuals

UNKNOWN does not enter automatic `Running` or fabricate a no-orphan claim. The caller must
show “tracking unknown; use Steam Stop”, leave stop/attach as explicit owner actions, and
start a new attribution attempt only with newly established evidence. No generic manual
attach UI is implemented in this unit. Do not feed a Steam client handle to the tracker as
a manual fallback. Cancellation before attribution only releases watchers/handles: the
request may have become the long-lived Steam client, so **it is never killed**. Inspect and
stop any unattributed game in Steam. After attribution, cancellation cleans the owned group.

The no-orphan boundary is the attributed, verified group, not the entire host. A game that
creates another session/group or is reparented outside the observed group **before observation**
is UNKNOWN/manual. An unobserved child escaping between samples is likewise outside the proof.
An immediately exiting game may disappear before the tracker sees its first live sample;
the existing tracker then degrades rather than inventing an exit. A Proton process whose
`/proc/exe` resolves to the shared Wine loader outside the game/prefix is UNKNOWN; no argv or
environment heuristic silently upgrades it. Group scans are not atomic, and the BCL signal
path has the usual stat-check-to-signal race; this is not a pidfd/cgroup containment guarantee.
Host namespaces and start ticks cannot be mixed across boots or PID namespaces.

## Tests and acceptance

`tests/Playnite.Core.Tests/p5-008/` exercises injected argv, native/Proton-prefix shapes,
unique/shared/multiple/unknown samples, PID and PGID reuse, path boundaries, wrapper exclusion,
escaped children, stop/cancel/disposal, and zero/nonzero command outcomes. No live Steam.
The real redacted corpus **is committed** at `tests/fixtures/steam/real`; its discovery test
rehydrates paths in a temporary root and asserts four manifest ids/install paths, both
libraries, the unavailable external library, declared app sets and Proton override evidence.
No skipped or conditionally passing corpus test, no fixture/oracle edits.

Owner-only script (not run by CI; building it is not real-machine evidence):

```sh
scripts/p5-008/steam-acceptance.sh --confirm-no-unsaved-data \
  /absolute/native/SteamLibrary 427520 /absolute/proton/SteamLibrary 1238040
# Repeat with --uri to test the protocol-handler path.
# Optional --native-exe /canonical/game/binary --proton-exe /canonical/game.exe
```

Use **your installed appids**, not blindly the example ids. Start/log in to Steam first;
close other copies of the selected games, save data and use a disposable test save/profile.
The script runs native stop, native cancel, Proton stop, Proton cancel. It prints request
argv, attributed PID/start ticks/PGID, every signalled PID/PGID and tracker transitions;
asserts Running → Stopped/Cancelled, empty attributed groups and surviving original client
identities. It does not change launch options, prefixes, Steam configuration or permissions.
It force-stops selected games (not graceful shutdown); game-generated files/Steam playtime
are normal launch effects and **not rolled back**. Do not use a session with unsaved progress.
UNKNOWN exits nonzero and explicitly asks the owner to stop the selected game in Steam;
it never “cleans up” unidentified processes. Keep output private: executable paths can contain
the owner's home. Inspect remaining processes manually before another run.

## Flatpak plan — not implemented or accepted

The shipped host adapter must **not** read sandbox `/proc` and attribute a host request from
that table. A future host helper must perform request, baseline, canonical-exe/stat observation,
signals and verification **all in the host namespace**, returning only bounded identity/status
records across the sandbox boundary. Candidate argv vectors:

```text
flatpak-spawn --host steam -applaunch <appid>
flatpak-spawn --host flatpak run com.valvesoftware.Steam -applaunch <appid>
flatpak-spawn --host xdg-open <Steam-rungameid-URI-from-spec-section-6.3>
```

Resolve host executables in the host helper; never assemble a shell command. Flatpak-spawn's
exit is not the game's exit. The helper must apply this same identity/group policy; absent
host-helper access/coverage remains UNKNOWN with Steam manual stop, not sandbox-PID signalling.

Read only the concrete canonical host Steam library roots, for example per invocation:
`--filesystem=/absolute/SteamLibrary:ro` (and the concrete Steam root `:ro` for manifests and
explicitly selected account compat metadata). For sandboxed Steam, grant only its concrete
`USER_HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam:ro` root and external libraries needed
(replace `USER_HOME` with the actual canonical host home at the invocation boundary).
Prefer library `steamapps:ro` plus the exact root metadata/config subdirectories when discovery
can work with those. Never request `--filesystem=host`, `home`, a writable library or all of
the entire Flatpak application-data parent directory. Resolve symlink targets and mount aliases before selecting grants. Reading
metadata does not require writing prefixes or saves. Host spawn may additionally require
`--talk-name=org.freedesktop.Flatpak`; that is a powerful host-execution capability, **not a
narrow filesystem grant**, and needs explicit packaging/security approval. Do not add it
silently or call the overall sandbox narrow merely because filesystem access is read-only.

### Exact outstanding owner-machine checklist

- Native Steam: native and Proton stop/cancel script runs, then the same four via `--uri`;
  retain real PID/start/PGID output, return codes and tracker events. Verify the actual game
  visible in Steam matches the requested app, including DRM/overlay behaviour.
- Verify full original client/helper survival and no live game/runtime group member after
  stop/cancel; inspect `/proc`/process table independently. Exercise a deliberately ambiguous
  launch and confirm UNKNOWN, Steam manual Stop and zero unrelated signals.
- Observe actual Proton `/proc/exe` placement. Report UNKNOWN when it is a shared loader;
  separately label any owner executable override heuristic, never present it as automatic proof.
- Prove configured Steam launch options are applied without modifying them from Playnite;
  verify cold-client startup, offline/auth failure, immediate exit and update-pending launch.
- Inside the intended Playnite Flatpak: verify the exact `:ro` grants suffice for root/library
  discovery, a space/Unicode external library and selected-account compat metadata; writing
  those locations and reading an unrelated home directory must be denied.
- With explicit host-spawn approval: verify native host Steam and Flatpak-hosted Steam argv,
  URI handler routing, host-helper availability, host PID namespace/start-tick consistency,
  canonical executable visibility, permission failures and helper/client lifetime.
- Repeat native/Proton stop/cancel through that host helper; assert no orphan and no client or
  unrelated group killed. Remove only the test permission overrides afterward; no blanket reset
  of existing owner overrides. Record exact before/after grants. These Flatpak steps cannot pass
  with the current same-namespace adapter and must remain unaccepted until the helper exists.
- No Phase 5 exit claim follows from this unit or from a single acceptance run.
