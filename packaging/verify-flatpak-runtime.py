#!/usr/bin/env python3
"""Report every installed runtime commit before failing on lock mismatches."""
import json
from pathlib import Path
import subprocess
import sys

locks = json.loads(Path(sys.argv[1]).read_text())
stage = sys.argv[2]
failed = False
inventory = subprocess.run(["flatpak", "list", "--user", "--runtime", "--all", "--columns=ref"],
                           capture_output=True, text=True)
if inventory.returncode != 0:
    print(inventory.stderr, end="", file=sys.stderr)
    raise SystemExit("Cannot enumerate installed Flatpak runtime refs")
installed = {ref.removeprefix("runtime/") for ref in inventory.stdout.splitlines() if ref}
refs = {**locks, **{ref: "<unlisted>" for ref in sorted(installed - locks.keys())}}
for ref, expected in refs.items():
    result = subprocess.run(["flatpak", "info", "--user", "--show-commit", ref],
                            capture_output=True, text=True)
    actual = result.stdout.strip() if result.returncode == 0 else "<unavailable>"
    print(f"{stage}: {ref} expected={expected} actual={actual}", flush=True)
    if result.returncode != 0:
        print(result.stderr, end="", file=sys.stderr)
    if result.returncode != 0 or actual != expected:
        failed = True
if failed:
    raise SystemExit("Flatpak runtime lock mismatch")
