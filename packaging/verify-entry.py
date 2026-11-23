#!/usr/bin/env python3
"""Resolve the desktop entry, launcher and actual apphosts within an extracted artifact."""
import argparse
import configparser
from pathlib import Path
import struct
import sys

ELF_MACHINES = {
    "x86_64": 62,
    "linux-x64": 62,
    "aarch64": 183,
    "linux-arm64": 183,
}


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def verify_elf_arch(path: Path, expected_arch: str) -> None:
    expected_machine = ELF_MACHINES[expected_arch]
    with path.open("rb") as handle:
        header = handle.read(20)
    require(len(header) >= 20, f"Truncated binary header: {path}")
    require(header[:4] == b"\x7fELF", f"Not an ELF binary: {path}")
    require(header[4] == 2 and header[5] == 1, f"Not 64-bit little-endian ELF: {path}")
    (machine,) = struct.unpack("<H", header[18:20])
    require(
        machine == expected_machine,
        f"Unexpected ELF machine {machine} for {path} (expected {expected_machine} for {expected_arch})",
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", help="Extracted artifact root directory")
    parser.add_argument("prefix", nargs="?", default="usr", help="Prefix inside root (default: usr)")
    parser.add_argument(
        "--arch",
        choices=sorted(ELF_MACHINES),
        help="Verify ELF machine architecture of packaged apphost binaries",
    )
    return parser.parse_args(argv)


args = parse_args(sys.argv[1:])
root = Path(args.root).resolve(strict=True)
prefix = args.prefix
base = root / prefix
app_id = "io.github.playnite_linux.Playnite"
entry = configparser.ConfigParser(interpolation=None)
entry.read(base / f"share/applications/{app_id}.desktop")
desktop = entry["Desktop Entry"]
require(desktop["Exec"] == "playnite", f"Unexpected Exec: {desktop['Exec']}")
require(desktop["Icon"] == app_id, f"Unexpected Icon: {desktop['Icon']}")
apphosts = [
    base / "lib/playnite/cli/Playnite.Cli",
    base / "lib/playnite/desktop/Playnite.Desktop",
    base / "lib/playnite/steam-import/Playnite.Steam.Import",
    base / "lib/playnite/launch/Playnite.Launch",
]
paths = [
    base / "bin/playnite",
    *apphosts,
    base / f"share/icons/hicolor/scalable/apps/{app_id}.svg",
]
for path in paths:
    resolved = path.resolve(strict=True)
    require(resolved.is_relative_to(root), f"Escapes artifact: {path}")
    require(resolved.is_file() and resolved.stat().st_size > 0, f"Not a nonempty file: {path}")
    if path.suffix != ".svg":
        require(resolved.stat().st_mode & 0o111, f"Not executable: {path}")
    print(f"Verified: {resolved.relative_to(root)}")
if args.arch:
    optional_apphosts = [base / "lib/playnite/desktop/legacy-import/Playnite.LegacyImport"]
    if prefix == "usr":
        optional_apphosts.append(base / "lib/playnite/steam-acceptance/SteamAcceptance")
    for path in [*apphosts, *(p for p in optional_apphosts if p.exists())]:
        verify_elf_arch(path.resolve(strict=True), args.arch)
        print(f"Verified ELF ({args.arch}): {path.resolve(strict=True).relative_to(root)}")
if prefix == "usr":
    for name in ("AppRun", f"{app_id}.desktop", f"{app_id}.svg", ".DirIcon"):
        require((root / name).resolve(strict=True).is_relative_to(root), f"Escapes artifact: {name}")
print("Desktop binary and icon resolve inside artifact")
