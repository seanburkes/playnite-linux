# Legacy oracle exclusions

The Windows oracle runs the v10 NUnit suite. Two tests assume a **non-administrator** Windows
user and test Windows ACL behaviour, not portable application logic:

| Test | Why excluded | Owner |
| --- | --- | --- |
| `FileSystemTests.CanWriteToFolderTest` | Asserts `c:\Windows\` is not writable. GitHub's Windows runner is an administrator, so the assertion is false. | oracle |
| `SafeFileEnumeratorTests.OverUnsafeWorksTest` | Asserts `DirectoryInfo.GetFiles` throws `UnauthorizedAccessException` under `c:\Windows\appcompat`. The runner has access. | oracle |

These are excluded by VSTest filter, not by editing `legacy/`. The Linux port has its own
permission/path tests; Windows ACL behaviour is not a port target.

`FileSystemTests.CheckSumTests` is **not** excluded: it failed because Git's `core.autocrlf`
changed `TestIni.ini` on Windows. `.gitattributes` now marks `legacy/**` as `-text`, preserving
the exact bytes.

A future improvement is to run the oracle as a standard user; until then, the exclusion is
recorded in the Phase 1 gate artifact and the executed-case baseline.
