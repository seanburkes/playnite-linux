#!/usr/bin/env python3
"""Build or check the generated port queue and ledger from per-unit shards.

Usage:
    python3 scripts/port/build_port_index.py build
    python3 scripts/port/build_port_index.py check [--against <ref>]
    python3 scripts/port/build_port_index.py check --shards-only [--against <ref>]
    python3 scripts/port/build_port_index.py check --shards-only --against <ref> \
        --forbid-aggregate-changes

`check --against <ref>` additionally verifies history: every base ledger row must
still exist at head with identical field values, per unit, as a multiset, so a
row may never be deleted, edited, or duplicated. Queue shards and ledger rows
must survive unless a unit is explicitly retired in
`docs/port/units/removed.tsv`, and <ref> must be a strict ancestor of HEAD. CI
passes the ref with `--against`; the PORT_INDEX_BASE_REF environment variable is
only a local convenience fallback.

History is append-only in content, not in order. A new row may carry any instant,
including one older than rows already recorded, so an out-of-order merge cannot
fail validation. `build` sorts the generated aggregate by instant then unit; that
ordering is produced by the parent-owned regeneration on main, and contributors
never hand-order or hand-edit an aggregate.

A timestamp correction recorded in `docs/port/units/ledger-corrections.tsv` in
the same change may change only the timestamp of one existing base row, and only
to a later instant. At most one record per unit may do so. A record names a unit
that was live when it was recorded, so a later retirement does not invalidate it;
correction records are themselves append-only.

Pull requests validate the shard tree only. `--shards-only` skips the byte
comparison against the committed aggregates, which are generated on main by the
post-merge `port-index` workflow. `--forbid-aggregate-changes` fails when the
branch changed either aggregate since its merge base with `--against`, so a pull
request owns only its unit shards and a stale branch does not appear to revert
aggregates that landed on main.

The migration PR that first adds `docs/port/units` is the one legitimate
pre-migration base: `--allow-pre-migration-base` acknowledges the history skip
and prints a notice instead of passing silently.

Exit codes: 0 success, 1 validation error or drift.
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import os
import re
import subprocess
import sys
import tempfile
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
UNITS = ROOT / "docs" / "port" / "units"
QUEUE = ROOT / "docs" / "port" / "queue.tsv"
LEDGER = ROOT / "docs" / "port" / "ledger.tsv"

LOCAL_PATH = re.compile(
    r"/(?:home|Users|root)(?:/|\b)|"
    r"[A-Za-z]:[\\/](?:Users|Documents and Settings)(?:[\\/]|\b)|"
    r"firstmate[\\/](?:data|state)(?:[\\/]|\b)|\.treehouse(?:[\\/]|\b)",
    re.IGNORECASE,
)
# Exact pre-guard lines only; see docs/port/LOCAL-PATH-EXCEPTIONS.md.
LOCAL_PATH_EXCEPTIONS = {
    "docs/port/06-OPERATIONS.md": {
        "a13eb8ee06c92eec70f9e001ac0f87035bc11eccd8e15a7e97c83a0271c86926",
        "031f509b4f17e702cf346ce9b1c624ceec785150580b804ae1b191bef66f1d76",
        "9652e10ff8c0f2d1e67e0b6ba02f464c8b2a44ebbb5f41b4e72ebe401dd56d1c",
    },
    "docs/port/07-FIRSTMATE.md": {
        "0a682c0fd67ea978f1f9f75fda303796077061353f0eb2e35386f72913012cff",
        "921b02fb751da919edeae705db455e43ccf2316b5661e239ad24ba04a91add8e",
        "5b6d8f878e65a4e4d03cdb7a831632b8ee248daf20baf4b5779d12475ccd0f38",
    },
    "docs/port/08-STEAM-SPEC.md": {
        "a0ce67ccb5c09f03ec392bd7e76a83c96777f7e11b89b782c9a5984ec7b2b53f",
    },
    "docs/port/HANDOFF-2026-11-14.md": {
        "4726936fddae5fa3aef6be25e3dbd9290079229adc1cc4589a1629ed8d59e583",
        "b33cc46a2393554627570482e7feaccbf2c2283e0f01ed448f11d3215add4f72",
        "530aba180479f8a071ec8d607f708a23a472288134511eaf8f5163a9aa7168ac",
    },
    "docs/port/p1-009-steam-corpus-proposal.md": {
        "2081a4de964630e64556ddbf34d987ee168519eaf5a8ca7652d4a2c992114490",
        "9cece94d5ff21a68f4f97bd87cdf7433d5290ff3986fd311f7a459cddfb82dfb",
        "beef9ecb877ead399384f27d33328104a14b98e5c0000ecb9ed5fc42227d7d9b",
        "1bec7e4f2cc03557536b1742916d73a0b519b29b91b15ca5d681e743cfb9de18",
        "106d2de516a2844bd8006b18af0c8e637e184b9e3c365d0619ad450d5f2f54f6",
        "de0bf05d0723c1dd7895b73b6e02c9fb10b2eb99c644e9fa3eed49f7ee72d39b",
        "de8c61d186728aba493f70bc243c23bf9987f04d995ea1f11acf7634d6128311",
        "981072d68a390ea45bc6ccd4c41c64da92ba294c79bd9a15c78efa6461c77cf2",
        "62249f8f083da83c6316fa117726126664016dc2d07d9ed7888f7b3b60048021",
        "6e378ec52448e2a821d5b29aecde866b11992e3908cfb7b137142d941e3330c3",
        "08e0c3928b79f8e6f32b313c3ba46e01d3546592b67dedb139814500be9a6ef7",
    },
    "docs/port/units/p2-002.ledger.tsv": {
        "b02112a1a036cb33f262c8f392a4d3a9e5d21fc4022d817d3599337f97aabc29",
        "352d5a6b7b0be9aa4875060cea973c62b7a0384b827ba8606d965e0f230e0e01",
        "d28753330deb3b3927270edbffdd380541f924cfd7f9982d314705fc9a4508c8",
    },
    "docs/port/units/p2-ci-cost.ledger.tsv": {
        "1e65eafe7f62f8192f5ac778f624dd69ae23450766b449c687ad0f5ec5d24207",
    },
}
LOCAL_PATH_EXCEPTIONS["docs/port/ledger.tsv"] = (
    LOCAL_PATH_EXCEPTIONS["docs/port/units/p2-002.ledger.tsv"]
    | LOCAL_PATH_EXCEPTIONS["docs/port/units/p2-ci-cost.ledger.tsv"]
)


def local_path_errors(text: str, label: str) -> list[str]:
    """Reject new local paths; each exact historical line is allowed once per file."""
    allowed = LOCAL_PATH_EXCEPTIONS.get(label, set()).copy()
    errors = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        if not LOCAL_PATH.search(line):
            continue
        digest = hashlib.sha256(line.encode("utf-8")).hexdigest()
        if digest in allowed:
            allowed.remove(digest)
        else:
            errors.append(f"{label}:{lineno}: absolute local path in row; "
                          "use a repository-relative reference")
    return errors


def document_path_errors(root: Path) -> list[str]:
    """Scan documentation, including staged/new files, without inspecting oracle data."""
    paths = list((root / "docs").rglob("*"))
    paths.extend(path for path in root.iterdir() if path.suffix.lower() == ".md")
    errors = []
    for path in sorted(paths):
        if not path.is_file() or path.suffix.lower() not in {".md", ".tsv", ".txt", ".rst", ".adoc"}:
            continue
        label = path.relative_to(root).as_posix()
        try:
            text = path.read_bytes().decode("utf-8")
        except (OSError, UnicodeDecodeError) as exc:
            errors.append(f"{label}: cannot read documentation: {exc}")
            continue
        errors.extend(local_path_errors(text, label))
    return errors

QUEUE_COLUMNS = [
    "id", "wave", "area", "unit", "depends",
    "status", "owner", "oracle", "test", "notes",
]
LEDGER_COLUMNS = ["timestamp", "unit", "from", "to", "actor", "evidence"]
QUEUE_HEADER = "\t".join(QUEUE_COLUMNS)
LEDGER_HEADER = "\t".join(LEDGER_COLUMNS)

QUEUE_SUFFIX = ".queue.tsv"
LEDGER_SUFFIX = ".ledger.tsv"
README_NAME = "README.md"
REMOVED_NAME = "removed.tsv"
REMOVED_COLUMNS = ["unit", "reason", "approved-by"]
REMOVED_HEADER = "\t".join(REMOVED_COLUMNS)
CORRECTIONS_NAME = "ledger-corrections.tsv"
CORRECTIONS_COLUMNS = [
    "unit", "previous-timestamp", "corrected-timestamp", "actor", "reason",
]
CORRECTIONS_HEADER = "\t".join(CORRECTIONS_COLUMNS)

# Conservative unit-id shape: lowercase letters, digits and single hyphens.
# No dots, whitespace, path separators, or empty ids.
_UNIT_ID = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")
_WAVE = re.compile(r"W(\d+)")

ALLOWED_UNIT_FILES = (
    f"{README_NAME}, {REMOVED_NAME}, {CORRECTIONS_NAME}, "
    f"<unit-id>{QUEUE_SUFFIX} and <unit-id>{LEDGER_SUFFIX}"
)


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


@dataclass(frozen=True)
class LedgerCorrection:
    """One recorded, one-off timestamp correction of an existing ledger row."""

    fields: tuple[str, ...]

    @property
    def unit(self) -> str:
        return self.fields[0]

    @property
    def previous(self) -> str:
        return self.fields[1]

    @property
    def corrected(self) -> str:
        return self.fields[2]


@dataclass(frozen=True)
class ShardIndex:
    """The validated head shards."""

    queue: dict[str, QueueRow]
    ledger: dict[str, tuple[LedgerRow, ...]]
    removed: dict[str, tuple[str, ...]]
    corrections: tuple[LedgerCorrection, ...]


@dataclass(frozen=True)
class BaseSnapshot:
    """The shard and aggregate content at a base ref (for history checks)."""

    queue_shards: dict[str, tuple[str, ...]]
    ledger_shards: dict[str, tuple[tuple[str, ...], ...]]
    queue_aggregate: tuple[tuple[str, ...], ...]
    ledger_aggregate: tuple[tuple[str, ...], ...]
    removed: dict[str, tuple[str, ...]]
    corrections: tuple[tuple[str, ...], ...] = ()


def _parse_shard_bytes(
    data: bytes, label: str, header: str, columns: list[str]
) -> list[tuple[int, list[str]]]:
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise PortIndexError(f"{label}: invalid UTF-8: {exc}") from exc
    lines = text.splitlines()
    path_errors = local_path_errors(text, "docs/port/units/" + Path(label).name)
    if path_errors:
        raise PortIndexError("\n".join(path_errors))
    if not lines:
        raise PortIndexError(f"{label}: empty shard")
    if lines[0] != header:
        raise PortIndexError(
            f"{label}: header mismatch: expected {header!r}, got {lines[0]!r}"
        )

    rows: list[tuple[int, list[str]]] = []
    for lineno, line in enumerate(lines[1:], start=2):
        if line == "":
            raise PortIndexError(f"{label}:{lineno}: blank line")
        fields = line.split("\t")
        if len(fields) != len(columns):
            raise PortIndexError(
                f"{label}:{lineno}: expected {len(columns)} columns, got {len(fields)}"
            )
        rows.append((lineno, fields))
    return rows


def _read_shard(path: Path, header: str, columns: list[str]) -> list[tuple[int, list[str]]]:
    if not path.is_file():
        raise PortIndexError(f"{path}: not a regular file")
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise PortIndexError(f"{path}: cannot read shard: {exc}") from exc
    return _parse_shard_bytes(data, str(path), header, columns)


def _read_aggregate(path: Path, header: str) -> tuple[tuple[str, ...], ...]:
    if not path.exists():
        return ()
    if not path.is_file():
        raise PortIndexError(f"{path}: not a regular file")
    try:
        text = path.read_bytes().decode("utf-8")
    except UnicodeDecodeError as exc:
        raise PortIndexError(f"{path}: invalid UTF-8: {exc}") from exc
    lines = text.splitlines()
    if not lines:
        return ()
    if lines[0] != header:
        raise PortIndexError(
            f"{path}: header mismatch: expected {header!r}, got {lines[0]!r}"
        )
    rows: list[tuple[str, ...]] = []
    for lineno, line in enumerate(lines[1:], start=2):
        if line == "":
            raise PortIndexError(f"{path}:{lineno}: blank line")
        rows.append(tuple(line.split("\t")))
    return tuple(rows)


def _discover(
    units: Path,
) -> tuple[list[Path], list[Path], Path | None, Path | None, list[str]]:
    """Enumerate the units directory, rejecting anything not part of the contract."""
    errors: list[str] = []
    queue_paths: list[Path] = []
    ledger_paths: list[Path] = []
    removed_path: Path | None = None
    corrections_path: Path | None = None

    for entry in sorted(units.iterdir(), key=lambda path: path.name):
        name = entry.name
        if name == README_NAME:
            if not entry.is_file():
                errors.append(f"{entry}: {README_NAME} must be a regular file")
            continue
        if name == REMOVED_NAME:
            if not entry.is_file():
                errors.append(f"{entry}: {REMOVED_NAME} must be a regular file")
            else:
                removed_path = entry
            continue
        if name == CORRECTIONS_NAME:
            if not entry.is_file():
                errors.append(f"{entry}: {CORRECTIONS_NAME} must be a regular file")
            else:
                corrections_path = entry
            continue

        suffix = None
        if name.endswith(QUEUE_SUFFIX):
            suffix = QUEUE_SUFFIX
        elif name.endswith(LEDGER_SUFFIX):
            suffix = LEDGER_SUFFIX
        if suffix is None:
            errors.append(
                f"{entry}: unexpected entry under {units}; allowed: {ALLOWED_UNIT_FILES}"
            )
            continue

        unit_id = name[: -len(suffix)]
        if not entry.is_file():
            errors.append(f"{entry}: shard path is not a regular file")
            continue
        if not _UNIT_ID.fullmatch(unit_id):
            errors.append(
                f"{entry}: invalid unit id {unit_id!r}; use lowercase letters, "
                "digits and hyphens (no dots, spaces or path separators)"
            )
            continue
        if suffix == QUEUE_SUFFIX:
            queue_paths.append(entry)
        else:
            ledger_paths.append(entry)

    return queue_paths, ledger_paths, removed_path, corrections_path, errors


def _collect_queue(paths: list[Path]) -> tuple[dict[str, QueueRow], list[str]]:
    errors: list[str] = []
    rows: dict[str, QueueRow] = {}
    seen_ids: set[str] = set()

    for path in sorted(paths, key=lambda item: item.name):
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
        if not _UNIT_ID.fullmatch(fields[0]):
            errors.append(
                f"{path}:{lineno}: invalid unit id {fields[0]!r} in queue row; "
                "use lowercase letters, digits and hyphens"
            )
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


def _collect_ledger(
    paths: list[Path], queue_ids: set[str]
) -> tuple[dict[str, tuple[LedgerRow, ...]], list[str]]:
    errors: list[str] = []
    rows: dict[str, tuple[LedgerRow, ...]] = {}
    seen_rows: dict[tuple[object, ...], str] = {}

    for path in sorted(paths, key=lambda item: item.name):
        unit_id = path.name[: -len(LEDGER_SUFFIX)]
        if unit_id not in queue_ids:
            errors.append(f"{path}: ledger shard has no queue shard for {unit_id!r}")
            continue
        try:
            parsed = _read_shard(path, LEDGER_HEADER, LEDGER_COLUMNS)
        except PortIndexError as exc:
            errors.append(str(exc))
            continue

        shard_rows: list[LedgerRow] = []
        for lineno, fields in parsed:
            if not _UNIT_ID.fullmatch(fields[1]):
                errors.append(
                    f"{path}:{lineno}: invalid unit id {fields[1]!r} in ledger row; "
                    "use lowercase letters, digits and hyphens"
                )
            if fields[1] != unit_id:
                errors.append(
                    f"{path}:{lineno}: ledger unit {fields[1]!r} does not match filename {unit_id!r}"
                )
                continue
            try:
                instant = _timestamp(fields[0])
            except ValueError as exc:
                errors.append(f"{path}:{lineno}: {exc}")
                continue
            # Duplicate identity: one unit, instant and transition content.
            identity = (instant, *fields[1:])
            if identity in seen_rows:
                errors.append(
                    f"{path}:{lineno}: duplicate ledger row for {fields[1]!r} at "
                    f"{fields[0]}; first recorded at {seen_rows[identity]}"
                )
                continue
            seen_rows[identity] = f"{path}:{lineno}"
            shard_rows.append(LedgerRow(tuple(fields)))
        rows[unit_id] = tuple(shard_rows)

    return rows, errors


def _collect_removed(path: Path) -> dict[str, tuple[str, ...]]:
    parsed = _read_shard(path, REMOVED_HEADER, REMOVED_COLUMNS)
    removed: dict[str, tuple[str, ...]] = {}
    for lineno, fields in parsed:
        unit_id, reason, approved_by = fields
        if not _UNIT_ID.fullmatch(unit_id):
            raise PortIndexError(
                f"{path}:{lineno}: invalid unit id {unit_id!r} in retirement record"
            )
        if not reason.strip():
            raise PortIndexError(f"{path}:{lineno}: retirement record needs a reason")
        if not approved_by.strip():
            raise PortIndexError(
                f"{path}:{lineno}: retirement record needs a non-empty approved-by"
            )
        if unit_id in removed:
            raise PortIndexError(
                f"{path}:{lineno}: duplicate retirement record for {unit_id!r}"
            )
        removed[unit_id] = tuple(fields)
    return removed


def _collect_corrections(path: Path) -> tuple[LedgerCorrection, ...]:
    """Read and validate the recorded ledger timestamp corrections."""
    parsed = _read_shard(path, CORRECTIONS_HEADER, CORRECTIONS_COLUMNS)
    corrections: list[LedgerCorrection] = []
    seen_units: dict[str, int] = {}
    for lineno, fields in parsed:
        unit_id, previous, corrected, actor, reason = fields
        if not _UNIT_ID.fullmatch(unit_id):
            raise PortIndexError(
                f"{path}:{lineno}: invalid unit id {unit_id!r} in correction record"
            )
        if unit_id in seen_units:
            raise PortIndexError(
                f"{path}:{lineno}: multiple correction records for {unit_id!r}; a "
                "unit's ledger row may be corrected at most once, so a second "
                f"record is rejected (first recorded at line {seen_units[unit_id]})"
            )
        seen_units[unit_id] = lineno
        try:
            previous_instant = _timestamp(previous)
            corrected_instant = _timestamp(corrected)
        except ValueError as exc:
            raise PortIndexError(f"{path}:{lineno}: {exc}") from exc
        if corrected_instant <= previous_instant:
            raise PortIndexError(
                f"{path}:{lineno}: corrected timestamp {corrected!r} must be later "
                f"than previous timestamp {previous!r}"
            )
        if not actor.strip():
            raise PortIndexError(f"{path}:{lineno}: correction record needs an actor")
        if not reason.strip():
            raise PortIndexError(f"{path}:{lineno}: correction record needs a reason")
        corrections.append(LedgerCorrection(tuple(fields)))
    return tuple(corrections)


def _timestamp(value: str) -> datetime:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise ValueError(f"invalid timestamp {value!r}") from exc
    if parsed.tzinfo is None:
        raise ValueError(f"timestamp {value!r} needs a UTC offset")
    return parsed.astimezone(timezone.utc)


def _timestamp_or_none(value: str) -> datetime | None:
    try:
        return _timestamp(value)
    except ValueError:
        return None


def _wave_key(wave: str) -> tuple[int, int, str]:
    match = _WAVE.fullmatch(wave)
    if match:
        return (0, int(match.group(1)), "")
    return (1, 0, wave)


def build_index(units: Path) -> ShardIndex:
    """Validate every head shard and return the parsed index."""
    if not units.is_dir():
        raise PortIndexError(f"{units}: shard directory not found")

    queue_paths, ledger_paths, removed_path, corrections_path, errors = _discover(units)
    queue_rows, queue_errors = _collect_queue(queue_paths)
    errors.extend(queue_errors)
    if not queue_rows and not errors:
        errors.append(f"{units}: no queue shards found")

    ledger_rows, ledger_errors = _collect_ledger(ledger_paths, set(queue_rows))
    errors.extend(ledger_errors)

    removed: dict[str, tuple[str, ...]] = {}
    if removed_path is not None:
        try:
            removed = _collect_removed(removed_path)
        except PortIndexError as exc:
            errors.append(str(exc))
    for unit_id in sorted(removed):
        if unit_id in queue_rows or unit_id in ledger_rows:
            errors.append(
                f"{removed_path}: {unit_id!r} is recorded as retired but its shard still exists"
            )

    corrections: tuple[LedgerCorrection, ...] = ()
    if corrections_path is not None:
        try:
            corrections = _collect_corrections(corrections_path)
        except PortIndexError as exc:
            errors.append(str(exc))

    # A correction must name a unit that existed when it was recorded. A retired
    # unit has no shards today, so a historical correction stays valid after a
    # later retirement; only a unit that never existed, or that still exists
    # without the shard the correction needs, is a dead record.
    for correction in corrections:
        if correction.unit in removed:
            continue
        if correction.unit not in queue_rows:
            errors.append(
                f"docs/port/units/{CORRECTIONS_NAME}: correction for "
                f"{correction.unit!r} at {correction.previous}: no queue shard for "
                "that unit and it is not retired (dead correction record)"
            )
        elif correction.unit not in ledger_rows:
            errors.append(
                f"docs/port/units/{CORRECTIONS_NAME}: correction for "
                f"{correction.unit!r} at {correction.previous}: the unit has no "
                "ledger shard to correct"
            )

    if errors:
        raise PortIndexError("\n".join(errors))
    return ShardIndex(queue_rows, ledger_rows, removed, corrections)


def _ordered_ledger_rows(index: ShardIndex) -> tuple[LedgerRow, ...]:
    """The ledger rows in generated aggregate order.

    The key is total: instant, then unit, then the remaining fields. Rows that
    share an instant and unit therefore order by content rather than by their
    position in a shard, so reordering a shard cannot change generated bytes.
    """
    rows = [row for unit_id in sorted(index.ledger) for row in index.ledger[unit_id]]
    return tuple(
        sorted(
            rows,
            key=lambda row: (_timestamp(row.timestamp), row.unit, row.fields[2:]),
        )
    )


def render_index(index: ShardIndex) -> tuple[bytes, bytes]:
    queue_rows_sorted = sorted(
        index.queue.values(), key=lambda row: (_wave_key(row.fields[1]), row.fields[0])
    )
    ledger_rows_sorted = _ordered_ledger_rows(index)

    queue_text = QUEUE_HEADER + "\n" + "".join(
        "\t".join(row.fields) + "\n" for row in queue_rows_sorted
    )
    ledger_text = LEDGER_HEADER + "\n" + "".join(
        "\t".join(row.fields) + "\n" for row in ledger_rows_sorted
    )
    return queue_text.encode("utf-8"), ledger_text.encode("utf-8")


def render(units: Path) -> tuple[bytes, bytes]:
    """Validate every shard and return the generated queue and ledger bytes."""
    return render_index(build_index(units))


def snapshot_from_dir(units: Path, queue: Path, ledger: Path) -> BaseSnapshot | None:
    """Read a base checkout of the shards and aggregates for history comparison."""
    if not units.is_dir():
        return None

    queue_shards: dict[str, tuple[str, ...]] = {}
    for path in sorted(units.glob(f"*{QUEUE_SUFFIX}"), key=lambda item: item.name):
        unit_id = path.name[: -len(QUEUE_SUFFIX)]
        if not _UNIT_ID.fullmatch(unit_id):
            raise PortIndexError(f"{path}: invalid unit id {unit_id!r}")
        parsed = _read_shard(path, QUEUE_HEADER, QUEUE_COLUMNS)
        if len(parsed) != 1:
            raise PortIndexError(f"{path}: expected exactly one queue row, got {len(parsed)}")
        fields = parsed[0][1]
        if fields[0] != unit_id:
            raise PortIndexError(
                f"{path}: queue id {fields[0]!r} does not match filename {unit_id!r}"
            )
        queue_shards[unit_id] = tuple(fields)

    ledger_shards: dict[str, tuple[tuple[str, ...], ...]] = {}
    for path in sorted(units.glob(f"*{LEDGER_SUFFIX}"), key=lambda item: item.name):
        unit_id = path.name[: -len(LEDGER_SUFFIX)]
        if not _UNIT_ID.fullmatch(unit_id):
            raise PortIndexError(f"{path}: invalid unit id {unit_id!r}")
        if unit_id not in queue_shards:
            raise PortIndexError(f"{path}: ledger shard has no queue shard for {unit_id!r}")
        parsed = _read_shard(path, LEDGER_HEADER, LEDGER_COLUMNS)
        rows: list[tuple[str, ...]] = []
        for lineno, fields in parsed:
            if fields[1] != unit_id:
                raise PortIndexError(
                    f"{path}:{lineno}: ledger unit {fields[1]!r} does not match filename {unit_id!r}"
                )
            try:
                _timestamp(fields[0])
            except ValueError as exc:
                raise PortIndexError(f"{path}:{lineno}: {exc}") from exc
            rows.append(tuple(fields))
        ledger_shards[unit_id] = tuple(rows)

    removed: dict[str, tuple[str, ...]] = {}
    removed_path = units / REMOVED_NAME
    if removed_path.exists():
        removed = _collect_removed(removed_path)

    base_corrections: tuple[tuple[str, ...], ...] = ()
    corrections_path = units / CORRECTIONS_NAME
    if corrections_path.exists():
        base_corrections = tuple(
            correction.fields for correction in _collect_corrections(corrections_path)
        )

    return BaseSnapshot(
        queue_shards=queue_shards,
        ledger_shards=ledger_shards,
        queue_aggregate=_read_aggregate(queue, QUEUE_HEADER),
        ledger_aggregate=_read_aggregate(ledger, LEDGER_HEADER),
        removed=removed,
        corrections=base_corrections,
    )


def _git(root: Path, *args: str) -> bytes:
    completed = subprocess.run(
        ["git", "-C", str(root), *args],
        capture_output=True,
    )
    if completed.returncode != 0:
        stderr = completed.stderr.decode("utf-8", "replace").strip()
        raise PortIndexError(f"git {' '.join(args)} failed: {stderr}")
    return completed.stdout


def _repo_relative(path: Path, root: Path) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def _git_bytes(root: Path, *args: str) -> bytes | None:
    """Return stdout for a successful git call, or None for a missing object."""
    completed = subprocess.run(["git", "-C", str(root), *args],
                               capture_output=True)
    return completed.stdout if completed.returncode == 0 else None


def changed_aggregates(root: Path, ref: str, queue: Path, ledger: Path,
                       recorded_base: str | None = None) -> list[str]:
    """Paths of generated aggregates changed outside both base sides.

    `ref` is the fork point (the merge base of the recorded event base and HEAD).
    The comparison is three-dot: a stale branch must not appear to revert aggregate
    changes that landed on main after it forked. A file whose HEAD content equals
    the fork point or the recorded event base is not an aggregate edit. Any other
    content, and every add/delete/rename, still fails.
    """
    resolved = _git(root, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}").strip()
    fork = resolved.decode("ascii", "replace")
    if not fork:
        raise PortIndexError(f"cannot resolve base ref {ref!r}")
    merge_base = (
        _git(root, "merge-base", fork, "HEAD").strip().decode("ascii", "replace")
    )
    if not merge_base:
        raise PortIndexError(f"no merge base between {ref!r} and HEAD")
    recorded = fork
    if recorded_base is not None:
        resolved_recorded = _git(
            root, "rev-parse", "--verify", "--quiet", f"{recorded_base}^{{commit}}"
        ).strip().decode("ascii", "replace")
        if not resolved_recorded:
            raise PortIndexError(f"cannot resolve recorded base {recorded_base!r}")
        recorded = resolved_recorded
    paths = (_repo_relative(queue, root), _repo_relative(ledger, root))
    listing = _git(
        root, "diff", "--name-only", "-z", merge_base, "HEAD", "--", *paths
    )
    changed = [name for name in listing.decode("utf-8", "replace").split("\0") if name]
    if not changed:
        return []
    base_sides = {merge_base, recorded}
    allowed: dict[str, set[bytes]] = {name: set() for name in changed}
    for side in base_sides:
        for name in changed:
            content = _git_bytes(root, "show", f"{side}:{name}")
            if content is not None:
                allowed[name].add(content)
    failures = []
    for name in changed:
        head = _git_bytes(root, "show", f"HEAD:{name}")
        merge_content = _git_bytes(root, "show", f"{merge_base}:{name}")
        if head is None or merge_content is None:
            failures.append(name)
            continue
        if head == merge_content or head in allowed[name]:
            continue
        failures.append(name)
    return failures


def _is_strict_ancestor(root: Path, ref: str) -> bool:
    """True only when `ref` resolves to a commit strictly before HEAD."""
    try:
        head = _git(root, "rev-parse", "--verify", "HEAD^{commit}").strip()
        base = _git(root, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}").strip()
    except PortIndexError:
        return False
    if not base or base == head:
        return False
    completed = subprocess.run(
        [
            "git",
            "-C",
            str(root),
            "merge-base",
            "--is-ancestor",
            base.decode("ascii", "replace"),
            head.decode("ascii", "replace"),
        ],
        capture_output=True,
    )
    return completed.returncode == 0


def load_snapshot_from_git(root: Path, ref: str) -> BaseSnapshot | None:
    """Materialise the shards and aggregates at `ref` and read them."""
    resolved = _git(root, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}").strip()
    commit = resolved.decode("ascii", "replace")
    if not commit:
        raise PortIndexError(f"cannot resolve base ref {ref!r}")

    listing = _git(
        root,
        "ls-tree",
        "-r",
        "--name-only",
        "-z",
        commit,
        "--",
        "docs/port/units",
        "docs/port/queue.tsv",
        "docs/port/ledger.tsv",
    )
    names = [name for name in listing.decode("utf-8", "replace").split("\0") if name]
    if not any(name.startswith("docs/port/units/") for name in names):
        return None

    with tempfile.TemporaryDirectory(prefix="p1-011-base-") as temp:
        temp_root = Path(temp)
        for name in names:
            blob = _git(root, "cat-file", "blob", f"{commit}:{name}")
            destination = temp_root / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(blob)
        return snapshot_from_dir(
            temp_root / "docs" / "port" / "units",
            temp_root / "docs" / "port" / "queue.tsv",
            temp_root / "docs" / "port" / "ledger.tsv",
        )


def _ledger_content(row: tuple[str, ...]) -> tuple[object, ...]:
    """A ledger row's content with its timestamp compared as an instant.

    Ordering is derived, so this key deliberately excludes position. Two
    timestamps naming the same instant are the same content, matching the
    duplicate identity used when shards are parsed.
    """
    return (_timestamp_or_none(row[0]), *row[1:])


def _validate_unit_ledger(
    unit_id: str,
    base_rows: tuple[tuple[str, ...], ...],
    head_rows: list[tuple[str, ...]],
    corrections: list[LedgerCorrection],
) -> list[str]:
    """Require base row content to survive as a multiset, in any order.

    Every base row must still exist at head with identical field values. A row
    may only change its timestamp, and only when a new correction names it; a
    correction is a move, so the old content may not survive alongside the new.
    Any other head row that matches a base row on every field except its
    timestamp is an unauthorized edit. The head may otherwise grow freely,
    including with rows that sort before existing history, because the generated
    aggregate order belongs to the parent job.
    """
    errors: list[str] = []
    replacements: dict[int, LedgerCorrection] = {}
    for correction in corrections:
        previous = _timestamp(correction.previous)
        matches = [
            position
            for position, row in enumerate(base_rows)
            if _timestamp(row[0]) == previous
        ]
        if len(matches) != 1:
            errors.append(
                f"ledger correction for {unit_id!r} at {correction.previous}: no "
                "unique base ledger row carries that timestamp"
            )
            continue
        position = matches[0]
        if position in replacements:
            errors.append(
                f"ledger correction for {unit_id!r} at {correction.previous}: "
                "another correction in this change already targets that base row"
            )
            continue
        replacements[position] = correction

    expected_rows = [
        (replacements[position].corrected, *row[1:])
        if position in replacements
        else row
        for position, row in enumerate(base_rows)
    ]
    unmatched = Counter(_ledger_content(row) for row in head_rows)

    for position, expected in enumerate(expected_rows):
        key = _ledger_content(expected)
        if unmatched[key] > 0:
            unmatched[key] -= 1
            continue

        base_row = base_rows[position]
        correction = replacements.get(position)
        if correction is not None:
            corrected_instant = _timestamp(correction.corrected)
            altered = next(
                (
                    row
                    for row in head_rows
                    if _timestamp_or_none(row[0]) == corrected_instant
                    and tuple(row[1:]) != tuple(base_row[1:])
                ),
                None,
            )
            if any(
                _ledger_content(row) == _ledger_content(base_row) for row in head_rows
            ):
                errors.append(
                    f"ledger correction for {unit_id!r} at {correction.previous}: "
                    "recorded but not applied; the base row still carries its "
                    "previous timestamp"
                )
            elif altered is not None:
                errors.append(
                    f"ledger correction for {unit_id!r} at {correction.previous}: "
                    "only the timestamp may change, but another field differs"
                )
            else:
                errors.append(
                    f"ledger shard {unit_id!r}: base row {base_row!r} is missing or "
                    "altered at head; ledger history is append-only in content"
                )
            continue

        if not corrections:
            instant_edited = next(
                (
                    row
                    for row in head_rows
                    if tuple(row[1:]) == tuple(base_row[1:])
                    and _timestamp_or_none(row[0]) != _timestamp_or_none(base_row[0])
                ),
                None,
            )
            if instant_edited is not None:
                errors.append(
                    f"ledger shard {unit_id!r}: base row timestamp "
                    f"{base_row[0]} was changed without a matching record "
                    f"in docs/port/units/{CORRECTIONS_NAME}; a timestamp edit "
                    "requires a documented correction"
                )
                continue

        errors.append(
            f"ledger shard {unit_id!r}: base row {base_row!r} is missing or altered "
            "at head; ledger history is append-only in content"
        )

    # A correction moves one row: the previous content must not survive next to
    # the corrected copy, or the writer has appended an edit instead of replacing.
    head_contents = Counter(_ledger_content(row) for row in head_rows)
    for position, correction in replacements.items():
        base_row = base_rows[position]
        corrected_key = _ledger_content((correction.corrected, *base_row[1:]))
        if head_contents[_ledger_content(base_row)] and head_contents[corrected_key]:
            errors.append(
                f"ledger correction for {unit_id!r} at {correction.previous}: the "
                "previous row is still present at head alongside the corrected copy; "
                "a correction moves a row, it does not append one"
            )

    # Any leftover head row that matches a base row on every field except its
    # timestamp is a timestamp edit wearing a new row's clothes. It is allowed
    # only when a correction names exactly that unit and previous/corrected pair.
    leftover = Counter(unmatched)
    for row in head_rows:
        key = _ledger_content(row)
        if leftover[key] <= 0:
            continue
        leftover[key] -= 1
        base_match = next(
            (
                base_row
                for base_row in base_rows
                if tuple(base_row[1:]) == tuple(row[1:])
                and _timestamp_or_none(base_row[0]) != _timestamp_or_none(row[0])
            ),
            None,
        )
        if base_match is None:
            continue
        if any(
            _timestamp(correction.previous) == _timestamp(base_match[0])
            and _timestamp(correction.corrected) == _timestamp(row[0])
            for correction in corrections
        ):
            continue
        errors.append(
            f"ledger shard {unit_id!r}: head row {row!r} matches base row "
            f"{base_match!r} on every field except its timestamp; a timestamp edit "
            f"requires a matching record in docs/port/units/{CORRECTIONS_NAME}"
        )

    return errors


def validate_history(index: ShardIndex, base: BaseSnapshot) -> list[str]:
    """Reject silent loss or rewriting of units and ledger history."""
    errors: list[str] = []
    head_queue_ids = set(index.queue)
    head_ledger_ids = set(index.ledger)
    base_queue_ids = set(base.queue_shards)
    base_ledger_ids = set(base.ledger_shards)
    newly_retired = set(index.removed) - set(base.removed)

    # Retirement records are append-only too: a recorded retirement may not vanish.
    for unit_id in sorted(set(base.removed) - set(index.removed)):
        errors.append(
            f"docs/port/units/{REMOVED_NAME}: retirement record for {unit_id!r} was "
            "deleted; retirement records are permanent"
        )
    # A retirement record is immutable in content, not just in its unit key.
    for unit_id in sorted(set(base.removed) & set(index.removed)):
        if index.removed[unit_id] != base.removed[unit_id]:
            errors.append(
                f"docs/port/units/{REMOVED_NAME}: retirement record for {unit_id!r} was "
                "edited; retirement records are permanent (reason and approved-by are "
                "immutable)"
            )

    # A retirement must name a unit that existed at the base and is gone at head.
    for unit_id in sorted(newly_retired):
        if unit_id not in base_queue_ids and unit_id not in base_ledger_ids:
            errors.append(
                f"docs/port/units/{REMOVED_NAME}: {unit_id!r} records a retirement but "
                "no shard for it exists at the base ref"
            )
    for unit_id in sorted(index.removed):
        if unit_id in head_queue_ids or unit_id in head_ledger_ids:
            errors.append(
                f"docs/port/units/{REMOVED_NAME}: {unit_id!r} is recorded as retired but "
                "its shard still exists at head"
            )

    # Every base queue shard must survive, unless deliberately retired in this change.
    for unit_id in sorted(base_queue_ids - head_queue_ids):
        if unit_id not in newly_retired:
            errors.append(
                f"queue shard {unit_id!r} present at the base ref is missing at head; "
                f"restore it or record its retirement in docs/port/units/{REMOVED_NAME}"
            )

    # Every base queue aggregate row must survive (or be deliberately retired).
    base_aggregate_ids = {row[0] for row in base.queue_aggregate if row}
    for unit_id in sorted(base_aggregate_ids - head_queue_ids):
        if unit_id not in newly_retired:
            errors.append(
                f"queue aggregate row for {unit_id!r} present at the base ref is "
                "missing at head; the unit was not retired"
            )

    # A shard moved from one unit id to another is a rename, not a new unit.
    new_ids = head_queue_ids - base_queue_ids
    for old_id in sorted(base_queue_ids - head_queue_ids):
        old_fields = base.queue_shards[old_id]
        for new_id in sorted(new_ids):
            new_fields = index.queue[new_id].fields
            if tuple(new_fields[1:]) == tuple(old_fields[1:]):
                errors.append(
                    f"unit id {old_id!r} was renamed to {new_id!r}; unit ids are stable "
                    "identities, so retire the old unit and add a genuinely new one"
                )

    # Correction records are append-only too, and only a record new in this change
    # may authorize a timestamp edit.
    head_corrections = tuple(correction.fields for correction in index.corrections)
    base_corrections = tuple(base.corrections)
    new_corrections: list[LedgerCorrection] = []
    if head_corrections[: len(base_corrections)] != base_corrections:
        errors.append(
            f"docs/port/units/{CORRECTIONS_NAME}: correction records are append-only; "
            "existing records may not be deleted, edited, or reordered"
        )
    else:
        new_corrections = list(index.corrections[len(base_corrections):])

    corrections_by_unit: dict[str, list[LedgerCorrection]] = {}
    for correction in new_corrections:
        corrections_by_unit.setdefault(correction.unit, []).append(correction)
        # A new correction must be recorded while its unit is live. A historical
        # record is untouched by a later retirement, but a record added in the
        # same change that retires the unit is a dead record for a deleted row.
        if correction.unit in newly_retired:
            errors.append(
                f"ledger correction for {correction.unit!r} at {correction.previous}: "
                "the unit is retired in this change; record a correction only while "
                "the unit is live"
            )
        elif correction.unit not in base_queue_ids:
            errors.append(
                f"ledger correction for {correction.unit!r} at {correction.previous}: "
                "no queue shard for that unit exists at the base ref (dead "
                "correction record)"
            )
        if correction.unit not in base_ledger_ids:
            errors.append(
                f"ledger correction for {correction.unit!r} at {correction.previous}: "
                "no ledger shard for that unit exists at the base ref"
            )
        elif correction.unit not in head_ledger_ids:
            errors.append(
                f"ledger correction for {correction.unit!r} at {correction.previous}: "
                "the unit has no ledger shard at head"
            )
    # Ledger history is append-only in content: every base row must still exist at
    # head, per unit, with identical field values. Order is generated, so a new
    # row may sort anywhere, including before rows that are already recorded.
    for unit_id in sorted(base_ledger_ids):
        if unit_id in newly_retired:
            continue
        if unit_id not in head_ledger_ids:
            errors.append(
                f"ledger shard {unit_id!r} present at the base ref is missing at head; "
                f"restore it or retire the unit in docs/port/units/{REMOVED_NAME}"
            )
            continue
        errors.extend(
            _validate_unit_ledger(
                unit_id,
                base.ledger_shards[unit_id],
                [row.fields for row in index.ledger[unit_id]],
                corrections_by_unit.get(unit_id, []),
            )
        )

    return errors


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
    root: Path = ROOT,
    base_loader=None,
    ancestor_guard=None,
) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("build", "check"))
    parser.add_argument(
        "--against",
        metavar="REF",
        default=None,
        help=(
            "strict ancestor commit whose shards and ledger rows must survive "
            "(check only)"
        ),
    )
    parser.add_argument(
        "--allow-pre-migration-base",
        action="store_true",
        help=(
            "acknowledge a --against base that predates docs/port/units "
            "(migration PR only; prints a skip notice)"
        ),
    )
    parser.add_argument(
        "--shards-only",
        action="store_true",
        help=(
            "validate the shard tree and history without requiring the committed "
            "aggregates to equal freshly generated output (the pull request gate)"
        ),
    )
    parser.add_argument(
        "--forbid-aggregate-changes",
        action="store_true",
        help=(
            "fail when the change against --against edits docs/port/queue.tsv or "
            "docs/port/ledger.tsv (the pull request ownership gate)"
        ),
    )
    parser.add_argument(
        "--recorded-base",
        metavar="REF",
        default=None,
        help=(
            "recorded event base whose aggregate content is the other legitimate "
            "base side for --forbid-aggregate-changes"
        ),
    )
    args = parser.parse_args(argv)

    try:
        path_errors = document_path_errors(root)
        if path_errors:
            raise PortIndexError("\n".join(path_errors))
        index = build_index(units)
        queue_bytes, ledger_bytes = render_index(index)
    except PortIndexError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    if args.mode == "build":
        queue.write_bytes(queue_bytes)
        ledger.write_bytes(ledger_bytes)
        print(f"wrote {queue} and {ledger}")
        return 0

    if args.forbid_aggregate_changes:
        if not args.against:
            print(
                "error: --forbid-aggregate-changes requires --against",
                file=sys.stderr,
            )
            return 1
        recorded_base = (args.recorded_base
                         or os.environ.get("RECORDED_BASE") or None)
        try:
            changed = changed_aggregates(root, args.against, queue, ledger,
                                         recorded_base)
        except PortIndexError as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 1
        if changed:
            print(
                "error: pull requests must not modify the generated aggregates. "
                "Edit only your unit shard under docs/port/units/ and leave "
                "docs/port/queue.tsv and docs/port/ledger.tsv to the post-merge "
                "port-index workflow; drop those file changes from this PR.",
                file=sys.stderr,
            )
            for name in changed:
                print(f"  changed: {name}", file=sys.stderr)
            return 1

    if not args.shards_only:
        queue_ok = _check_file(queue, queue_bytes)
        ledger_ok = _check_file(ledger, ledger_bytes)
        if not (queue_ok and ledger_ok):
            return 1

    ref = args.against or os.environ.get("PORT_INDEX_BASE_REF", "").strip()
    if args.allow_pre_migration_base and not ref:
        print(
            "error: --allow-pre-migration-base requires --against",
            file=sys.stderr,
        )
        return 1
    if ref:
        guard = ancestor_guard or (lambda target: _is_strict_ancestor(root, target))
        if not guard(ref):
            print(
                f"error: --against {ref!r} is not a strict ancestor of HEAD; history "
                "may only be compared with an ancestor commit, not HEAD, a descendant, "
                "or an unrelated ref",
                file=sys.stderr,
            )
            return 1
        loader = base_loader or (lambda target: load_snapshot_from_git(root, target))
        try:
            base = loader(ref)
        except PortIndexError as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 1
        if base is None:
            if not args.allow_pre_migration_base:
                print(
                    f"error: base ref {ref!r} has no docs/port/units, so history cannot "
                    "be checked. This is only expected for the migration PR that first "
                    "adds the shard directory; pass --allow-pre-migration-base to "
                    "acknowledge it deliberately.",
                    file=sys.stderr,
                )
                return 1
            print(
                f"notice: base ref {ref!r} predates docs/port/units; history check "
                "skipped for the migration PR (--allow-pre-migration-base)"
            )
        else:
            history_errors = validate_history(index, base)
            if history_errors:
                for error in history_errors:
                    print(f"error: {error}", file=sys.stderr)
                return 1
            print(f"port index history matches {ref}")

    # Make every accepted correction visible in the check output so a reviewer
    # sees exactly which record authorized a history rewrite.
    for correction in index.corrections:
        print(
            f"notice: ledger correction {correction.unit}: {correction.previous} -> "
            f"{correction.corrected} by {correction.fields[3]} "
            f"({correction.fields[4]})"
        )

    if args.shards_only:
        print("port index shards are valid; aggregates are generated on main")
    else:
        print("port index is current")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
