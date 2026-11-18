# Post-merge adversarial review — Proton attribution (p5-008d)

- **Reviewed commit:** `612e193c28b4a046a7edb7affb134865e8575f5c`
  (`feat(steam): attribute Proton titles through the app prefix drive map (#140)`, parent `ceff167`)
- **PR:** https://github.com/seanburkes/playnite-linux/pull/140 (PR head `223bf69d`)
- **Diff reviewed:** `/home/sean/src/firstmate/data/p5-140/postmerge-612e193.diff`,
  sha256 `0c3a562f73d5b739b1c8017792a9ba0fb870c11bf8b42e974db932e43ed8afde`
  (byte-identical to `git diff ceff167 612e193`; 1597 lines, 13 files)
- **Method:** static review of the merged tree plus git object comparison. No build, no test
  run, no edits, no merge (per brief).

## Verdict

**No blocking P1/P2 findings.** Every requirement in the brief is implemented, and each
safety-critical claim is backed by code whose failure mode is "no candidate / no signal /
incomplete snapshot" rather than a guess. Six non-blocking observations are recorded below;
none of them allows a non-game, group, client or helper signal, and none allows a clean
acceptance to be claimed without evidence.

## Checklist verification

| Requirement | Where | Result |
| --- | --- | --- |
| `/proc/PID/exe` primary; cmdline fallback only when exe matches nothing | `SteamLaunchEvidence.cs:374` (`TryGetGame`): native branch requires `Matches` + not wrapper/client and returns before any argv read; fallback also refuses a wrapper/client/shell host exe; `row.Executable is null` never falls back | Pass |
| argv[0] only, absolute drive path (`S:\`, `s:/`) | `SteamLaunchEvidence.cs:142` uses `commandLine[0]` only; `:197` requires letter+`:`+separator and `Length >= 3`; later arguments are never consulted (`ProtonCommandLineTests.Game_path_in_a_later_argument...`) | Pass |
| Drive resolved through *this* app prefix's `dosdevices` | `SteamLaunchEvidence.cs:159`: `<target.ProtonPrefix>/dosdevices/<lowercased>:`; `ProtonPrefix is null` returns before filesystem access (`:137`) | Pass |
| Canonicalised components (no symlink aliasing a root) | `SteamLaunchEvidence.cs:225` resolves every component with `File.ResolveLinkTarget(..., true)`; the resolved path, not the argument string, is compared to the roots | Pass |
| Result is a real file under installdir or prefix (override still applied) | `:193` requires `File.Exists(canonical)`; `:148-149` requires `Matches`, i.e. `Under(install)`/`Under(prefix)`, not infrastructure/shell, and `ExecutableOverride` equality (`:293`) | Pass |
| Traversal / UNC / relative / missing drive / symlink escape rejected | `:197` rejects relative and UNC before lookup; `:159-193` resolves `..` then canonicalises, so out-of-root results fail `Matches`; a missing drive link yields null (caught `IOException` in `Canonical`) | Pass, tested (`SteamProtonCommandLineTests.Windows_path_outside...`, `...Symlink_inside_the_game_directory_that_escapes...`) |
| Wrapper excluded as host exe and as resolved Windows image; Wine preloaders allowed | Host exe: `:374` / `GroupRejection:404`; resolved image: `:148-149`; exclusion set `:104-116`; `wine64-preloader` deliberately absent | Pass, tested (host-exe theory, Windows-wrapper theory, symlinked image resolving to `services.exe`) |
| pidfd pin → stat identity → exe re-read → argv re-check → SIGTERM; withheld on changed/unreadable argv | `LinuxExternalProcessHost.cs:116` pins via `signals.Open`; `:126` re-checks pid/start/PGID/running; `:136` re-reads exe; `:150-168` re-reads and compares argv[0] `Ordinal`, returning without signalling on null/empty/mismatch; `:171` terminates | Pass, tested in `LinuxPidFdSignalTests` (match, non-argv[0], changed, unreadable) |
| No non-game/group/client/helper signal; client retained; no orphan | Only `games` rows (IsGame-gated) are signalled, at most once per stop attempt (`SteamProcessLauncher.cs:298,384-395`); `PidFdSendSignal(fd, 15, ..., 0)` with no negative PID; tracker input is not signal authority; harness asserts client retention and no orphan | Pass |
| Harness readiness settle; `NO LAUNCH` distinct from `ATTRIBUTION UNKNOWN` | `Program.cs:103` waits for complete snapshot + original clients + previous identities gone for 15 s (180 s cap); `:122-133` distinguishes `CandidateObserved`; `PASS` still gated on `IsDegraded` | Pass, with classification caveat O1 |
| Evidence honesty; no Phase 5/Flatpak claim | Merged tree `diff 612e193 223bf69d` is empty; `955260bc..223bf69d` touches only `docs/port/p5-008-identity-stop.md`, the `p5-008` shard and a ledger shard (docs-only, as claimed); Flatpak/launch-options/Phase 5 explicitly out of scope (`docs:165`) | Pass |

## Non-blocking observations

**O1 (P3) — `NO LAUNCH` text asserts a cause it cannot prove.**
`tools/p5-008/SteamAcceptance/Program.cs:129` prints "no game process appeared under
{installdir}/{prefix} … This is a steam launch failure, not an attributed UNKNOWN".
Concrete failing input: a cold Proton launch where only wrappers appear for the whole 240 s
window (`pv-adverb`/`python3`/`steam-launch-wrapper` running, game image never reached) — or a
Proton image whose argv is unreadable. `CandidateObserved` is false either way, so the run is
reported as a Steam launch failure although the launch happened. The `live` re-launch guard at
`:137-144` uses the same `Matches || IsGame` evidence that just failed, so it also cannot see
that process. Not blocking: the flow sets `failed` (`:122`), the run still exits `1`
(`:237-240`), and nothing is signalled; the operational definition ("no candidate seen") is the
one the brief specifies.

**O2 (P3) — "at most one SIGTERM per identity" is overstated.**
`signalled` is local to one `StopAsync` attempt (`SteamProcessLauncher.cs:298`), but
`GameSessionTracker` retries `AttemptStopAsync` (`GameSessionTracker.cs:350-376`, up to three
attempts). Concrete failing input: a Proton game that ignores SIGTERM — the second attempt has
a fresh `signalled` set and signals the same identity again. Safety impact: none (game-only,
pidfd, re-verified each time). The shard claim
(`docs/port/units/p5-008.ledger.d/140-proton-attribution.tsv:2`) should read "at most one
SIGTERM per stop attempt".

**O3 (P3) — doc ordering phrase does not match the control flow.**
`docs/port/p5-008-identity-stop.md:52` says the unreadable/evidence-changed checks run "before
any empty-group shortcut", but `SteamProcessLauncher.cs:241` returns the clean empty snapshot
first; the new checks are `:246-254` and `:257-263`. The invariant the sentence describes still
holds because `escaped` (`:224`) rejects a still-live tracked identity outside the group before
either shortcut. Pre-existing wording retained by this diff; worth a one-line correction to
avoid a future reviewer "fixing" the code to match the sentence.

**O4 (P3) — residual: non-wrapper Windows system images are game identities.**
`SteamLaunchEvidence.cs:104-118` is a hand-maintained exclusion list; a PE under the prefix that is
not listed and whose argv[0] is a drive path is accepted and signalled, e.g.
`C:\windows\system32\svchost.exe` (also `cmd.exe`, `dllhost.exe`, `taskhostw.exe`) with the
corresponding real file in `prefix/drive_c`. This is exactly the exclusion set the brief
prescribes, and the process is still inside this app's own prefix and fresh group, so it is
recorded as an accepted residual, not a defect.

**O5 (P3) — test gap on the new single-signal rule.**
`SteamLaunchTests.cs:586-609` (`Surviving_game_is_reported...`) asserts only that all signalled
identities equal the game identity, not that the count is one, so the new per-attempt
de-duplication is unasserted. The empty-group ordering (O3) is likewise untested. Behaviour is
correct by inspection; assertions would pin it.

**O6 (P3) — per-poll cost.** `LinuxExternalProcessHost.cs:52` reads `/proc/PID/cmdline` for
every running host process on every 250 ms poll, and `TryGetGame` re-resolves argv paths per
snapshot. Expected cost is small (cmdlines are short and non-drive arguments return before any
filesystem access), but it is a new whole-table read on the attribution hot path.

## Evidence-honesty audit

- The merged tree is identical to PR head `223bf69d`; the only commits between the acceptance
  code head `955260bc` and the merge head touch docs/shards. The doc's parenthetical "(later PR
  commits are docs-only)" is accurate.
- `docs/port/units/p5-008.queue.tsv` and the new `140-proton-attribution.tsv` are unit shards;
  the protected/aggregate paths (`docs/port/queue.tsv`, `docs/port/ledger.tsv`,
  `docs/port/surface-parity.tsv`, `tests/golden`, `tests/fixtures`, `tests/baseline.json`,
  `docs/formal`, `legacy/`) are untouched by the commit.
- The acceptance bullet list, the harness output it quotes, and the harness code agree
  (`EVIDENCE`/`CMDLINE`/`STOP REPORT`/`NO LAUNCH` shapes). `Flatpak`, launch options and
  Phase 5 are consistently disclaimed.
- The claim "Local validation on the code head: Core 2413 passed, Architecture 199 passed" and
  the linux-ci run citation were not re-executed in this review (no builds per brief); they are
  recorded as owner-reported evidence, not independently reproduced here.
- No protected-path or oracle change is implied by this review; this document is review
  commentary only.
