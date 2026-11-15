# Legacy oracle exclusions

The Windows oracle runs the v10 NUnit suite. Two tests assume a **non-administrator** Windows
user and test Windows ACL behaviour, not portable application logic. A third is a flaky
Windows shell test:

| Test | Why excluded | Owner |
| --- | --- | --- |
| `FileSystemTests.CanWriteToFolderTest` | Asserts `c:\Windows\` is not writable. GitHub's Windows runner is an administrator, so the assertion is false. | oracle |
| `SafeFileEnumeratorTests.OverUnsafeWorksTest` | Asserts `DirectoryInfo.GetFiles` throws `UnauthorizedAccessException` under `c:\Windows\appcompat`. The runner has access. | oracle |
| `ProcessStarterTests.ShellExecuteTest` | A Windows `notepad`/`taskkill` shell test whose `taskkill` returned 1 instead of 0. It passed in the 282/282 baseline, so this is environment flakiness, not a missing check or oracle-setup gap. | oracle |

These are excluded by VSTest filter, not by editing `legacy/`. The Linux port has its own
permission/path tests; Windows ACL behaviour is not a port target.

**EX-3 captain authorisation.** The captain explicitly authorised quarantining the flaky
`ProcessStarterTests.ShellExecuteTest` and accepting an executed baseline of **281 executed /
3 excluded** (down from the earlier 282/2). The executed-count drop is therefore a recorded
captain decision about environment flakiness, not a weakened test rule: the test is filtered by
name, `legacy/` is unmodified, and no other case was removed or skipped.

`FileSystemTests.CheckSumTests` is **not** excluded: it failed because Git's `core.autocrlf`
changed `TestIni.ini` on Windows. `.gitattributes` now marks `legacy/**` as `-text`, preserving
the exact bytes.

A future improvement is to run the oracle as a standard user; until then, the exclusions are
recorded in the Phase 1 gate artifact and the executed-case baseline.
