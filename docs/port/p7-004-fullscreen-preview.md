# Fullscreen library preview

The desktop library exposes **Fullscreen** alongside its usual library controls.
The v10 references are
`legacy/source/Playnite.FullscreenApp/Windows/MainWindow.xaml` and
`legacy/source/Playnite.FullscreenApp/Themes/Fullscreen/Default/Views/Main.xaml`.

Use the arrow keys to move between games, **Previous** and **Next** to change
pages, and **Enter** or **Play** to launch the selected game. Use **Escape**
or **Back to desktop** to return to the desktop library and its previous
selection.

## Living-room controls

Open **Fullscreen** from the desktop library. Use the D-pad to move through
games and controls, A to select, B to go back, and the shoulder buttons to
change pages. In **Game details**, use Up and Down to scroll. **Search** opens
a controller keyboard and also accepts text from a physical keyboard.

Controller input requires `libSDL3.so.0` in the application's runtime.
Keyboard and mouse work when SDL3 is unavailable. The controller can reconnect
without leaving fullscreen or resetting the current selection.

For a tracked executable launch, fullscreen displays the session status and
restores the launched game when the session ends and the window is active.
For Steam and Heroic URI launches, fullscreen reports the handoff and
restores the launched game when the window regains focus.
