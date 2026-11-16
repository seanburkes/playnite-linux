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
SOURCE = OUT / "source"
GOLDEN = ROOT / "tests/golden/archives"
FIXED_TIME = (2020, 1, 2, 3, 4, 6)

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

# A v10-shaped source tree used to pin the backup listing and exclusions. Default and
# non-mode theme directories are present here precisely so the golden proves they are omitted.
SOURCE_FILES = {
    "data/config.json": b'{"DatabasePath":"library"}\n',
    "data/fullscreenConfig.json": b'{"Monitor":0}\n',
    "data/Extensions/TestAddon/extension.yaml": b"Id: TestAddon\nName: Test Addon\n",
    "data/Extensions/TestAddon/TestAddon.dll": b"synthetic-addon\n",
    "data/ExtensionsData/TestAddon/data.json": b'{"enabled":true}\n',
    "data/Themes/Desktop/Custom/theme.yaml": b"Id: CustomDesktop\nMode: Desktop\n",
    "data/Themes/Desktop/Custom/Views/Main.xaml": b"<Grid />\n",
    "data/Themes/Desktop/Default/theme.yaml": b"built-in\n",
    "data/Themes/Desktop/readme.txt": b"not a theme\n",
    "data/Themes/Other/theme.yaml": b"not a mode\n",
    "data/Themes/Fullscreen/Custom/theme.yaml": b"Id: CustomFullscreen\nMode: Fullscreen\n",
    "library/database.json": b'{"Id":"v10-test-library"}\n',
    "library/games.db": b"synthetic-v10-games-db\n",
    "library/files/cover.png": b"\x89PNG\r\n\x1a\nsynthetic-cover\n",
    "library/files/Backgrounds/background.jpg": b"synthetic-background\n",
}

SOURCE_ENTRIES = [
    "config.json",
    "extension/TestAddon/TestAddon.dll",
    "extension/TestAddon/extension.yaml",
    "extensiondata/TestAddon/data.json",
    "fullscreenConfig.json",
    "library/database.json",
    "library/games.db",
    "libraryfiles/Backgrounds/background.jpg",
    "libraryfiles/cover.png",
    "themes/Desktop/Custom/Views/Main.xaml",
    "themes/Desktop/Custom/theme.yaml",
    "themes/Fullscreen/Custom/theme.yaml",
]


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


def patch_declared_size(data, name, size):
    """Rewrite one member's declared uncompressed size in both ZIP headers."""
    data = bytearray(data)
    encoded = name.encode()
    local = data.find(b"PK\x03\x04")
    while local != -1:
        name_length = int.from_bytes(data[local + 26:local + 28], "little")
        if bytes(data[local + 30:local + 30 + name_length]) == encoded:
            data[local + 22:local + 26] = size.to_bytes(4, "little")
            break
        local = data.find(b"PK\x03\x04", local + 4)
    central = data.find(b"PK\x01\x02")
    while central != -1:
        name_length = int.from_bytes(data[central + 28:central + 30], "little")
        if bytes(data[central + 46:central + 46 + name_length]) == encoded:
            data[central + 24:central + 28] = size.to_bytes(4, "little")
            break
        central = data.find(b"PK\x01\x02", central + 4)
    return bytes(data)


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


def write_or_compare(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        assert path.read_bytes() == data, f"Refusing to modify protected evidence: {path}"
    else:
        path.write_bytes(data)


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
        # Synthetic bomb input: the member claims 8 bytes but expands to 64 in both headers.
        "lying-size.zip": patch_declared_size(
            zip_bytes([("library/games.db", b"x" * 64)]), "library/games.db", 8),
        "linked.tar": tar_bytes(MINIMAL, link=True),
    }

    OUT.mkdir(parents=True, exist_ok=True)
    GOLDEN.mkdir(parents=True, exist_ok=True)
    hashes = {}
    for name, data in fixtures.items():
        path = OUT / name
        write_or_compare(path, data)
        hashes[name] = hashlib.sha256(data).hexdigest()

    source_hashes = {}
    for name, data in SOURCE_FILES.items():
        path = SOURCE / name
        write_or_compare(path, data)
        source_hashes[name] = hashlib.sha256(data).hexdigest()

    provenance = {
        "generator": "python3 scripts/port/generate_archive_fixtures.py",
        "layout_source": "legacy/source/Playnite/Backup.cs:61-65,95-185",
        "derivation": "Synthetic payload bytes placed in the exact v10 entry roots and member names. "
                      "The v10 source defines the layout and exclusions, not file bytes; these are "
                      "not captured Windows archives. There is no version or member manifest: that "
                      "is deferred to Phase 4 with the SQLite backup manifest.",
        "fixtures": {name: {"sha256": hashes[name]} for name in sorted(hashes)},
        "source_tree": {name: {"sha256": source_hashes[name]} for name in sorted(source_hashes)},
    }
    write_or_compare(OUT / "provenance.json",
                     (json.dumps(provenance, indent=2) + "\n").encode())

    listing = {
        "source": "tests/fixtures/archives-v10/v10-full.zip",
        "provenance": "v10 entry roots from legacy/source/Playnite/Backup.cs; synthetic payloads",
        "entries": [name for name, _ in FULL],
    }
    write_or_compare(GOLDEN / "v10-full.entries.json",
                     (json.dumps(listing, indent=2) + "\n").encode())

    source_listing = {
        "source": "tests/fixtures/archives-v10/source",
        "provenance": "v10 backup writer layout and exclusions; no version or member manifest",
        "entries": SOURCE_ENTRIES,
    }
    write_or_compare(GOLDEN / "v10-source-backup.entries.json",
                     (json.dumps(source_listing, indent=2) + "\n").encode())

    restore_tree = {
        "source": "tests/fixtures/archives-v10/v10-full.zip",
        "provenance": "v10 restore prefix routing",
        "data": [
            "Extensions/TestAddon/TestAddon.dll",
            "Extensions/TestAddon/extension.yaml",
            "ExtensionsData/TestAddon/data.json",
            "Themes/Desktop/Custom/Views/Main.xaml",
            "Themes/Desktop/Custom/theme.yaml",
            "Themes/Fullscreen/Custom/theme.yaml",
            "config.json",
            "fullscreenConfig.json",
        ],
        "library": [
            "database.json",
            "files/Backgrounds/background.jpg",
            "files/cover.png",
            "games.db",
        ],
    }
    write_or_compare(GOLDEN / "v10-restore-tree.json",
                     (json.dumps(restore_tree, indent=2) + "\n").encode())


if __name__ == "__main__":
    main()
