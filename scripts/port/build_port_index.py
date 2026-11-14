#!/usr/bin/env python3
"""Build or check the generated port queue and ledger from per-unit shards.

Usage:
    python3 scripts/port/build_port_index.py build
    python3 scripts/port/build_port_index.py check

Exit codes: 0 success, 1 validation error or drift.
"""

from __future__ import annotations

import argparse
import difflib
import re
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
UNITS = ROOT / "docs" / "port" / "units"
QUEUE = ROOT / "docs" / "port" / "queue.tsv"
LEDGER = ROOT / "docs" / "port" / "ledger.tsv"

QUEUE_COLUMNS = [
    "id", "wave", "area", "unit", "depends",
    "status", "owner", "oracle", "test", "notes",
]
LEDGER_COLUMNS = ["timestamp", "unit", "from", "to", "actor", "evidence"]
QUEUE_HEADER = "\t".join(QUEUE_COLUMNS)
LEDGER_HEADER = "\t".join(LEDGER_COLUMNS)

QUEUE_SUFFIX = ".queue.tsv"
LEDGER_SUFFIX = ".ledger.tsv"

_WAVE = re.compile(r"W(\d+)")


class PortIndexError(Exception):
    """A shard violates the generated-index contract."""


@dataclass(frozen=True)
class QueueRow:
    fields: tuple[str, ...]


@dataclass(frozen=True)
class LedgerRow:
    fields: tuple[str, ...]

    @property
    def timestamp(self) -> str:
        return self.fields[0]

    @property
    def unit(self) -> str:
        return self.fields[1]


def _read_shard(path: Path, header: str, columns: list[str]) -> list[tuple[int, list[str]]]:
    text = path.read_bytes().decode("utf-8")
    lines = text.splitlines()
    if not lines:
        raise PortIndexError(f"{path}: empty shard")
    if lines[0] != header:
        raise PortIndexError(
            f"{path}: header mismatch: expected {header!r}, got {lines[0]!r}"
        )

    rows: list[tuple[int, list[str]]] = []
    for lineno, line in enumerate(lines[1:], start=2):
        if line == "":
            raise PortIndexError(f"{path}:{lineno}: blank line")
        fields = line.split("\t")
        if len(fields) != len(columns):
            raise PortIndexError(
                f"{path}:{lineno}: expected {len(columns)} columns, got {len(fields)}"
            )
        rows.append((lineno, fields))
    return rows


def _collect_queue(units: Path) -> tuple[dict[str, QueueRow], list[str]]:
    errors: list[str] = []
    rows: dict[str, QueueRow] = {}
    seen_ids: set[str] = set()

    for path in sorted(units.glob(f"*{QUEUE_SUFFIX}")):
        unit_id = path.name[: -len(QUEUE_SUFFIX)]
        try:
            parsed = _read_shard(path, QUEUE_HEADER, QUEUE_COLUMNS)
        except PortIndexError as exc:
            errors.append(str(exc))
            continue

        if len(parsed) != 1:
            errors.append(f"{path}: expected exactly one queue row, got {len(parsed)}")
            continue

        lineno, fields = parsed[0]
        if fields[0] != unit_id:
            errors.append(
                f"{path}:{lineno}: queue id {fields[0]!r} does not match filename {unit_id!r}"
            )
        if fields[0] in seen_ids:
            errors.append(f"{path}:{lineno}: duplicate unit id {fields[0]!r}")
            continue
        seen_ids.add(fields[0])
        rows[unit_id] = QueueRow(tuple(fields))

    return rows, errors


def _collect_ledger(units: Path, queue_ids: set[str]) -> tuple[list[LedgerRow], list[str]]:
    errors: list[str] = []
    rows: list[LedgerRow] = []

    for path in sorted(units.glob(f"*{LEDGER_SUFFIX}")):
        unit_id = path.name[: -len(LEDGER_SUFFIX)]
        if unit_id not in queue_ids:
            errors.append(f"{path}: ledger shard has no queue shard for {unit_id!r}")
            continue
        try:
            parsed = _read_shard(path, LEDGER_HEADER, LEDGER_COLUMNS)
        except PortIndexError as exc:
            errors.append(str(exc))
            continue

        for lineno, fields in parsed:
            if fields[1] != unit_id:
                errors.append(
                    f"{path}:{lineno}: ledger unit {fields[1]!r} does not match filename {unit_id!r}"
                )
                continue
            try:
                _timestamp(fields[0])
            except ValueError as exc:
                errors.append(f"{path}:{lineno}: {exc}")
                continue
            rows.append(LedgerRow(tuple(fields)))

    return rows, errors


def _timestamp(value: str) -> datetime:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise ValueError(f"invalid timestamp {value!r}") from exc
    if parsed.tzinfo is None:
        raise ValueError(f"timestamp {value!r} needs a UTC offset")
    return parsed.astimezone(timezone.utc)


def _wave_key(wave: str) -> tuple[int, int, str]:
    match = _WAVE.fullmatch(wave)
    if match:
        return (0, int(match.group(1)), "")
    return (1, 0, wave)


def render(units: Path) -> tuple[bytes, bytes]:
    """Validate every shard and return the generated queue and ledger bytes."""
    if not units.is_dir():
        raise PortIndexError(f"{units}: shard directory not found")

    queue_rows, errors = _collect_queue(units)
    if not queue_rows and not errors:
        errors.append(f"{units}: no queue shards found")
    ledger_rows, ledger_errors = _collect_ledger(units, set(queue_rows))
    errors.extend(ledger_errors)
    if errors:
        raise PortIndexError("\n".join(errors))

    queue_rows_sorted = sorted(
        queue_rows.values(), key=lambda row: (_wave_key(row.fields[1]), row.fields[0])
    )
    ledger_rows_sorted = sorted(
        ledger_rows, key=lambda row: (_timestamp(row.timestamp), row.unit)
    )

    queue_text = QUEUE_HEADER + "\n" + "".join(
        "\t".join(row.fields) + "\n" for row in queue_rows_sorted
    )
    ledger_text = LEDGER_HEADER + "\n" + "".join(
        "\t".join(row.fields) + "\n" for row in ledger_rows_sorted
    )
    return queue_text.encode("utf-8"), ledger_text.encode("utf-8")


def _check_file(path: Path, generated: bytes) -> bool:
    committed = path.read_bytes() if path.exists() else b""
    if committed == generated:
        return True

    print(f"error: {path} is not generated output", file=sys.stderr)
    old_lines = committed.decode("utf-8", "replace").splitlines()
    new_lines = generated.decode("utf-8").splitlines()
    diff = difflib.unified_diff(
        old_lines,
        new_lines,
        fromfile=str(path),
        tofile=f"{path} (generated)",
        lineterm="",
    )
    for line in diff:
        print(line, file=sys.stderr)
    return False


def main(
    argv: list[str] | None = None,
    *,
    units: Path = UNITS,
    queue: Path = QUEUE,
    ledger: Path = LEDGER,
) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("build", "check"))
    args = parser.parse_args(argv)

    try:
        queue_bytes, ledger_bytes = render(units)
    except PortIndexError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    if args.mode == "build":
        queue.write_bytes(queue_bytes)
        ledger.write_bytes(ledger_bytes)
        print(f"wrote {queue} and {ledger}")
        return 0

    queue_ok = _check_file(queue, queue_bytes)
    ledger_ok = _check_file(ledger, ledger_bytes)
    if not (queue_ok and ledger_ok):
        return 1
    print("port index is current")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
