# Playnite 10.64 UI reference sources — p1-004

**Reference-only, non-blocking.** This is the p1-004 deliverable: a curated index of
official and public Playnite 10.x UI material for a port author to compare against.
p1-004 is satisfied by this index; no local Windows capture is required for Phase 1.

**Retrieved:** 2026-11-14 UTC.

**No copyrighted media is stored here.** This file contains links, slides, section
anchors, image filenames and timestamps only. It does not download, commit, mirror or
embed any frame or screenshot. Use the links in a browser; do not add copies to the
repository.

These references are **human comparison material only**. They are never a pixel gate,
never a test oracle, never a baseline for snapshot acceptance, and never a claim that
the Linux port is visually or behaviourally equivalent to v10.

## Version caveats

Playnite **10.64** was released on 2026-11-09
([official release](https://github.com/JosefNemec/Playnite/releases/tag/10.64)). None of
the sources below labels itself as a frozen 10.64 capture:

- The official website and manual are current-release material, but they are not
  version-pinned. They were retrieved five days after 10.64 shipped and are treated as
  current 10.x references, not as frozen 10.64 evidence.
- The public video predates 10.64 and does not state an exact patch version.
- The public written/gallery sources predate 10.64 or label a different version.

Do not promote any of these links to "10.64 screenshot evidence". The frozen 10.64
source remains `legacy/`; the optional capture procedure in
[`README.md`](README.md) is the only route to a genuine local 10.64 image set.

## Official sources

Primary tier:

1. Official Playnite website — <https://playnite.link/>
2. Official Playnite manual (generated from the official `Playnite/Docs` repository) —
   <https://api.playnite.link/docs/>
3. Official GitHub repository and release tags —
   <https://github.com/JosefNemec/Playnite>

The official website's five-slide carousel is at
<https://playnite.link/#carousel-711607>; slide 1 is `screen1.jpg` through slide 5
`screen5.jpg`. The site does not label the slides, so the descriptions below are from
opening each slide at retrieval time.

The manual pages are section-anchored and also name the image file embedded in the page.
Follow the section anchor, then open the named image from the same `images/` directory if
the direct link is unavailable.

## By screen

### Main library — grid view

| Publisher | URL | Where the screen appears | Notes |
| --- | --- | --- | --- |
| Official Playnite website | <https://playnite.link/#carousel-711607> | Slide 2 of 5, image `screen2.jpg`; direct link <https://playnite.link/screen2.jpg> | Grid of covers with the selected-game panel and filter panel. Version not labelled. |
| Official Playnite manual | <https://api.playnite.link/docs/manual/gettingStarted/playniteDesktopMode.html#grid-view> | Section "Grid View"; image `playniteDesktopMode_GridView.jpg`; direct link <https://api.playnite.link/docs/manual/gettingStarted/images/playniteDesktopMode_GridView.jpg> | Shows the cover grid and the hover/info affordance. Current manual, not version-pinned. |
| Public video — Joey's Retro Handhelds | <https://www.youtube.com/watch?v=gRtvb-cvDjg&t=233s> | **3:53** — "click the grid icon" and the covers are shown in a grid | Published 2025-05-25; Playnite 10.x, exact patch not stated; not 10.64. |
| Public screenshot gallery — LO4D | <https://playnite.en.lo4d.com/screenshots> | Screenshot `playnite-2.png`; direct link <https://cdn.lo4d.com/t/screenshot/800/playnite-2.png> | Page labels Playnite **10.56**; grid plus a game context menu. Not a review. |

### Main library — list view

| Publisher | URL | Where the screen appears | Notes |
| --- | --- | --- | --- |
| Official Playnite website | <https://playnite.link/#carousel-711607> | Slide 5 of 5, image `screen5.jpg`; direct link <https://playnite.link/screen5.jpg> | Dense list with the genre explorer panel and sortable columns. Version not labelled. |
| Official Playnite manual | <https://api.playnite.link/docs/manual/gettingStarted/playniteDesktopMode.html#list-view> | Section "List View"; image `playniteDesktopMode_ListView.jpg`; direct link <https://api.playnite.link/docs/manual/gettingStarted/images/playniteDesktopMode_ListView.jpg> | List/table view with the column chooser open. Current manual, not version-pinned. |
| Public screenshot gallery — LO4D | <https://playnite.en.lo4d.com/screenshots> | Screenshot `playnite-7.png`; direct link <https://cdn.lo4d.com/t/screenshot/800/playnite-7.png> | Page labels Playnite **10.56**; list view with the context menu open. Not a review. |

### Main library — details view

| Publisher | URL | Where the screen appears | Notes |
| --- | --- | --- | --- |
| Official Playnite website | <https://playnite.link/#carousel-711607> | Slide 1 of 5, image `screen1.jpg`; direct link <https://playnite.link/screen1.jpg> | Compact game list on the left, selected-game overview/description on the right. Version not labelled. |
| Official Playnite manual | <https://api.playnite.link/docs/manual/gettingStarted/playniteDesktopMode.html#details-view> | Section "Details View"; image `playniteDesktopMode_DetailsView.jpg`; direct link <https://api.playnite.link/docs/manual/gettingStarted/images/playniteDesktopMode_DetailsView.jpg> | Details view with the panel arrangement. Current manual, not version-pinned. |
| Public written review — Techgage | <https://techgage.com/article/playnite-tour/> | Article image `Playnite-Library-Screen.jpg`; direct link <https://techgage.com/wp-content/uploads/2022/08/Playnite-Library-Screen.jpg> | Published 2022-08-25; version not stated; pre-dates 10.64. Use as a layout cross-check only. |
| Public screenshot gallery — LO4D | <https://playnite.en.lo4d.com/screenshots> | Screenshot `playnite.png`; direct link <https://cdn.lo4d.com/t/screenshot/800/playnite.png> | Page labels Playnite **10.56**; details view. Not a review. |

### Settings

| Publisher | URL | Where the screen appears | Notes |
| --- | --- | --- | --- |
| Official Playnite manual | <https://api.playnite.link/docs/manual/gettingStarted/configuringPlaynite.html#general-configuration> | Section "General configuration"; image `gettingStarted_GeneralConfig.png`; direct link <https://api.playnite.link/docs/manual/gettingStarted/images/gettingStarted_GeneralConfig.png> | Desktop Settings with **General** selected. Current manual, not version-pinned. |
| Official Playnite manual | <https://api.playnite.link/docs/manual/gettingStarted/configuringPlaynite.html#user-interface-and-appearance-configuration> | Section "User Interface and appearance configuration"; image `gettingStarted_SettingsAppearance.png`; direct link <https://api.playnite.link/docs/manual/gettingStarted/images/gettingStarted_SettingsAppearance.png> | Settings with **Appearance** selected. Current manual, not version-pinned. |
| Public video — Joey's Retro Handhelds | <https://www.youtube.com/watch?v=gRtvb-cvDjg&t=500s> | **8:20** — Settings → Add-ons, then the theme list | Published 2025-05-25; Playnite 10.x, exact patch not stated; not 10.64. |
| Public video — Joey's Retro Handhelds | <https://www.youtube.com/watch?v=gRtvb-cvDjg&t=577s> | **9:37** — Settings → Appearance → Grid View | Same video and version caveat. |
| Public screenshot gallery — LO4D | <https://playnite.en.lo4d.com/screenshots> | Screenshots `playnite-4.png` (General), `playnite-5.png` (Metadata), `playnite-6.png` (Performance); direct links <https://cdn.lo4d.com/t/screenshot/800/playnite-4.png>, <https://cdn.lo4d.com/t/screenshot/800/playnite-5.png>, <https://cdn.lo4d.com/t/screenshot/800/playnite-6.png> | Page labels Playnite **10.56**; several settings pages. Not a review. |

### First-run / import

| Publisher | URL | Where the screen appears | Notes |
| --- | --- | --- | --- |
| Official Playnite manual | <https://api.playnite.link/docs/manual/gettingStarted/firstStart.html> | Section "First Time Configuration Wizard"; images `firstStart_Wizard1.png`, `firstStart_Wizard2.png` (library-integration selection), `firstStart_Wizard3.png` (finish); direct links <https://api.playnite.link/docs/manual/gettingStarted/images/firstStart_Wizard1.png>, <https://api.playnite.link/docs/manual/gettingStarted/images/firstStart_Wizard2.png>, <https://api.playnite.link/docs/manual/gettingStarted/images/firstStart_Wizard3.png> | Current manual, not version-pinned. Wizard layout and integration list can change between 10.x releases. |
| Public video — Joey's Retro Handhelds | <https://www.youtube.com/watch?v=gRtvb-cvDjg&t=173s> | **2:53** — first-run setup asks which libraries to integrate, then authentication | Published 2025-05-25; Playnite 10.x, exact patch not stated; not 10.64. |
| Public written review — Techgage | <https://techgage.com/article/playnite-tour/> | Article image `Playnite-First-time-Configuration-02-Client-Choices.jpg`; direct link <https://techgage.com/wp-content/uploads/2022/08/Playnite-First-time-Configuration-02-Client-Choices.jpg> | Published 2022-08-25; version not stated; pre-dates 10.64. Client list is dated. |

### Fullscreen mode

| Publisher | URL | Where the screen appears | Notes |
| --- | --- | --- | --- |
| Official Playnite website | <https://playnite.link/#carousel-711607> | Slide 3 of 5, image `screen3.jpg` (fullscreen library grid); slide 4 of 5, image `screen4.jpg` (fullscreen game view); direct links <https://playnite.link/screen3.jpg>, <https://playnite.link/screen4.jpg> | Default-theme fullscreen library and game page. Version not labelled. |
| Official Playnite manual | <https://api.playnite.link/docs/manual/gettingStarted/playniteFullscreenMode.html> | Section "User Interface" (`#user-interface`), image `playniteFullscreenMode_UI.jpg`; the following game-view image `playniteFullscreenMode_GameDetails.jpg` is in the same section; direct links <https://api.playnite.link/docs/manual/gettingStarted/images/playniteFullscreenMode_UI.jpg>, <https://api.playnite.link/docs/manual/gettingStarted/images/playniteFullscreenMode_GameDetails.jpg> | Current manual, not version-pinned. |
| Public video — Joey's Retro Handhelds | <https://www.youtube.com/watch?v=gRtvb-cvDjg&t=283s> | **4:43** — switches from Desktop to the default Fullscreen mode and shows the library; **13:13** at <https://www.youtube.com/watch?v=gRtvb-cvDjg&t=793s> returns to the fullscreen look | Published 2025-05-25; Playnite 10.x, exact patch not stated; not 10.64. |
| Public written review — Techgage | <https://techgage.com/article/playnite-tour/> | Article image `Playnite-Fullscreen-Mode-scaled.jpg`; direct link <https://techgage.com/wp-content/uploads/2022/08/Playnite-Fullscreen-Mode-scaled.jpg> | Published 2022-08-25; version not stated; pre-dates 10.64. |

## Coverage summary

| Screen | Recorded sources | Strongest reference |
| --- | ---: | --- |
| Main library — grid | 4 | Official manual `#grid-view` |
| Main library — list | 3 | Official manual `#list-view` |
| Main library — details | 4 | Official manual `#details-view` |
| Settings | 5 entries / 3 publishers | Official manual `#general-configuration` and `#user-interface-and-appearance-configuration` |
| First-run / import | 3 | Official manual `firstStart.html` |
| Fullscreen | 4 entries / 4 publishers | Official manual fullscreen page and official website slides 3–4 |

All six tracked screens have at least one official reference. None is a frozen 10.64
capture.

## Known gaps

- **No official YouTube channel or trailer was identified.** The official website links
  Reddit, Discord, Patreon, Ko-fi and GitHub, not YouTube; the `@Playnite` YouTube handle
  belongs to an unrelated channel. The public video above is the best available narrated
  walkthrough, not an official trailer.
- **No source is explicitly version-labelled 10.64.** The official site/manual are
  current but unpinned; the video is 10.x without an exact patch; Techgage states no
  version; LO4D labels 10.56.
- **List view has no reliable video timestamp.** The public video names the list view
  during its view-switcher tour but does not dwell on a separate list capture, so the
  official website slide and official manual image are the references.
- **First-run/import is layout-sensitive.** The wizard changed across 10.x; use the
  official manual for the flow, not pixel positions.
- **No local 10.64 captures exist.** That is intentional and non-blocking. If genuine
  images are ever required, follow the optional procedure in [`README.md`](README.md).

## Link check

Every URL above returned HTTP 200 on 2026-11-14 UTC (GET, following redirects, with a
browser user agent). YouTube timestamps were checked against the video's public chapter
markers and auto-generated transcript; the video description/transcript confirms the
spoken content at each cited timestamp. Re-check links before relying on them in a later
milestone; external pages can move.
