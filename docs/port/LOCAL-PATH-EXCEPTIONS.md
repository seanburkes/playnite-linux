# Pre-guard local-path exceptions

The local-path guard starts from main commit `0b02bf9`. New local home-directory,
orchestrator data/state, and worktree references must use repository-relative evidence
instead. The generator rejects an offending file and row without echoing its private path.
The CI policy suite also checks documentation. Both build and check validate documentation
and every shard before writing aggregates.

Four immutable ledger rows are accepted pre-guard exceptions, not permission for new leaks:

| Unit | Timestamp | SHA256 of the exact UTF-8 row, excluding newline |
| --- | --- | --- |
| p2-002 | 2026-11-15T05:49:30Z | `b02112a1a036cb33f262c8f392a4d3a9e5d21fc4022d817d3599337f97aabc29` |
| p2-002 | 2026-11-15T06:30:00Z | `352d5a6b7b0be9aa4875060cea973c62b7a0384b827ba8606d965e0f230e0e01` |
| p2-002 | 2026-11-15T06:49:00Z | `d28753330deb3b3927270edbffdd380541f924cfd7f9982d314705fc9a4508c8` |
| p2-ci-cost | 2026-11-15T19:36:12Z | `1e65eafe7f62f8192f5ac778f624dd69ae23450766b449c687ad0f5ec5d24207` |

Each row is allowed once in its original unit ledger and once in the generated aggregate.
The exception is bound to both its file and full content: copying it elsewhere, modifying
it, or appending it twice does not inherit acceptance. No historical ledger row is edited.

Existing documentation examples are likewise grandfathered by exact line hash and filename
in `scripts/port/build_port_index.py`, not by path prefix or a moving comparison base:

| Existing document | Accepted line numbers at the baseline |
| --- | --- |
| `06-OPERATIONS.md` | 30, 31, 56 |
| `07-FIRSTMATE.md` | 12, 25, 31 |
| `08-STEAM-SPEC.md` | 24 |
| `HANDOFF-2026-11-14.md` | 7, 11, 95 |
| `p1-009-steam-corpus-proposal.md` | 92, 94, 150, 177, 178, 184, 185, 193, 194, 203, 262 |

These include historical operator commands and synthetic-fixture home placeholders.
Changing those lines requires removing the local reference, not refreshing the exception.
The scan covers Markdown, TSV, text, reStructuredText, and AsciiDoc under `docs/`, plus root
Markdown files, including new files before they are committed. Frozen oracle data is not
scanned or modified. Binary documents are outside this text guard's scope.
