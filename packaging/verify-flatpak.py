#!/usr/bin/env python3
"""Check both declared and effective Flatpak permissions against the narrow allowlist."""
import configparser
import json
from pathlib import Path
import sys

manifest = json.loads(Path(sys.argv[1]).read_text())
expected = ["--socket=wayland", "--socket=fallback-x11", "--share=ipc", "--device=dri"]
if manifest["finish-args"] != expected:
    raise SystemExit(f"Unexpected finish-args: {manifest['finish-args']}")
if len(sys.argv) > 2:
    permissions = configparser.ConfigParser(interpolation=None)
    permissions.read(sys.argv[2])
    # Flatpak expands fallback-x11 into x11 plus the fallback flag.
    expected_context = {"shared": {"ipc"}, "sockets": {"x11", "wayland", "fallback-x11"},
                        "devices": {"dri"}}
    if permissions.sections() != ["Context"] or permissions.defaults():
        raise SystemExit("Unexpected permission sections")
    actual = {key: set(filter(None, value.split(";")))
              for key, value in permissions["Context"].items()}
    if actual != expected_context:
        raise SystemExit(f"Unexpected effective permissions: {actual}")
print("Flatpak permissions match the narrow allowlist")
