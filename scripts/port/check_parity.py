#!/usr/bin/env python3
"""Validate docs/port/surface-parity.tsv.

Usage:
    python3 scripts/port/check_parity.py

Exit codes: 0 valid, 1 invalid.
"""

from __future__ import annotations

import csv
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PARITY = ROOT / "docs" / "port" / "surface-parity.tsv"

COLUMNS = [
    "id", "area", "surface", "v10_ref", "v1_status",
    "test_id", "oracle", "owner", "notes",
]
STATUSES = {"planned", "ported", "adapted", "deferred", "out-of-scope-v1"}


def main() -> int:
    errors: list[str] = []
    if not PARITY.exists():
        print(f"error: {PARITY} not found", file=sys.stderr)
        return 1

    with PARITY.open(encoding="utf-8", newline="") as f:
        rows = list(csv.DictReader(f, delimiter="\t"))

    if rows and list(rows[0].keys()) != COLUMNS:
        errors.append(f"header mismatch: {list(rows[0].keys())} != {COLUMNS}")

    seen: set[str] = set()
    for lineno, row in enumerate(rows, start=2):
        rid = row.get("id", "")
        if not rid:
            errors.append(f"line {lineno}: missing id")
        elif rid in seen:
            errors.append(f"line {lineno}: duplicate id {rid}")
        seen.add(rid)

        status = row.get("v1_status", "")
        if status not in STATUSES:
            errors.append(f"line {lineno} ({rid}): bad status {status!r}")

        if status in {"ported", "adapted"} and not row.get("test_id"):
            errors.append(f"line {lineno} ({rid}): {status} row has no test_id")

        if status in {"deferred", "out-of-scope-v1"} and not row.get("notes"):
            errors.append(f"line {lineno} ({rid}): {status} row needs a reason in notes")

        if not row.get("owner"):
            errors.append(f"line {lineno} ({rid}): missing owner")

    if errors:
        for e in errors:
            print(f"error: {e}", file=sys.stderr)
        return 1

    print(f"ok: {len(rows)} parity rows, {len(seen)} unique ids")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
