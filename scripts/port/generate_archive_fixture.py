#!/usr/bin/env python3
"""Additive source-derived v10 backup corpus; does not call the port."""
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[2]
DEST = ROOT / "tests/fixtures/archive-v10"
DB = ROOT / "legacy/source/Tests/Playnite.Tests/Resources/Sony - PlayStation Vita.db"
SOURCES = [
    ROOT / "legacy/source/Playnite/Archive.cs",
    ROOT / "legacy/source/Playnite/Backup.cs",
    DB,
]
MEMBERS = {
    "config.json": b'{"DatabasePath":"D:\\\\Playnite\\\\library","Language":"en_US"}',
    "fullscreenConfig.json": b'{"StartupPage":0}',
    "library/games.db": DB.read_bytes(),
    "libraryfiles/game-id/cover.txt": "opaque media 日本語\n".encode(),
    "extension/sample/extension.yaml": b"Id: sample\nType: GenericPlugin\n",
    "extensiondata/sample/settings.json": b'{"enabled":true}',
    "themes/Desktop/Custom/theme.yaml": b"ThemeApiVersion: 2.0.0\n",
    "themes/Fullscreen/Custom/theme.yaml": b"ThemeApiVersion: 1.0.0\n",
}


def main():
    DEST.mkdir(parents=True, exist_ok=True)
    # Windows Path.Combine emits backslashes in v10's non-root member names.
    with zipfile.ZipFile(DEST / "backup.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in MEMBERS.items():
            info = zipfile.ZipInfo(name.replace("/", "\\"), (2021, 7, 4, 1, 1, 2))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
    provenance = {
        "generator": "python3 scripts/port/generate_archive_fixture.py",
        "kind": "source-derived, not executed Windows oracle",
        "layout": "Backup.cs lines 94-185; Archive.cs lines 37-67",
        "payload": "Synthetic config/media/addon/theme shapes; unmodified frozen SQLite emulator database as opaque library payload, not a Playnite LiteDB export",
        "sources": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in SOURCES},
        "members": {k: hashlib.sha256(v).hexdigest() for k, v in MEMBERS.items()},
        "archiveSha256": hashlib.sha256((DEST / "backup.zip").read_bytes()).hexdigest(),
    }
    (DEST / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    golden = ROOT / "tests/golden/archive-listing.txt"
    golden.write_text("\n".join(sorted(MEMBERS) + ["backup-manifest.json"]) + "\n")


if __name__ == "__main__":
    main()
