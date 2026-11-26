# Scan and play SNES ROMs

In native Linux Playnite, choose **Import SNES ROMs** and select Snes9x or RetroArch with Snes9x. Choose a local folder. For RetroArch, also select the installed `snes9x_libretro.so` file.

The scan includes `.sfc` and `.smc` files in subfolders and ignores symbolic links inside the tree. Each absolute path gets one library entry under the **SNES** source. Rescanning applies the chosen profile to the ROMs found and preserves their library entries and metadata. A ROM moved to a new path is imported as a new entry. Remove the old entry separately.

Play starts the emulator using the same tracked session as a native game. Playnite looks for `snes9x-gtk`, `snes9x`, or `retroarch` in its standard native tool directories. RetroArch receives the selected core and ROM as separate arguments. The scan does not need an installed emulator and does not open ROM contents. Archive import and launching emulators outside a Flatpak sandbox are outside this slice.

The frozen Playnite scanner in `legacy/source/Playnite/Emulators/Scanner.cs` and the `Snes9X` and `RetroArch` emulator definitions are the references for the two profiles and their common file types. Linux command lines follow the [Snes9x usage guide](https://github.com/snes9xgit/snes9x/blob/master/unix/docs/readme_unix.html) and [RetroArch CLI documentation](https://docs.libretro.com/guides/cli-intro/).
