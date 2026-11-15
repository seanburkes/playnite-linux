#!/usr/bin/env python3
"""Additive p3-005 evidence, sourced only from the frozen v10 tree.

Run from any directory. Existing output must compare byte-for-byte; never overwrite it.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "tests/fixtures/manifests-v10"
copies = {
    "library.yaml": "source/Tests/TestGameLibrary/extension.yaml",
    "metadata.yaml": "source/Tests/TestMetadataPlugin/extension.yaml",
    "generic.yaml": "source/Tests/TestPlugin/extension.yaml",
    "desktop.yaml": "source/Playnite.DesktopApp/Themes/Desktop/Default/theme.yaml",
    "fullscreen.yaml": "source/Playnite.FullscreenApp/Themes/Fullscreen/Default/theme.yaml",
    "emulator.yaml": "source/Playnite/Emulation/Emulators/Dolphin/emulator.yaml",
    "platforms.yaml": "source/Playnite/Emulation/Platforms.yaml",
    "regions.yaml": "source/Playnite/Emulation/Regions.yaml",
}


def add(name, data):
    path = OUT / name
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        assert path.read_bytes() == data, f"Refusing to modify protected evidence: {path}"
    else:
        path.write_bytes(data)


provenance = {}
for name, source in copies.items():
    data = (ROOT / "legacy" / source).read_bytes()
    add(name, data)
    provenance[name] = {"source": "legacy/" + source, "sha256": hashlib.sha256(data).hexdigest(), "derivation": "byte copy"}

# No checked-in current-shape service payload exists: derive values from the model members,
# enum declaration order, and the package versions in AddonManifestTests.cs:20-32.
derived = {
    "addon.json": {
        "AddonId": "test-addon", "Type": 0, "Name": "日本語 🎮 \"quoted\"\\path\nnext",
        "Author": "Test", "IconUrl": "https://example.invalid/icon.png",
        "InstallerManifestUrl": "https://example.invalid/installer.yaml",
        "ShortDescription": "short", "Description": "long", "SourceUrl": "https://example.invalid/source",
        "CommunityNote": "note", "Tags": ["one"], "Links": {"site": "https://example.invalid"},
        "Screenshots": [{"Thumbnail": "thumb", "Image": "image"}],
        "UserAgreement": {"Updated": "2020-01-02T03:04:05Z", "AgreementUrl": "https://example.invalid/license"},
    },
    "installer.json": {"AddonId": "test-addon", "AddonType": 0, "Packages": [
        {"Version": version, "RequiredApiVersion": api, "PackageUrl": f"https://example.invalid/{version}.pext",
         "ReleaseDate": "2020-01-02T03:04:05Z", "Changelog": ["日本語", "quote: \" and slash: \\"]}
        for version, api in [("1.0", "1.0"), ("1.1", "1.0"), ("1.2", "1.1"), ("1.3", "2.0")]
    ]},
    "update.json": {"Version": "10.64", "SdkVersion": "6.0", "FullscreenThemeVersion": "1.0",
                    "DesktopThemeVersion": "1.0", "Sha256": "a" * 64,
                    "PackageUrls": ["https://example.invalid/日本語.zip"], "VersionHistory": ["10.63", "10.64"]},
    "release-note.json": {"Version": "10.64", "Note": "日本語\n\"quoted\"\\path"},
}
for name, obj in derived.items():
    data = (json.dumps(obj, ensure_ascii=False, indent=2) + "\n").encode()
    add(name, data)
    provenance[name] = {"source": "legacy/source/Playnite/Manifests/AddonManifestBase.cs" if name in ("addon.json", "installer.json")
                        else "legacy/source/Playnite/App/UpdateManifest.cs",
                        "sha256": hashlib.sha256(data).hexdigest(),
                        "derivation": "synthetic values for declared wire members; installer versions from legacy/source/Tests/Playnite.Tests/Manifests/AddonManifestTests.cs:20-32; not captured service output"}
add("provenance.json", (json.dumps(provenance, indent=2) + "\n").encode())
