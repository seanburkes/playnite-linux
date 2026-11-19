#!/usr/bin/env python3
"""Resolve the desktop entry, launcher and actual apphosts within an extracted artifact."""
import configparser
from pathlib import Path
import sys


def require(condition, message):
    if not condition:
        raise SystemExit(message)


root = Path(sys.argv[1]).resolve(strict=True)
prefix = sys.argv[2] if len(sys.argv) > 2 else "usr"
base = root / prefix
app_id = "io.github.playnite_linux.Playnite"
entry = configparser.ConfigParser(interpolation=None)
entry.read(base / f"share/applications/{app_id}.desktop")
desktop = entry["Desktop Entry"]
require(desktop["Exec"] == "playnite", f"Unexpected Exec: {desktop['Exec']}")
require(desktop["Icon"] == app_id, f"Unexpected Icon: {desktop['Icon']}")
paths = [base / "bin/playnite", base / "lib/playnite/cli/Playnite.Cli",
         base / "lib/playnite/desktop/Playnite.Desktop",
         base / "lib/playnite/steam-import/Playnite.Steam.Import",
         base / f"share/icons/hicolor/scalable/apps/{app_id}.svg"]
for path in paths:
    resolved = path.resolve(strict=True)
    require(resolved.is_relative_to(root), f"Escapes artifact: {path}")
    require(resolved.is_file() and resolved.stat().st_size > 0, f"Not a nonempty file: {path}")
    if path.suffix != ".svg":
        require(resolved.stat().st_mode & 0o111, f"Not executable: {path}")
    print(f"Verified: {resolved.relative_to(root)}")
if prefix == "usr":
    for name in ("AppRun", f"{app_id}.desktop", f"{app_id}.svg", ".DirIcon"):
        require((root / name).resolve(strict=True).is_relative_to(root), f"Escapes artifact: {name}")
print("Desktop binary and icon resolve inside artifact")
