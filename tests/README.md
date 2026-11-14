# Tests

Layout:

```
tests/
  fixtures/     synthetic + redacted real inputs (v10 library, Steam artifacts)
  golden/       expected bytes / snapshots, with a manifest and regeneration notes
  baseline.json executed-case counts, skips, coverage (Phase 2+)
  screens/      headless Avalonia UI baselines + semantic trees
```

Rules:

- Fixtures and goldens are oracle artifacts. Do not edit them in a porting change; use an
  `oracle-change` PR with a second reviewer.
- Regeneration is a human-run script, not an auto-accept flag.
- Every golden has a provenance note: source, capture command, and what it proves.
- A test that would pass with the implementation deleted is not a test.
