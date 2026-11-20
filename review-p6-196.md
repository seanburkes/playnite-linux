# Review — PR #196: p6-007 URI handler and HTML description service

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/196
- Reviewed head: `3242778edf40c856e1c1e710685a320e7d6f0ff4` (`3242778`), base `main`
- Reviewed diff: `data/p6-196/candidate.diff` (13 files, 3088 additions, 0 deletions)
- Diff sha256: `d97d46032043e48628f86106980e16227737be726665bd738fd87fd79d3e8050`
- Scope: `docs/port/p6-007-uri-html-description.md`, the p6-007 queue shard and ledger
  fragment, `Playnite.Core.UriHandling` (`PlayniteUriRoute`, `UriHandlerApi`),
  `Playnite.Core.Descriptions` (`HtmlDescriptionSanitizer`, `HtmlDescriptionService`) and
  the five new test files.

## Verdict

**No blocking P1/P2 findings.** I found no URI-parse escape, no dispatch path that can
reach a shell or an unintended target, no sanitizer bypass, no unbounded input, no UI or
network dependency, no weakened test and no protected-path edit. Approve for the captain's
merge decision. The observations in the last section are advisory and can be follow-ups.

## What I verified

### URI parse and validation (`src/Playnite.Core/UriHandling/PlayniteUriRoute.cs`)

- Scheme is required with `StartsWith("playnite://", OrdinalIgnoreCase)`; `playnite:/`,
  `https://…`, `playnite://` and bare `playnite://playnite` are rejected
  (`:248`, `:261`). `?`, `#` and `\` are rejected anywhere, not stripped (`:254`).
- The source is an allowlisted charset of 1–64 ASCII letters, digits, `.`, `_`, `~`, `-`;
  `.` and `..` are rejected (`:316`). `/` cannot appear inside it because the URI is split
  on `/` first, and `@`/`%` are not in the charset, so an authority-confusion form such as
  `playnite://user@host/…` cannot parse.
- Raw control characters and unescaped whitespace are rejected over the whole URI (`:232`),
  and a decoded control character rejects the argument (`:627`). That also keeps the
  error strings (which echo the decoded command and filter field) free of CR/LF/ANSI.
- Percent escapes are validated as hex before use and decoded as bytes, then decoded as
  strict UTF-8 with `UTF8Encoding(false, throwOnInvalidBytes: true)` (`:569`–`:637`), so
  `%GG`, a lone `%`, `%FF` and a surrogate-encoded `%ED%A0%80` all fail rather than becoming
  replacement characters. `+` means space, matching v10 `HttpUtility.UrlDecode`.
- Every route narrows to a typed target: `start` requires a GUID and an optional
  `0`–`999` action index parsed with `NumberStyles.None` (`:454`), `showgame` exactly one
  GUID, `search` at most one bounded term, `view` exactly `desktop|fullscreen`, `filter`
  exactly one allowlisted field and one value validated per kind. `creatediag` and
  `installaddon` are explicit rejections; any other command is rejected (`:417`–`:429`).
- Bounds are enforced before allocation-heavy work: 2048 URI characters, 32 arguments,
  1024 per argument, 256 per search term/filter value, 64 per source.
- `TryParse` itself has no throw path for hostile input (no indexing without a length guard,
  `Guid.TryParse`/`int.TryParse` used throughout, decode wrapped in a narrow `catch`), so a
  malformed URI cannot escape the dispatch entry point.

### Dispatch (`src/Playnite.Core/UriHandling/UriHandlerApi.cs`)

- Malformed input returns `Rejected` before either the host or a handler is consulted
  (`:188`). Plugin sources are dispatched even when no host is registered; app routes
  without a host return the documented `Unavailable` + `MissingHostDiagnostic` (`:196`).
- The host seam is typed (`Guid`, enum, record); there is no string command, no
  `Process.Start`, no shell string and no path construction anywhere in the new code.
- Host and handler exceptions become `Failed` with the exception type; caller cancellation
  is rethrown (`:225`, `:259`); a host that returns `null` maps to `Failed`.
