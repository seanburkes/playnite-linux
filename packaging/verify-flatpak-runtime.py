#!/usr/bin/env python3
"""Report every installed runtime commit before failing on lock mismatches."""
import json
from pathlib import Path
import subprocess
import sys

locks = json.loads(Path(sys.argv[1]).read_text())
stage = sys.argv[2]
failed = False
for ref, expected in locks.items():
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
