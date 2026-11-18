# Review — PR #141 (p5-008c2, group-change stop seam)

Reviewed: https://github.com/seanburkes/playnite-linux/pull/141
Candidate diff sha256: `093524fc6193a182c0d5e4fde7049895d71bebd34a9000a8fbb6537bdc40581b`
Reviewed head: `fc3ebd8d0718a4cd36a9df27d133767925bb55f4` (PR head, open, not draft)
Code head: `83f4c146ab7c7b468d16d331f71582b1a1a3a5a9` (the two commits after it, `a910810` and `fc3ebd8`, are docs-only: the contract addendum and the 141 ledger fragment).

Scope: this diff only (6 files: 3 docs/shards, host, attribution comment, tests). No other unit reviewed.

**Verdict: no blocking P1/P2 findings.** The P2-1 seam is closed, both new tests genuinely fail on the pre-fix host, the P2-2 comment now matches the classifier, the acceptance claim is qualified honestly, and the shard/protected-path/skip checks hold.

## Attack results

### P2-1 — row is no longer dropped on a same-ticks PGID change

Base host (`ceff167`, `src/Playnite.Platform.Linux/p5-008/LinuxExternalProcessHost.cs:56-60`) only added a row when start ticks **and** PGID matched, so a `setsid` between the observer read and the identity re-read silently dropped the row and returned `complete=true`. The fix (`fc3ebd8`, `src/Playnite.Platform.Linux/p5-008/LinuxExternalProcessHost.cs:63-83`) splits the test:

- ticks differ → row dropped (PID recycle), unchanged.
- ticks equal, PGID changed → row **retained** with the re-read identity (`:77-81`) and `complete=false` (`:76`).
- otherwise → normal retained row (`:85`).

The retained row is visible to the escape check because `SteamProcessAttribution.SameProcess` compares only PID and start ticks (`src/Playnite.Steam/p5-008/SteamLaunchEvidence.cs:166-167`), so a known identity at PGID 20 matches the same PID/ticks now at PGID 30. Both launch-time consumers then refuse:

- `SteamProcessLauncher.SnapshotAsync` returns `([], false, "A tracked Steam child left its group…")` (`SteamProcessLauncher.cs:210-217`), before the empty-members shortcut (`:228-231`).
- `SteamProcessLauncher.StopAsync` throws on `!sample.IsComplete` before any signalling (`:275-279`); the terminal report at `:320-325` is unreachable. Signal order (`games` only, `:350-354`) also cannot touch the escaped group.

Checked the case the host flags and the case it does not: if the game had already moved before the snapshot, both reads agree on PGID 30, the sample is complete, and the same escape check still fires because it compares against the launch-time PGID (`:211-217` in `SnapshotAsync`, `:283-290` in `StopAsync`). That path is already covered by the pre-existing `Escaped_observed_child_degrades_and_is_never_signalled_in_an_unrelated_group` test. A clean stop report with the game still alive was not reachable in either shape.

**Do the new tests fail on the old code?** Yes, by construction:

- `Same_start_ticks_in_a_changed_group_is_retained_and_incomplete` (`tests/Playnite.Core.Tests/p5-008/LinuxPidFdSignalTests.cs:114-128`) reads `firstGroup=20` for the observer and `secondGroup=30` for the re-read (`:210-226`). On the old condition the row is dropped, so `Assert.False(IsComplete)` and `Assert.Single` both fail; on the fix the row is the re-read identity `(pid, 2, 30)`.
- `Launcher_seam_a_group_change_between_reads_cannot_produce_a_clean_stop` (`:130-157`) alternates old group (odd read) and new group (even read) after `Escaped = true` (`:237-250`). The host does exactly two stat reads per snapshot for this PID (one in `LinuxProcessObserver`, one re-read), so each snapshot reproduces the disagreement. On old code the snapshot is `([], true)` and the first assertion fails; on the fix the snapshot is incomplete with the escape reason, `StopAsync` throws `SteamAttributionUnknownException`, `StopReport` is null and `seam.Signalled` is empty.

The seam reader is deterministic (only the target PID increments; every other PID returns null and does not disturb parity), so this is not a timing-dependent test.

### P2-2 — comment matches the classifier

`GroupRejection`'s new comment (`SteamLaunchEvidence.cs:169-179`) says an unreadable live member is tolerated and never signalled, and that a survivor is identified only with parent evidence (baseline Steam client, attributed game identity, or an identified helper). That is what `SteamProcessLauncher.ClassifySurvivors` implements (`SteamProcessLauncher.cs:418-464`): `ownedParentPids` comes from running baseline `IsSteamClient` rows plus `known`/`descendants` (`:421-433`), and an unreadable executable (`IsInfrastructure(null) == false`) can only be identified through a parent in that set (`:447-455`). `GroupRejection` itself only adds the member to `tolerated` and never signals or labels it. The existing tests `Unreadable_survivor_without_evidence_is_unidentified_and_not_a_helper_claim` and `Unreadable_survivor_with_observed_steam_parent_is_an_identified_helper` cover both sides.

### Acceptance/evidence honesty

- The doc addendum (`docs/port/p5-008-identity-stop.md:138-146`) and the 141 ledger fragment agree on the four flows, timings (3.9/2.2 and 3.3/3.0), the exact `degraded=False` reason, and the PIDs.
- The Proton limitation is stated, not hidden: 105600 is `Terraria.bin.x86_64`, "not a Proton/Windows-binary shape", "still not Proton/Windows-binary stop-shape proof", and "no Phase 5 exit or Flatpak proof is claimed".
- The cited linux-ci run checks out: `gh api …/actions/runs/44400284691` reports `linux-ci` `status=completed`, `conclusion=success`, `head_sha=83f4c14…`.
- I ran the read-only claim at the reviewed head: `python3 scripts/port/build_port_index.py check --shards-only --against origin/main` exits 0 ("shards are valid").
- No solution build was run, per the brief.

### Scope guards

- No `p5-004` (or any other unit) change; the diff is confined to `p5-008` source, its test file, its contract doc and its unit shards.
- No protected path (`tests/golden`, `tests/fixtures`, `tests/baseline.json`, `docs/formal`, `docs/port/surface-parity.tsv`) and no `legacy/` change. The new fragment uses the per-PR naming convention (`141-group-change.tsv`), which the index gate accepts.
- No `[Skip]`/`[Ignore]` added; both new tests are plain `[Fact]`. Count movement 2376 → 2378 is consistent with exactly two added tests.

## Non-blocking observation (P3, not a merge blocker)

`LinuxExternalProcessHost.cs:76` marks the **whole** snapshot incomplete for a PGID change on any sampled row, not just a tracked one. Concrete input: rows `game=(22,2,20)`, unrelated `(90,7,50)` whose re-read reports `(90,7,51)`; the host returns `IsComplete=false`, so `SteamProcessLauncher.StopAsync` (`:276-278`) throws UNKNOWN for a healthy game that the old code stopped normally. It is fail-closed, the `GameSessionTracker` retries the stop, and the flag is what keeps `IsRunningAsync` (`:194-199`) from reporting a moved game as not running; I do not treat it as blocking, but it is a real availability trade-off worth a future scoping decision.
