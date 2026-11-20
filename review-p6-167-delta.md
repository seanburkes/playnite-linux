# Review — PR #167 delta (p6-011 duplicate CLI options)

- PR: https://github.com/seanburkes/playnite-linux/pull/167
- Reviewed delta: `77045f7..56747e7`, head `56747e755d9a7c184dfa38bfb63b5aeed086ebaa`
  (`1b89764` fix + `56747e7` evidence row)
- Reviewed diff: `data/p6-167/delta.diff`, sha256
  `c070d51c66b989d64c270e4b90c95ae69ab44bf5852b04c108e9d14928954f1b`
  (9 files, +56/−5). Recomputed `git diff 77045f7 56747e7` is byte-identical.
- Scope: focused delta only. Static review of the diff and the head tree; no build, no test
  execution, no other reviewer input.

## Verdict

**PASS — no blocking P1/P2 findings.**

The fix rejects a repeated option on every flag the three query commands define, with the
documented message, usage line, empty stdout and exit 2; single-use parsing is untouched;
the tests would fail if the guard were removed; and the only change beyond the fix is the
Port-Unit evidence row.

## 1. Every multi-use flag and both booleans are guarded

`list` and `search` share `QueryOptions.Parse` (`src/Playnite.Cli/Commands/QueryOptions.cs`):

- Guard calls at `--library` :28, `--offset` :32, `--limit` :36, `--sort` :40,
  `--descending` :44.
- The guard is the first statement in each `case`, before `Value`/`Bounded` run, so a second
  occurrence fails even when its value is invalid or missing.
- `search` strips only the positional term before parsing (`SearchCommand.cs:25-26`), so its
  term-first syntax reaches the same guarded parser.

`launch` (`src/Playnite.Cli/Commands/LaunchDryRunCommand.cs`):

- Guard calls at `--library` :146, `--id` :150, `--name` :160, `--dry-run` :164.

Enumerating every `case "--…"` in both parsers shows no unguarded flag, and those are all the
options the three commands define. `--version` is a `Program` flag outside the documented
p6-011 surface (see non-blocking note).

## 2. Documented exit code and message

- `OptionGuard.Once` (`src/Playnite.Cli/Commands/OptionGuard.cs:9-15`, throw at :13) throws `UsageException`
  with `"<option> was specified more than once."`, a newline, and that command's usage text.
- All three commands catch `UsageException` before writing any record and return
  `QueryExitCodes.InvalidArguments` = 2 (`QueryCommandSupport.cs:11`;
  `LaunchDryRunCommand.cs:40-43`, `ListCommand.cs:26-29`, `SearchCommand.cs:32-35`).
- The docs state exactly this behaviour and wording (`docs/port/p6-011-cli-query.md:20-22`:
  names the option, prints `--limit was specified more than once.` with usage on stderr,
  writes no records, exits 2).
- The spawned-CLI test proves the real path through `Program`'s unchanged dispatch:
  `CliQueryCommandsTests.cs:73-76` asserts exit 2, empty stdout and the message on stderr.

## 3. Single-use parsing unchanged

The hunks are additive only: a `seen` set plus one guard call per option
(`QueryOptions.cs:22`, `LaunchDryRunCommand.cs:140`). Every existing parse, range check, sort
lookup and validation line is byte-identical to `77045f7`, and `Program.cs`, the dispatch,
the usage text and the exit-code mapping are untouched. The existing bad-argument rows around
the new blocks still exercise the single-use behaviour.

## 4. Tests cover the duplicates and fail without the guard

- `ListCommandTests.cs:102-106` — `--limit`, `--offset`, `--sort`, `--descending`, `--library`.
- `SearchCommandTests.cs:92-96` — the same five through the term-first form.
- `LaunchDryRunCommandTests.cs:118-121` — `--id`, `--name`, `--library`, `--dry-run`.

Each row runs through the shared theory (`ListCommandTests.cs:83`, `SearchCommandTests.cs:74`,
`LaunchDryRunCommandTests.cs:101`) and asserts exit 2, empty stdout and the message. With the
guard removed the second value simply wins and the run proceeds: the in-process rows reach the
missing `/tmp/library.sqlite` and exit 4 instead of 2, so every row fails its exit-code
assertion. The end-to-end row (`CliQueryCommandsTests.cs:73-76`) uses a real library, so
without the guard it would print records and exit 0 — it is guard-dependent independently of
the missing-file path, which also proves the "writes no records" clause.

## 5. Nothing beyond the fix

- `git diff 1b89764 56747e7` is exactly one added ledger row:
  `docs/port/units/p6-011.ledger.d/167-cli-query-surface.tsv:3`. The docs diff
  (`docs/port/p6-011-cli-query.md`) describes the fix and updates the test-coverage table; it
  changes no product or test behaviour.
- Evidence row checked against the forge: run `44659922819` is `success` on
  `1b89764663be34253978ca0381e1fdf9f832c1a8`, and its step log reports Architecture 199, Core
  2599, Smoke 4, zero failed and zero skipped, matching the row and the commit message.
- Head commit `56747e7` carries the `Port-Unit: p6-011` trailer. PR #167 is OPEN, not a draft,
  at head `56747e7` as read from the forge during this review.

## Non-blocking observations

- `--version` is a `Program`-level flag outside the three commands and is not guarded; a
  repeated `--version` still lands on the usage path with exit 1. Out of the documented
  p6-011 surface; no change requested.
- Duplicate detection runs before value validation, so `--limit 0 --limit 0` reports the
  duplicate rather than the range error. Both are exit 2 and the contract does not order
  them.
