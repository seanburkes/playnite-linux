# Replacement crash matrix

This is additive protocol evidence, not output from the Windows oracle. Expected
visibility derives from the unchanged `docs/formal/LibraryImport.tla` actions
`PublishPartial`, `Publish`, `Crash`, `Recover` and invariants `AtomicLive`,
`DurableReceipt`, `CrashSafe`, `ReaderNeverPartial`, `CleanResumable`.

Before the atomic rename, SIGKILL preserves the complete old artifact. After
rename, SIGKILL releases the exclusive publication lease and exposes the complete
new artifact with its already-flushed receipt. Managed failures before lease
release can instead restore the old file while readers are excluded.

`LibraryReplacementTests.cs` consumes every matrix entry in real killed child
processes. New rows/data come from the unmodified Windows-produced
`tests/fixtures/v10/library`, SHA256SUMS and provenance recorded by p1-003
(Windows run 43650013648, artifact 14135728206). The old one-game library is
explicitly synthetic destination setup, not a claimed v10 oracle.
