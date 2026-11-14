# Gate P1 — Windows oracle baseline

- Date: 2026-11-14
- Owner: oracle
- Reviewer: independent plan review (session `ses_e27629994ffec0IK0wD5lgcnOT`); gate evidence pending captain review
- Run: https://github.com/seanburkes/playnite-linux/actions/runs/43604176690
- Result: **pass-with-exceptions**

## Evidence

- `legacy/source/Playnite.slnx` builds on `windows-latest` with .NET Framework MSBuild, x86,
  Release. No source changes to `legacy/`.
- `Playnite.Deps` 10.56.0 fetched from the pinned feed and verified against
  `oracle-lock/SHA256SUMS` (SHA-256 `f45b8c9f…d822`).
- VSTest ran the v10 NUnit suite: **282 executed, 282 passed, 0 failed, 0 skipped**.
- TRX and build binlog uploaded as the `legacy-oracle` artifact.
- `tests/baseline.json` records the executed count.
- `tools/LegacyOracle` builds on Windows and emits v10 serialized manifest bytes; goldens are
  checked in under `tests/golden/manifests/` (run 43605036209).

## Exceptions

| id | reason | owner | expiry |
| --- | --- | --- | --- |
| EX-1 | `FileSystemTests.CanWriteToFolderTest` assumes a non-admin Windows user; the runner is admin. | oracle | Revisit when the oracle runs as a standard user |
| EX-2 | `SafeFileEnumeratorTests.OverUnsafeWorksTest` assumes a non-admin Windows user; the runner is admin. | oracle | Revisit when the oracle runs as a standard user |

Both are Windows ACL tests, not portable app logic. They are excluded by VSTest filter, not by
editing `legacy/`; see `tests/legacy-exclusions.md`.

## Still required for Phase 1 exit

- [x] Old app builds and tests run on Windows CI.
- [x] Executed cases recorded from a TRX file.
- [x] Oracle harness constrained to v10 APIs emits deterministic bytes (manifest v0).
- [ ] Fixture v10 library migrated and verified.
- [ ] UI reference images captured (human only).
- [ ] Steam real-artifact corpus captured/redacted.
- [ ] LiteDB spike result recorded (passed).
- [ ] All protected paths guarded (workflow in place; CODEOWNERS in place).
