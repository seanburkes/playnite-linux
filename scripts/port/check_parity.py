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
LEGACY_SOURCE = ROOT / "legacy" / "source"

COLUMNS = [
    "id", "area", "surface", "v10_ref", "v1_status",
    "test_id", "oracle", "owner", "notes",
]
STATUSES = {
    "planned", "ported", "adapted", "greenfield",
    "deferred", "out-of-scope-v1", "removed",
}


def resolve_ref(ref: str) -> Path | None:
    ref = ref.strip()
    if ref in {"", "—", "-"}:
        return None
    if ref.startswith("legacy/"):
        ref = ref[len("legacy/"):]
    return LEGACY_SOURCE / ref


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

        if status in {"ported", "adapted", "greenfield"} and not row.get("test_id"):
            errors.append(f"line {lineno} ({rid}): {status} row has no test_id")

        if status in {"deferred", "out-of-scope-v1", "removed"} and not row.get("notes"):
            errors.append(f"line {lineno} ({rid}): {status} row needs a reason in notes")

        if not row.get("owner"):
            errors.append(f"line {lineno} ({rid}): missing owner")

        ref = resolve_ref(row.get("v10_ref", ""))
        if ref is not None and not ref.exists():
            errors.append(f"line {lineno} ({rid}): v10_ref does not exist: {ref.relative_to(ROOT)}")

    if errors:
        for e in errors:
            print(f"error: {e}", file=sys.stderr)
        return 1

    print(f"ok: {len(rows)} parity rows, {len(seen)} unique ids")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
