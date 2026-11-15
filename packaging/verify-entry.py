#!/usr/bin/env python3
"""Resolve the desktop entry, launcher and actual apphosts within an extracted artifact."""
import configparser
from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve(strict=True)
prefix = sys.argv[2] if len(sys.argv) > 2 else "usr"
base = root / prefix
app_id = "io.github.playnite_linux.Playnite"
entry = configparser.ConfigParser(interpolation=None)
entry.read(base / f"share/applications/{app_id}.desktop")
desktop = entry["Desktop Entry"]
assert desktop["Exec"] == "playnite", desktop["Exec"]
assert desktop["Icon"] == app_id, desktop["Icon"]
paths = [base / "bin/playnite", base / "lib/playnite/cli/Playnite.Cli",
         base / "lib/playnite/desktop/Playnite.Desktop",
         base / f"share/icons/hicolor/scalable/apps/{app_id}.svg"]
for path in paths:
    resolved = path.resolve(strict=True)
    assert resolved.is_relative_to(root), f"Escapes artifact: {path}"
    assert resolved.is_file() and resolved.stat().st_size > 0, path
    if path.suffix != ".svg":
        assert resolved.stat().st_mode & 0o111, path
    print(f"Verified: {resolved.relative_to(root)}")
if prefix == "usr":
    for name in ("AppRun", f"{app_id}.desktop", f"{app_id}.svg", ".DirIcon"):
        assert (root / name).resolve(strict=True).is_relative_to(root), name
print("Desktop binary and icon resolve inside artifact")
