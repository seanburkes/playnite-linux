# p6-007 — URI handler and HTML description service

`Playnite.Core.UriHandling` parses, validates and dispatches the application's `playnite://`
routes. `Playnite.Core.Descriptions` turns an untrusted game description into the documented safe
HTML subset. Both are headless: no Avalonia type, no network call, no child process and no shell.
Every boundary is an injectable seam with a fake in the tests.

## Why Core

The platform layer already validates the raw URI before it crosses a process boundary
(`Playnite.Platform.Linux.Integration.PlayniteUri`, p5-005a). Core cannot reference that project,
and the route table is an SDK harness concern, so this unit keeps an independent, stricter parser
in Core and narrows the result to a known command. The v10 names (`IUriHandlerAPI`,
`PlayniteUriEventArgs`) are kept where they map cleanly. The public Abstractions files are
protected, so p6-008 owns moving this surface into Abstractions and freezing it. No `sdk-*` parity
row is rewritten here.

> **Superseded by p6-008:** the move happened. `IUriHandlerAPI`, its host seam, the route types
> and DTOs now live in `Playnite.SDK`; `PlayniteUriEventArgs` lives in `Playnite.SDK.Events`;
> the HTML description contracts live in `Playnite.SDK.Descriptions`. The services remain in
> `Playnite.Core.UriHandling` and `Playnite.Core.Descriptions`. See `p6-008-sdk-freeze.md`.

## URI routes

The URI shape is `playnite://` followed by `<source>/<argument>...`, as in v10. The `playnite`
source is reserved for application commands; every other valid source belongs to a registered
plugin handler. Sources are matched case-insensitively, as in v10. Commands are lower-case and
matched ordinally.

Routes below omit the scheme prefix:

| Route | Target | Validation |
| --- | --- | --- |
| `playnite/start/<gameId>[/<actionIndex>]` | `StartGame` | `gameId` is a GUID; `actionIndex` is 0–999 when present. |
| `playnite/showgame/<gameId>` | `ShowGame` | Exactly one GUID argument. |
| `playnite/search[/<term>]` | `Search` | At most one term, 1–256 characters, no control characters. |
| `playnite/view/desktop` | `ShowView` | Target is `desktop` or `fullscreen`. |
| `playnite/filter/<field>/<value>` | `Filter` | Field and value rules below. |
| `<source>/<argument>...` | `CustomSource` | Source is 1–64 of `A-Z a-z 0-9 . _ ~ -`, not `playnite`, not `.`/`..`. |

The v10 `creatediag` and `installaddon` commands are rejected with an explicit diagnostic.
Diagnostics packaging is outside this unit and add-on installation is p6-010 (store v1.1), so a
silently ignored command would be worse than a rejection.

Filter fields map to the stored fields of the p6-001 `FilterSettings` snapshot:

| Kind | Fields | Accepted value |
| --- | --- | --- |
| Flag | `favorite`, `installed`, `uninstalled`, `hidden` | `true` or `false`. |
| Id | `genre`, `platform`, `publisher`, `developer`, `category`, `tag`, `series`, `region`, `source`, `age-rating`, `library`, `completion-status`, `feature` | A GUID, normalized to the canonical `D` form. |
| Year | `release-year` | Four ASCII digits; a leading zero is rejected. |
| Text | `name` | 1–256 characters, no control characters. |

### Input validation

The whole URI is rejected when it is empty, longer than 2048 characters, contains a control
character or unescaped whitespace, lacks the `playnite://` scheme, or contains a query, fragment
or backslash. The source is a single bounded segment; there are at most 32 arguments of at most
1024 characters. Percent escapes must be well formed and decode as strict UTF-8 (a bad escape, a
lone `%`, an invalid byte sequence, an unpaired surrogate or a decoded control character rejects
the URI). `+` means space, matching v10 `HttpUtility.UrlDecode`. Traversal segments and unknown or
unsupported commands are rejected. Parsing never executes anything, so a malicious argument cannot
reach a shell.

### Dispatch contract

`UriHandlerApi.DispatchAsync(uri, cancellationToken)` returns a `UriDispatchResult`; it does not
throw for bad input or for a failing host.

