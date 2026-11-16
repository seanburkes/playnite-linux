# p3-006 — stored-field search, filter and sort

## Boundary

The API is in `Playnite.Core.Queries`, using the real `Playnite.Core.Models.Game`.
Firstmate steering p3-006/005–006 authorizes this stored-field boundary: no database,
navigation abstraction, Avalonia or invented expanded model properties. Matching-only
settings omit UI notifications, serialization and SDK conversion. The protected parity
row `core-013` is not edited; this document and the unit shard record partial delivery.

**Deferred:** expanded metadata **text** filters (source, completion status, genre,
platform, developer, publisher, category, tag, series, region, age rating, feature),
including comma splitting, text-over-ID precedence and AND/OR text matching
(`GameDatabase_Filters.cs:279–344`, `Settings/FilterSettings.cs:186–226`). Stored GUID
membership on these fields does work; it never resolves metadata or substitutes IDs
for names. Library **sorting** and metadata-name/list game sort keys (Platforms,
Categories, Genres, Developers, Publishers, Tags, Series, AgeRatings, Regions, Source,
CompletionStatus, Features) are deferred until metadata lookup exists. They are not
exposed by `SortOrder`; remaining enum values retain their v10 numbers. Grouping and
search UI/provider discovery are not part of this pure-logic unit.

## Source-derived behavior

All references below are under `legacy/source/`.

| Port | Frozen reference | Preserved semantics |
| --- | --- | --- |
| `TextSearch.MatchTextFilter` | `Playnite/ViewModels/SearchViewModel.cs:668–715` | Blank query accepts even missing target; nonblank query rejects missing target. Jaro-Winkler ≥ .90 runs before the length guard. Optional acronym prefix; split only on literal space; every query word must occur within some target word, ignoring invariant case, symbols and nonspacing marks. No `NormalizeGameName` preprocessing. |
| Existing text helpers reused | `Playnite/Common/Extensions/StringExtensions.cs:105–143` and Jaro/Levenshtein helpers | Acronym starts at alphanumeric characters following literal spaces, not tabs/hyphens. Repeated query words may reuse the same target word. Matching is directional, unlike Levenshtein distance and metadata equality. |
| `FilterMatcher` scalar predicates | `Playnite/Database/GameDatabase_Filters.cs:61–269,371–376` | All active fields conjoin; equal installed/uninstalled toggles disable that restriction; Favorite is positive-only; Hidden is equality, not inclusion. Release year exact membership and `{}` for missing date; version ordinal-ignore-case substring; categorical playtime/install-size/score membership. |
| Name filter | same file `:176–199` | `^` takes the next character as a case-sensitive name-group selector, ignoring trailing characters; SortingName affects the group. Non-fuzzy queries always drop their first character, even without `!`. `!` forces substring matching in fuzzy mode. |
| Stored ID predicates | same file `:168–174,296–307,346–369` | Library is OR membership in PluginId. Source/completion are OR membership; MaxGuid aliases Empty on a failed match. GUID lists use configured ALL/ANY; Empty matches a null or empty list, not an explicit Empty item. Inactive/null selections pass. |
| `DateSegmentCalculator` | `PlayniteSDK/Models/Game.cs:1531–1565` | Calendar Today/Yesterday before elapsed `<7`, `<31`, `<365` day buckets. Future dates outside Today fall into PastWeek. Both local clock and calculator are caller-injected; active date filters without them throw explicitly. No default ambient clock. |
| `GameComparer` | `Playnite.DesktopApp/DesktopCollectionView.cs:230–234`; `Playnite.FullscreenApp/FullscreenCollectionView.cs:59–62`; `Playnite/GamesCollectionViewEntry.cs:43–103,190–195` | Stored Name, LastActivity, ReleaseDate, Version, PlayCount, Playtime, UserScore, CriticScore, CommunityScore, Added, Modified, IsInstalled, Hidden, Favorite, InstallDirectory, InstallSize, RecentActivity, RomList. Primary direction only; non-name ties always use ascending effective Name (SortingName override). Equal keys preserve input order through LINQ's stable OrderBy. No invented ID tie-break. Nullable scalar values first ascending/last descending. Version and joined ROM paths are strings, not version/path parsers. Current-culture string order. |
| `ComparableDbItemList<T>` | `PlayniteSDK/Collections/ComparableList.cs:15–53` | Independent named-entity list comparer: populated before empty, then current-culture case-sensitive joined strings in existing item order. Null collection becomes empty; CompareTo(null) returns -1; wrong types throw. This does not claim game metadata navigation. |
| `GameFieldComparer` | `Playnite/Database/GameFieldComparer.cs:11–57` | Null equals empty; otherwise remove whitespace and ASCII hyphens, then invariant-ignore-case equality. A nonempty string reduced to empty still differs from original empty. v10's current-culture hash and null hash exception are retained, including its Turkish equality/hash mismatch. Not used for sort order. |
| `SearchResults.Rank` | `Playnite/ViewModels/SearchViewModel.cs:205–210` | On caller-selected eligible games: Levenshtein ignoring case, display Name, installed first, then stable ties; cap at 60. Unlike library sorting this uses display Name, not SortingName. |

## Evidence

`QueryPropertyTests` uses fixed seeds and generated nullable scalars, strings, ID
selections and lists. For every exposed sort key in both directions it asserts
reflexivity, sign antisymmetry, transitivity, adjacent order, stability, sort
idempotence and filter/sort commutation. Separate primary-key bindings prevent a
constant/incorrect-field comparer from satisfying those laws. Named-list total order,
filter idempotence, generated ALL/ANY membership, equality symmetry, case invariance
and Levenshtein symmetry are checked separately. Search directionality is explicitly
pinned rather than falsely asserting symmetric query matching.

`QueryDateAndFilterTests` covers every stored ID-list binding, scalar conjunction,
missing selections, fixed-clock generated segment membership/idempotence, injected
calculator forwarding, missing-context errors, exact bucket edges and future dates.
Quirk tables cite the frozen sources above; no protected fixture, golden, baseline,
parity table or aggregate was changed. Pure synchronous computation owns no resources;
cancellation/disposal are not applicable.

Local verification: Core **1365 passed**, Architecture **144 passed**, zero failed or
skipped; Core Release build **0 warnings, 0 errors**. Actual linux-ci run
**43775725997** on `9fb148ee502905a8c4aedc9526ad88b87faf4651` confirms these counts
in downloaded JUnit artifacts (Smoke also 4/4, all MTP exit 0). Firstmate steering
007–009 authorized raising the Core floor from **1254 to 1365** with an additive
capture under `oracle-change`, after the preceding union floor merged. A fresh Core
run on the normal integration merge also passed 1365/1365. All historical captures
and other floors remain intact. JUnit contains 1254 pre-query cases plus 111 query
cases; this is not a guessed future merged-head union. Independent adversarial
reviews are firstmate-owned and recorded in the PR; this document does not claim a
Windows/.NET Framework oracle execution.
