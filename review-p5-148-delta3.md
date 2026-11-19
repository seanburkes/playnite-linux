# Review — PR #148 final delta (p5-008, unlinked relative argv)

Reviewed: https://github.com/seanburkes/playnite-linux/pull/148
Delta diff (`delta3.diff`) sha256: `3651baf5cb501fe73042e9843d30ac66a1d19d12383d6dd24fac7f72b373be94`
Reviewed head: `11597a9c95053bdadf070af8a56b81078da85672` (requested delta head, `083e2439..11597a9`)
Current PR head at review time: `cfce3502f9720e1824c10532c81c7d1add85c550` (one docs-only ledger-fold commit after the reviewed head; see P2)

Scope: only `083e2439..11597a9` — 5 files, 161 insertions, 2 deletions: the AppImage acceptance doc, the `p5-008.ledger.d/149-…` fragment, the `p5-008.queue.tsv` shard, `SteamAcceptanceSurvivorTests.cs`, and `SteamAcceptanceSurvivorScan.cs`. No other unit or reviewer artifact was read. No build was run, per the brief.

**Verdict: one blocking P2 at the reviewed head (`unit-ownership`), no P1 and no runtime/code defect. The P2 is already removed at the current head `cfce350`, so if the merge is taken at `cfce350` or later there are no blocking findings from this delta.**

## P2 (blocking at `11597a9`) — the delta adds a second per-PR ledger fragment

File: `docs/port/units/p5-008.ledger.d/149-appimage-acceptance.tsv:1` (new fragment; row at `:2`). The sibling `148-appimage-acceptance.tsv` is already in the PR base, and `docs/port/units/p5-008.queue.tsv:2` is the one allowed queue shard.

Concrete failure: relative to the fork point `5507699`, PR #148 adds both `148-appimage-acceptance.tsv` and `149-appimage-acceptance.tsv`. The pull-request gate allows exactly one new `<its-pr>-<short-slug>.tsv` fragment per PR. Reproduced with the exact CI command at `11597a9`:

```text
$ PORT_INDEX_PR=148 python3 scripts/port/build_port_index.py check --shards-only \
    --against 5507699 --forbid-aggregate-changes --recorded-base 5507699
error: pull requests must not modify generated or bot-owned files. …
  ownership: docs/port/units/p5-008.ledger.d/149-appimage-acceptance.tsv:
    a PR may only add one immutable <its-pr>-<short-slug>.tsv ledger fragment
  ownership: a PR may add exactly one ledger fragment
exit=1
```

The rule is `scripts/port/build_port_index.py:1728-1739` (name must match the PR number) and `:1792-1793` (exactly one fragment); the required check is `.github/workflows/plan-guards.yml:286-332` (`Port aggregates are generated on main`, surfaced as `unit-ownership`). At the reviewed head that check cannot pass, so PR #148 cannot merge at `11597a9`.

The current head already clears it: `cfce350` deletes the `149-…` fragment, folds its row into `148-appimage-acceptance.tsv`, and returns the queue owner to `fm/p5-008g`. The same command at `cfce350` exits 0 (`port index shards are valid; aggregates are generated on main`), and `gh-axi pr checks 148` reports `unit-ownership,pass`. I did not review that commit's content beyond confirming the shard gate.

## The requested code delta is otherwise sound

### Relative argv is judged lexically against the process's own cwd

`tools/p5-008/SteamAcceptance/SteamAcceptanceSurvivorScan.cs:55-71`. Absolute paths keep the pre-existing `GetRealPath` path with the lexical fallback (`:73-83`); Windows drive arguments still go through `ProtonCommandLine.ResolveAppPath` (`:58-61`, implemented unchanged at `src/Playnite.Steam/p5-008/SteamLaunchEvidence.cs:163-175`). A relative argument that has no drive-map resolution now returns `InAppRoots(Path.GetFullPath(Path.Combine(workingDirectory, path)))` (`:68-70`), and `InAppRoots` accepts the exact install dir/prefix or anything under them (`:86-87`). This is exactly the claimed behaviour: a missing target no longer suppresses app-path evidence.

### The survivor is never signalled

`Find` is read-only: it projects the `after` snapshot and returns entries (`:15-47`); there is no signal call in the file. The harness prints `unsignalled by the scan` (`tools/p5-008/SteamAcceptance/Program.cs:206`), and the regression asserts the worker is still alive after the scan (`tests/Playnite.Core.Tests/p5-008/SteamAcceptanceSurvivorTests.cs:141-142`).

### The regression fails pre-fix and passes post-fix

`SteamAcceptanceSurvivorTests.cs:96-154`. The worker calls `os.setsid()` before printing `ready` (`:105-110`), so its PGID is its own PID and cannot match the `Environment.ProcessId` passed as the group at `:138-139`. Baseline is empty, the observed set is empty, and `/usr/bin/python3` is not under the install/prefix, so the only surviving branch is the relative argv evidence. The pre-fix predicate (`:205-244`) is a faithful copy of `083e2439`'s `HasAppPath` for argv: it combines the cwd and requires `GetRealPath`, which returns null for the deleted `worker.py`, so `Assert.False` at `:136` holds; the fixed scan reports the row at `:141`. This is a static trace (no build per the brief), but the two outcomes follow from the deleted target and the captured absolute cwd. The existing `Detached_worker_with_relative_argv_under_its_install_directory_is_a_survivor` (`:46-94`) still covers the existing-target case.

### Scope, protected paths, skips

The delta is confined to `p5-008` docs/shards, its test file, and its acceptance tool. No `tests/golden`, `tests/fixtures`, `tests/baseline.json`, `docs/formal`, `docs/port/surface-parity.tsv`, aggregate queue/ledger, or `legacy/` change. No `[Skip]`/`[Ignore]` is added; the delta adds one `[Fact]`, consistent with the test-count movement. The AppImage acceptance rerun (four flows, zero survivors) and linux-ci run `44444538201` were not re-executed here; only the static/diff claims were checked.

## Non-blocking observation (P3)

`SteamAcceptanceSurvivorScan.cs:68-70` replaces realpath for **all** relative argv paths, not only when the target is missing. Concrete input: cwd `/tmp`, argv `./game`, where `/tmp/game` is a symlink to `/steamapps/common/Game/game`; pre-fix realpath yielded the install path and the process was reported, now the lexical `/tmp/game` is outside the roots and only the `Executable` check can catch it. A wrapper whose own executable is outside the install is therefore missed in that shape. A minimal fix is to try `files.GetRealPath(combined)` in the existing try/catch and use `InAppRoots(resolved ?? combined)`. I do not treat this as blocking: the requested delta is explicitly lexical, and the shipped acceptance run reported zero survivors; it is a narrowing of the pre-fix predicate worth a follow-up decision, not a merge blocker.
