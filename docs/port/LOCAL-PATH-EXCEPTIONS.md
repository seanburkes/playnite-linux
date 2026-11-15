# Pre-guard local-path exceptions

The local-path guard starts from fixed main commit
`0b02bf9ff395a91b3524545074f53abe403effc2`, never a moving event base or HEAD.
New local home-directory, orchestrator data/state, and worktree references must use
repository-relative evidence instead. Home environment-variable and tilde forms, Windows
Users/Home paths, and conventional absolute roots are covered. The protected
`LOCAL_ABSOLUTE_ROOTS` tuple in the generator configures the root set.
The generator rejects an offending file and row without echoing its private path.
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

Existing documentation examples are likewise grandfathered by exact filename, full-line
SHA256, and occurrence count from that fixed commit. The generator reads only that immutable
snapshot, not the changed exception note or current branch, to calculate allowances; missing
baseline history fails closed. This covers historical operator commands, installation paths,
spike reports, and synthetic-fixture placeholders in the wider scan. It does not permit new
or edited lines or more copies than the baseline contained. Changing a baseline line requires
removing the local reference, not refreshing its exception. The four ledger rows above are
the complete ledger allowance; CI asserts the recorded list and aggregate hashes match.

Remote HTTP(S), FTP, and SSH URLs are removed before matching, including Markdown links and
code spans. A genuine local path elsewhere on the same line still fails; a file URI does not
receive this remote-URL exemption.

The scan covers Markdown, TSV, text, reStructuredText, and AsciiDoc under `docs/`, root Markdown,
and Markdown under `spikes/`, `tests/`, `tools/`, `packaging/`, and `src/`, including new files
before commit. Frozen `legacy/` and `oracle-lock/` trees and binary documents are outside this
text guard. Existing fixture Markdown is read, never modified. This note and the generator
are protected and code-owned; adding exceptions requires the protected policy review gate.
