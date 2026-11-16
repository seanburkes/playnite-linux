#!/usr/bin/env python3
"""Generate the additive p3-008b archive evidence from the frozen v10 layout.

Run from any directory: python3 scripts/port/generate_archive_fixtures.py

The v10 layout is defined by legacy/source/Playnite/Backup.cs: root config files,
library/<root files>, libraryfiles/, extension/, extensiondata/ and
themes/<Desktop|Fullscreen>/<theme>/, with themes/Default never packed. The payload bytes here
are synthetic (the v10 source defines names, not file contents); no byte is presented as a
captured Windows archive. Existing files are compared and never overwritten.
"""
import hashlib
import io
import json
import tarfile
import warnings
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "tests/fixtures/archives-v10"
GOLDEN = ROOT / "tests/golden/archives"
FIXED_TIME = (2020, 1, 2, 3, 4, 6)
MANIFEST = "backup-manifest.json"

FULL = [
    ("config.json", b'{"DatabasePath":"library"}\n'),
    ("fullscreenConfig.json", b'{"Monitor":0}\n'),
    ("library/database.json", b'{"Id":"v10-test-library"}\n'),
    ("library/games.db", b"synthetic-v10-games-db\n"),
    ("libraryfiles/cover.png", b"\x89PNG\r\n\x1a\nsynthetic-cover\n"),
    ("libraryfiles/Backgrounds/background.jpg", b"synthetic-background\n"),
    ("extension/TestAddon/extension.yaml", b"Id: TestAddon\nName: Test Addon\n"),
    ("extension/TestAddon/TestAddon.dll", b"synthetic-addon\n"),
    ("extensiondata/TestAddon/data.json", b'{"enabled":true}\n'),
    ("themes/Desktop/Custom/theme.yaml", b"Id: CustomDesktop\nMode: Desktop\n"),
    ("themes/Desktop/Custom/Views/Main.xaml", b"<Grid />\n"),
    ("themes/Fullscreen/Custom/theme.yaml", b"Id: CustomFullscreen\nMode: Fullscreen\n"),
]

MINIMAL = [
    ("config.json", b'{"DatabasePath":"library"}\n'),
    ("library/games.db", b"synthetic-v10-games-db\n"),
]

EXTENSION_DATA_ONLY = [
    ("extensiondata/TestAddon/data.json", b'{"enabled":true}\n'),
]

TRAVERSAL = [
    ("../outside.txt", b"escape\n"),
    ("library/../../escape.txt", b"escape\n"),
    ("/absolute.txt", b"escape\n"),
    ("C:/windows.txt", b"escape\n"),
    ("a/./dot.txt", b"escape\n"),
    ("a//empty-segment.txt", b"escape\n"),
]

DUPLICATE = [
    ("library/games.db", b"first\n"),
    ("library/games.db", b"second\n"),
]


def manifest_members(entries):
    return [
        {"name": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest().upper()}
        for name, data in entries
    ]


def manifest_bytes(entries, *, version=1, omit=None, bad_hash=False):
    members = [
        member for member in manifest_members(entries)
        if member["name"] != omit
    ]
    if bad_hash:
        members[0]["sha256"] = "0" * 64
    payload = {"formatVersion": version, "members": members}
    return (json.dumps(payload, separators=(",", ":"), sort_keys=False) + "\n").encode()


def zip_bytes(entries, *, directories=()):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in directories:
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.create_system = 3
            info.external_attr = 0o040755 << 16
            archive.writestr(info, b"")
        for name, data in entries:
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            with warnings.catch_warnings():
                # duplicate.zip intentionally carries two members with one name.
                warnings.simplefilter("ignore", UserWarning)
                archive.writestr(info, data)
    return buffer.getvalue()


def tar_bytes(entries, *, link=False):
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w", format=tarfile.USTAR_FORMAT) as archive:
        for name, data in entries:
            info = tarfile.TarInfo(name)
            info.size = len(data)
            info.mtime = 1577934246
            info.uid = 0
            info.gid = 0
            info.uname = ""
            info.gname = ""
            archive.addfile(info, io.BytesIO(data))
        if link:
            info = tarfile.TarInfo("linked-theme")
            info.type = tarfile.SYMTYPE
            info.linkname = "../outside"
            info.mtime = 1577934246
            info.uid = 0
            info.gid = 0
            info.uname = ""
            info.gname = ""
            archive.addfile(info)
    return buffer.getvalue()


def main():
    fixtures = {
        "v10-full.zip": zip_bytes(FULL),
        "v10-minimal.zip": zip_bytes(MINIMAL),
        "v10-minimal.tar": tar_bytes(MINIMAL),
        "v10-extensiondata-only.zip": zip_bytes(EXTENSION_DATA_ONLY),
        "traversal.zip": zip_bytes(TRAVERSAL),
        "duplicate.zip": zip_bytes(DUPLICATE),
        "backslash.zip": zip_bytes([("library\\games.db", b"synthetic-v10-games-db\n")]),
        "directory.zip": zip_bytes(MINIMAL, directories=("library/",)),
        "v11-full.zip": zip_bytes(FULL + [(MANIFEST, manifest_bytes(FULL))]),
        "v11-wrong-version.zip": zip_bytes(FULL + [(MANIFEST, manifest_bytes(FULL, version=2))]),
        "v11-member-missing.zip": zip_bytes(FULL + [(MANIFEST, manifest_bytes(FULL, omit="library/games.db"))]),
        "v11-hash-mismatch.zip": zip_bytes(FULL + [(MANIFEST, manifest_bytes(FULL, bad_hash=True))]),
        "linked.tar": tar_bytes(MINIMAL, link=True),
    }

    OUT.mkdir(parents=True, exist_ok=True)
    GOLDEN.mkdir(parents=True, exist_ok=True)
    hashes = {}
    for name, data in fixtures.items():
        path = OUT / name
        if path.exists():
            assert path.read_bytes() == data, f"Refusing to modify protected evidence: {path}"
        else:
            path.write_bytes(data)
        hashes[name] = hashlib.sha256(data).hexdigest()

    provenance = {
        "generator": "python3 scripts/port/generate_archive_fixtures.py",
        "layout_source": "legacy/source/Playnite/Backup.cs:61-65,95-185",
        "derivation": "Synthetic payload bytes placed in the exact v10 entry roots and member names. "
                      "The v10 source defines the layout and exclusions, not file bytes; these are "
                      "not captured Windows archives.",
        "fixtures": {name: {"sha256": hashes[name]} for name in sorted(hashes)},
    }
    provenance_path = OUT / "provenance.json"
    provenance_bytes = (json.dumps(provenance, indent=2) + "\n").encode()
    if provenance_path.exists():
        assert provenance_path.read_bytes() == provenance_bytes, \
            f"Refusing to modify protected evidence: {provenance_path}"
    else:
        provenance_path.write_bytes(provenance_bytes)

    listing = {
        "source": "tests/fixtures/archives-v10/v10-full.zip",
        "provenance": provenance["derivation"],
        "entries": [name for name, _ in FULL],
    }
    golden_path = GOLDEN / "v10-full.entries.json"
    golden_bytes = (json.dumps(listing, indent=2) + "\n").encode()
    if golden_path.exists():
        assert golden_path.read_bytes() == golden_bytes, f"Refusing to modify protected evidence: {golden_path}"
    else:
        golden_path.write_bytes(golden_bytes)


if __name__ == "__main__":
    main()