| Situation | Status | Side effect |
| --- | --- | --- |
| Valid app route, host completes | `Completed` | The injected `IUriCommandHost` method is called once. |
| Valid plugin source, handler returns | `Completed` | The registered callback receives the decoded arguments. |
| Malformed or unsupported URI | `Rejected` | Nothing is parsed further; no host or handler runs. |
| Plugin source not registered | `UnhandledSource` | No callback runs; the parsed route is returned. |
| App route with no host | `Unavailable` | `UriHandlerApi.MissingHostDiagnostic`; plugin sources still work. |
| Host returns `Unavailable`/`Failed` | mapped | The host diagnostic is kept. |
| Host or handler throws | `Failed` | The exception type is reported; it does not escape. |
| Caller cancellation | throws `OperationCanceledException` | Cancellation is never converted into a failure. |

`RegisterSource` rejects a null handler, an invalid source name, the reserved `playnite` source and
a duplicate (case-insensitive), as v10 did. `RemoveSource` is a no-op for an absent source.

## HTML description service

`HtmlDescriptionService.RenderAsync(gameId, cancellationToken)` asks an injected
`IHtmlDescriptionProvider` for the raw description and passes it to an injected `IHtmlSanitizer`
(default `HtmlDescriptionSanitizer`). The host adapts the p6-005 metadata document's `Description`
field (or a plugin provider); the tests use a fake. The service opens no socket and no file.

| Provider answer | Status | Result |
| --- | --- | --- |
| Found, non-blank, safe output | `Rendered` | Sanitised HTML and the sanitisation counters. |
| Found but null/blank | `Empty` | `no-description`; the sanitiser is not called. |
| Missing | `Empty` | `no-description`. |
| Sanitiser leaves nothing | `Empty` | `sanitized-empty`, with the sanitisation result. |
| Failure with a reason | `Failed` | The provider's stable reason. |
| Throws | `Failed` | `provider-exception: <ExceptionType>`. |
| Null result | `Failed` | `provider-invalid-response`. |
| Caller cancellation | throws `OperationCanceledException` | Never converted into a failure. |

### Safe subset

Allowed elements: `a b blockquote br code del div em h1 h2 h3 h4 h5 h6 hr i ins li ol p pre s
span strong sub sup u ul`. Everything else is dropped. The content of active or foreign elements
(`script`, `style`, `iframe`, `object`, `embed`, `svg`, `math`, `template`, `textarea`, `title`,
`noscript`, `noembed`, `noframes`, `plaintext`, `xmp`) is dropped too, so source text cannot leak
into the description.

The only surviving attribute is one `href` on `a`, and only when it is an absolute `http` or
`https` URL with a host and no user info. Every anchor then gets
`rel="noreferrer noopener"`. Event handlers, `style`, `src`, `target`, `class`, duplicate
attributes and all other attributes are removed. Entities are decoded before text is re-escaped
and before a link is validated, so `jav&#x61;script:` and friends are rejected rather than
smuggled through. Malformed markup is escaped or dropped, depth is bounded at 64, input at
256 KiB, and the sanitiser is total: it never throws for hostile input.

## Evidence

- Product: `src/Playnite.Core/UriHandling/` and `src/Playnite.Core/Descriptions/`.
- Unit tests: `tests/Playnite.Core.Tests/UriHandling/` (route parse/validation and
  dispatch matrix, including malformed, traversal, injection, entity and cancellation cases) and
  `tests/Playnite.Core.Tests/Descriptions/` (script, handlers, `javascript:`/`data:`/`file:`
  links, iframe, style, active content, entity bypasses, malformed markup, depth and truncation,
  plus provider/sanitiser contract tests through fakes). Zero skips.
- `dotnet test tests/Playnite.Core.Tests --filter "FullyQualifiedName~UriHandling"`: 102 passed.
- `dotnet test tests/Playnite.Core.Tests --filter "FullyQualifiedName~Descriptions"`: 62 passed.
- Warning-free build: `dotnet build src/Playnite.Core/Playnite.Core.csproj -c Release` and
  `dotnet build tests/Playnite.Core.Tests/Playnite.Core.Tests.csproj -c Release` report zero
  warnings and zero errors.
- No network, no Avalonia, no banned API, no protected oracle, baseline or aggregate edit.

## Deliberate boundaries

- No `Avalonia.HtmlRenderer` reference: the UI rendering path belongs to the Phase 7 shell. This
  unit produces the safe HTML string; it does not add a UI dependency to Core.
- No live metadata fetch: p6-005 owns metadata resolution; this unit consumes a description
  through the provider seam.
- No plugin-source store or persistence: registration is in-memory, as v10's dictionary was.
- `sdk-*` parity rows and the Abstractions freeze stay with p6-008.
