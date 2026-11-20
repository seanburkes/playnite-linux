# p6-011 — CLI query surface

`list`, `search` and `launch --dry-run` are new `ICliCommand` files under
`src/Playnite.Cli/Commands/`. `CliCommandRegistry` discovers them from the
`Playnite.Cli` assembly, so `Program.cs`, the `import-v10` command and the usage text stay
unchanged. The commands read the p6-001 `LibraryService`; they never load Avalonia, never
use the network and never start a process.

## Command surface

```sh
Playnite.Cli list   --library <library.sqlite> [--offset N] [--limit N] [--sort <key>] [--descending]
Playnite.Cli search <term> --library <library.sqlite> [--offset N] [--limit N] [--sort <key>] [--descending]
Playnite.Cli launch --dry-run --library <library.sqlite> (--id <game-guid> | --name <display-name>)
```

`--library` is required and must name an existing SQLite library. A missing file is a
service error; the command never creates a database.

Every option is single-use. A repeated option is an invalid argument: the command names the
option, prints `--limit was specified more than once.` (with the command's usage text) on
stderr, writes no records and exits 2.

`list` returns every stored game, including hidden and uninstalled games, in ascending
name order by default. `search` applies the v10 text matcher to display names; a blank term
behaves like `list`. `--offset` is 0 to 1,000,000 and `--limit` is 1 to 10,000, matching the
`LibraryQuery` bounds; a null limit returns the remainder of the page. `--sort` accepts a
`Playnite.Core.Queries.SortOrder` name such as `Name`, `Playtime` or `LastActivity`, and
`--descending` reverses the primary sort key.

`launch --dry-run` resolves one game and prints its first stored play action. `--name`
requires a single case-insensitive exact display-name match; zero matches exit 3 and several
matches exit 2 with a request to use `--id`. It resolves `File`, `URL` and `Script` play
actions. A game with only `Emulator` play actions, a library-plugin action or no play action
exits 5 and prints the reason. `dryRun` is always `true` and `started` is always `false`; the
command only reports the plan.

## Output contract

Success output is JSON Lines: one complete JSON object per line, UTF-8, no wrapping array.
Every record has a `kind` field so a consumer can discriminate without positional state.

`list` and `search` print one `page` record followed by one `game` record per returned game:

```json
{"kind":"page","totalCount":3,"offset":0,"limit":2,"returned":2}
{"kind":"game","id":"10000000-0000-0000-0000-000000000001","gameId":"220","name":"Alpha","installed":true,"hidden":false,"favorite":false,"playtimeSeconds":3600,"playCount":2,"releaseYear":2019}
```

`totalCount` is the count before paging; `returned` is the number of `game` records that
follow. `gameId` is the provider identity, `id` the library GUID, `playtimeSeconds` a stored
playtime value and `releaseYear` an integer or `null`.

`launch --dry-run` prints exactly one `launch` record:

```json
{"kind":"launch","dryRun":true,"started":false,"id":"10000000-0000-0000-0000-000000000001","gameId":"220","name":"Alpha","installed":true,"actionType":"File","actionName":"Play","path":"/games/alpha/alpha.sh","arguments":"--fullscreen","workingDirectory":"/games/alpha","script":null,"fileExists":true}
```

`path`, `arguments`, `workingDirectory` and `script` are stored strings or `null`.
`fileExists` is `true` or `false` for a `File` action and `null` for other action types. A
missing executable is reported, not treated as a failure: the dry run still exits 0 because
the plan was resolved.

Field order is fixed by the writer and covered by tests; consumers should match by name.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Command completed and wrote its records. |
| 1 | Unknown verb or malformed top-level invocation; owned by `Program` and unchanged. |
| 2 | Invalid arguments: missing or duplicate options, an unknown option or sort key, an out-of-range offset or limit, no term, an invalid GUID, or both/neither `--id` and `--name`. |
| 3 | `launch` could not find the requested game identity. |
| 4 | Service error: the library file is missing, unreadable or not a valid library. |
| 5 | `launch` resolved the game but it has no supported play action. |

Errors are one line on stderr and no partial record is written; bad arguments and service
errors never print a stack trace.

## Tests

`tests/Playnite.Core.Tests/Cli/`:

| File | Covers |
| --- | --- |
| `ListCommandTests.cs` | page and game records, name order, sort and bounded paging, every bad-argument class including repeated options, missing library. |
| `SearchCommandTests.cs` | v10 matching, blank term, unpaged total, paging, bad arguments including repeated options, missing library. |
| `LaunchDryRunCommandTests.cs` | resolve by id and exact name, file/URL actions, never started, missing executable, missing and ambiguous games, missing/plugin/emulator play actions, bad arguments including repeated options, missing library. |
| `CliQueryCommandsTests.cs` | spawns the real built CLI and exercises the discovered command path end to end: `list`, `search`, `launch --dry-run`, unknown verb, bad and repeated arguments, and exit codes 1, 2, 3 and 5. |
| `CliQueryFixture.cs` | temporary SQLite libraries written through `GameRepository`. |

The commands are also reachable through `Program.RunAsync` without a dispatch edit because
the existing registry tests assert the assembly scan.