- Registration is case-insensitive and lock-protected, rejects the reserved source, invalid
  names, null handlers and duplicates; removal of an absent source is a no-op, which matches
  the v10 contract (`legacy/source/Playnite/PlayniteUriHandler.cs:31`–`:58`). `RegisteredSources`
  takes its snapshot under the same lock.

### Sanitizer (`src/Playnite.Core/Descriptions/HtmlDescriptionSanitizer.cs`)

- The output language is closed: text is entity-decoded and then re-escaped for `&`, `<`, `>`
  with control characters removed (`:450`); element names come from the fixed allowlist; end tags are
  emitted from the parse stack only; and the only attribute emitted is `href` on `a`, with
  `rel="noreferrer noopener"` (`:185`–`:198`). There is no `style`, `src`, event handler,
  `target` or class output path.
- Href validation decodes entities first, trims, rejects empty/oversized/control-bearing
  values, requires an absolute URI with a non-empty host and no user info, and keeps only
  `http`/`https` (`:430`). `jav&#x61;script:`, `data:`, `file:`, protocol-relative,
  userinfo and relative targets all fail; the final `AbsoluteUri` is additionally escaped by
  `AppendAttribute`, so a quote cannot break out even if a future `Uri` normalisation left
  one in place.
- Active and foreign elements (`script`, `style`, `iframe`, `object`, `svg`, `math`, …) are
  dropped with their contents, so source text cannot surface as display text for those
  elements. Misnested end tags are rebalanced against the open stack; unknown end tags are
  dropped.
- Totality: comments/doctypes/PIs are skipped without recursion; the input is truncated at
  256 KiB, the output at 512 KiB, depth at 64 and href at 2048 (`:38`, `:44`); the tag reads
  are bounded and guarded (`TryReadAttributes` returns `false` on an unterminated tag, which
  ends the parse and closes the open stack). I found no input that throws.
- I traced the usual bypass families through the parser by hand: uppercase/mixed-case script,
  nested `<scr<script>ipt>`, entity-encoded schemes and controls, quoted `>` inside
  attributes, unquoted attribute values, CDATA/doctype smuggling, unclosed raw-text elements
  and depth bombs. None survives as markup; the worst outcomes are conservative over-drops.

### Description service (`src/Playnite.Core/Descriptions/HtmlDescriptionService.cs`)

- Provider results map as documented: null result → `provider-invalid-response`; failure
  with a reason → its stable reason; missing/blank → `no-description`; sanitizer-empty →
  `sanitized-empty`; exception → `provider-exception: <Type>`; caller cancellation
  propagates before and after the call (`:89`–`:152`).
- The service holds no socket, file or timer; the sanitizer is injectable and the default is
  the total one above.

### Evidence, ownership and constraints

- I re-derived the test counts from the diff: 63 route cases + 39 dispatch cases = **102**
  URI handling, and 47 sanitizer + 15 service cases = **62** descriptions, **164** total —
  matching the unit doc and the ledger fragment. No test is skipped, filtered or deleted;
  all test files are new.
- The diff adds only new files: no existing test, no `legacy/` file, no oracle path, no
  aggregate `docs/port/queue.tsv`/`ledger.tsv`. It adds the p6-007 queue shard row
  (`todo`→`done`, owner `fm/p6-007` matching the PR branch) and one
  `p6-007.ledger.d/196-…tsv` fragment naming this PR and the linux-ci run.
- No Avalonia/UI reference, no network call, no child process, no shell, no banned
  `System.Windows`/`System.Drawing`/`Win32`/interop symbol, and no new package reference.
  `src/Playnite.Core/Playnite.Core.csproj` uses default item globbing, so the new
  `Descriptions/` and `UriHandling/` directories compile without a project edit.
- The `Filter` field table maps onto the stored properties of
  `src/Playnite.Core/Queries/FilterSettings.cs`; `MetadataDocument.Description` exists in
  p6-005, so the documented provider seam is real.

