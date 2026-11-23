#!/usr/bin/env python3
"""Report every installed runtime commit before failing on lock mismatches."""
import argparse
import json
from pathlib import Path
import platform
import subprocess
import sys


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("lock_file", help="Path to flatpak-runtime-lock.json")
    parser.add_argument("stage", help="Stage label for log output")
    parser.add_argument(
        "--arch",
        default=platform.machine(),
        help="Target Flatpak architecture (default: host machine)",
    )
    return parser.parse_args(argv)


args = parse_args(sys.argv[1:])
lock_data = json.loads(Path(args.lock_file).read_text())
if args.arch not in lock_data:
    raise SystemExit(f"Unsupported Flatpak runtime lock architecture: {args.arch}")
locks = lock_data[args.arch]
stage = args.stage
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
