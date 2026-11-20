# p6-007 — URI handler and HTML description service

`Playnite.Core.UriHandling` parses, validates and dispatches the application's `playnite://`
routes. `Playnite.Core.Descriptions` turns an untrusted game description into the documented safe
HTML subset. Both are headless: no Avalonia type, no network call, no child process and no shell.
Every boundary is an injectable seam with a fake in the tests.

## Why Abstractions

The platform layer already validates the raw URI before it crosses a process boundary
(`Playnite.Platform.Linux.Integration.PlayniteUri`, p5-005a). Core cannot reference that project,
and the route table is an SDK harness concern, so the parser is an independent, stricter
implementation that narrows the result to a known command.

The plugin-facing half of the unit is the contract, not the implementation, so it lives in
`Playnite.SDK.Abstractions` where a plugin can consume it with a reference to that assembly
alone. The concrete services (`UriHandlerApi`, `HtmlDescriptionService`, `HtmlDescriptionSanitizer`)
stay in Core because they are host implementations. The v10 names (`IUriHandlerAPI`,
`PlayniteUriEventArgs`) and their v10 namespaces are kept exactly.

## SDK Abstractions move

The move is a namespace move, not a redefinition: every moved type keeps its member set, defaults
and behaviour. `Playnite.Core` keeps the concrete services and now references the contracts from
`Playnite.SDK.Abstractions` only; Abstractions references no other `Playnite.*` assembly and no
Avalonia package.

| Old namespace | New namespace | Types |
| --- | --- | --- |
| `Playnite.Core.UriHandling` | `Playnite.SDK` | `IUriHandlerAPI`, `IUriCommandHost`, `UriCommandStatus`, `UriCommandResult`, `UriDispatchStatus`, `UriDispatchResult`, `PlayniteUriTarget`, `PlayniteViewTarget`, `PlayniteFilterField`, `PlayniteFilterTarget`, `PlayniteUriRoute` |
| `Playnite.Core.UriHandling` | `Playnite.SDK.Events` | `PlayniteUriEventArgs` (the v10 namespace for the type) |
| `Playnite.Core.Descriptions` | `Playnite.SDK.Descriptions` | `IHtmlDescriptionProvider`, `IHtmlSanitizer`, `HtmlDescriptionStatus`, `HtmlDescriptionSourceResult`, `HtmlDescriptionResult`, `HtmlSanitizationResult` |

`PlayniteUriRoute` moves with the dispatch result because the result carries a route and
Abstractions may not reference Core. The v7 text surface is declared in
`PublicAPI.Unshipped.txt`; `PublicAPI.Shipped.txt` and its checksum are unchanged.

### ApiCompat v6→v7 delta

The move restores the v10 names and namespaces, so it removes ApiCompat differences instead of
introducing any. Running Microsoft.DotNet.ApiCompat 11.0.100 against the published
`PlayniteSDK` 6.18.0 baseline (`lib/net462/Playnite.SDK.dll`, SHA-256
`ce7511941aba4d4b17dd7b226fadfd0ecefdf51ab73775b25631089fd7542fc7`) and this branch's
Abstractions sources compiled as `Playnite.SDK` reports 216 `CP0001` differences, down from the
218 recorded before the move; no member-level break (`CP0002`/`CP0006`/`CP0009`) is reported and
no suppression is needed or added. The two entries the move removes are:

```text
CP0001: Type 'Playnite.SDK.IUriHandlerAPI' exists on v6-baseline but not on v7-candidate
CP0001: Type 'Playnite.SDK.Events.PlayniteUriEventArgs' exists on v6-baseline but not on v7-candidate
```

The complete v6→v7 report remains the p6-008 freeze artifact; this unit records only the delta
its moved members produce. See `p6-007-apicompat-v6-v7-delta.txt` for the command and counts.

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

- Product contracts: `src/Playnite.SDK.Abstractions/UriHandling/`, `Events/` and `Descriptions/`;
  concrete services: `src/Playnite.Core/UriHandling/UriHandlerApi.cs`,
  `src/Playnite.Core/Descriptions/`.
- Unit tests: `tests/Playnite.Core.Tests/UriHandling/` (route parse/validation and
  dispatch matrix, including malformed, traversal, injection, entity and cancellation cases),
  `tests/Playnite.Core.Tests/Descriptions/` (script, handlers, `javascript:`/`data:`/`file:`
  links, iframe, style, active content, entity bypasses, malformed markup, depth and truncation)
  and `tests/Playnite.Core.Tests/SdkContracts/UriHtmlContractTests.cs` (each moved interface
  exercised through the real Core service and a recording fake host). Zero skips.
- `dotnet test tests/Playnite.Core.Tests -c Release`: 3044 passed, zero failures, zero skips.
- `dotnet test tests/Playnite.Architecture.Tests -c Release`: 199 passed, zero skips (PublicAPI,
  deterministic build and ApiCompat baseline checks included).
- Warning-free build: `dotnet build src/Playnite.SDK.Abstractions/Playnite.SDK.Abstractions.csproj
  -c Release`, `dotnet build src/Playnite.Core/Playnite.Core.csproj -c Release` and
  `dotnet build tests/Playnite.Core.Tests/Playnite.Core.Tests.csproj -c Release` report zero
  warnings and zero errors.
- ApiCompat: 216 `CP0001` v6→v7 differences after the move (218 before), no member-level break,
  no suppressions; the delta is in `p6-007-apicompat-v6-v7-delta.txt`.
- No network, no Avalonia, no banned API, no aggregate edit.

## Deliberate boundaries

- No `Avalonia.HtmlRenderer` reference: the UI rendering path belongs to the Phase 7 shell. This
  unit produces the safe HTML string; it does not add a UI dependency to Core.
- No live metadata fetch: p6-005 owns metadata resolution; this unit consumes a description
  through the provider seam.
- No plugin-source store or persistence: registration is in-memory, as v10's dictionary was.
- `sdk-*` parity rows and the complete v6→v7 freeze report stay with p6-008.
