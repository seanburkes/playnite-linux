# Fullscreen library preview

Fullscreen is a fast-follow under [ADR-008](00-DECISIONS.md#adr-008--v1-scope-desktop-steam-only)
and `ui-019`. The desktop library exposes an opt-in preview; the normal startup
has no fullscreen entry. The v10 references are
`legacy/source/Playnite.FullscreenApp/Windows/MainWindow.xaml` and
`legacy/source/Playnite.FullscreenApp/Themes/Fullscreen/Default/Views/Main.xaml`.

```sh
dotnet run --project src/Playnite.Desktop -- --fullscreen-preview
```

After setup, choose **Fullscreen** in the library. Use the arrow keys to move
between games, **Previous** and **Next** to change pages, and **Enter** or
**Play** to request a Steam launch for an installed Steam game. **Escape** or
**Back to desktop** closes the preview without changing desktop selection or
stopping Steam. The launch response does not confirm the game started.

The preview does not include a hero/details view, controller input, or
fullscreen theme parity. Headless smoke covers entry, keyboard navigation,
paging, dispatch through a fake opener, and return to the original selection.