## Non-blocking observations (P3, not merge-blocking)

1. **A void `embed` swallows the rest of the description.**
   `src/Playnite.Core/Descriptions/HtmlDescriptionSanitizer.cs:64` lists `embed` in
   `DropContentElements`, but `embed` is a void element and
   `SkipElementContent` (`:401`) then searches for a `</embed>` that never comes and returns
   end-of-input. Concrete input:
   `Sanitize("<p>a</p><embed src=\"x\"><p>b</p>")` returns `"<p>a</p>"`; `<p>b</p>` is
   silently discarded. No safety impact (it over-drops), but it is undocumented content loss
   on a plausible scraped description. Treat `embed` as tag-only removal and add a case to
   `HtmlDescriptionSanitizerTests`; the current tests only exercise `embed` with a closing
   tag via the shared active-content theory.

2. **Truncation can split a surrogate pair.** `:113` slices at 256 KiB of UTF-16 code units;
   if the boundary lands between a high and low surrogate the output keeps a lone high
   surrogate. Harmless in memory, but a later UTF-8 encode yields U+FFFD. Trimming a trailing
   high surrogate at the cut is a one-line hardening.

3. **The allowlist is publicly castable.** `:50` exposes `AllowedElements` as
   `IReadOnlySet<string>` over a mutable `HashSet<string>`; same-process code can cast it
   back and mutate a security allowlist. This is not remotely exploitable (attributes are
   still filtered, `DropContentElements` is checked first, so even `"img"` only yields an
   attribute-less tag), but a `FrozenSet`/immutable backing would make the invariant real.

4. **Empty segments are silently dropped.** `PlayniteUriRoute.cs:260` uses
   `Split('/', RemoveEmptyEntries)`, so `playnite:///playnite/search/x` and
   `playnite://playnite//search/x` parse exactly like the documented
   `playnite://<source>/<argument>…` shape. Nothing unsafe survives (every remaining segment
   is validated), but the acceptance is wider than the documented grammar. Either reject an
   empty authority/segment or document the tolerance.

5. **The doc overstates traversal rejection.** `docs/port/p6-007-uri-html-description.md:56`
   says "Traversal segments … are rejected", but only the source segment is checked:
   `playnite://my-plugin/../../etc/passwd` parses as `CustomSource` with arguments
   `["..", "..", "etc", "passwd"]`. No path is constructed here and plugin handlers receive
   data, so there is no escape today; the sentence should say source-level only so plugin
   authors do not rely on a guarantee the parser does not make.

6. **v10 `restore` is omitted without a recorded decision.** v10 handles
   `UriCommands.Restore` (`legacy/source/Playnite.DesktopApp/ViewModels/DesktopAppViewModel.cs:1167`,
   same line in the fullscreen view model) and the new table drops it, so
   `playnite://playnite/restore` is rejected as unsupported. The doc calls out `creatediag`
   and `installaddon` only. No v10 code generates a `restore` link, so impact is low, but the
   gap should be named in the "Deliberate boundaries" section (or wired to the host).

7. **CI evidence is for an earlier commit.** The ledger fragment
   (`docs/port/units/p6-007.ledger.d/196-uri-html-description.tsv:2`) cites linux-ci run
   `44713744036` on head `ddab240…`, while the reviewed head is `3242778…`, and asserts the
   delta is docs-only. That is exactly the shape the unit-evidence guard accepts, and I did
   not re-verify commit-by-commit because this review was scoped to the supplied cumulative
   diff; the code at `ddab240` is what CI covered.

## Review method and limits

Read-only: this review used only the supplied diff, the forge's PR head identity
(`3242778edf40c856e1c1e710685a320e7d6f0ff4`) and base-repository files needed to check the
named dependencies (`FilterSettings`, `MetadataDocument`, the p5-005a platform parser and
the v10 oracle). No build, no test run, no edit and no other review was consulted, so the
"zero warnings / 102 + 62 passed" claims are checked for internal consistency (test-case
counts and CI citation) rather than re-executed here.
