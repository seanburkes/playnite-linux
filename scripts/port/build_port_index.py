#!/usr/bin/env python3
"""Build or check the generated port queue and ledger from per-unit shards.

Usage:
    python3 scripts/port/build_port_index.py build
    python3 scripts/port/build_port_index.py check [--against <ref>]
    python3 scripts/port/build_port_index.py check --shards-only [--against <ref>]
    python3 scripts/port/build_port_index.py check --shards-only --against <ref> \
        --forbid-aggregate-changes

`check --against <ref>` additionally verifies history: base ledger rows must be
an exact prefix of the head rows, queue shards and ledger rows must survive
unless a unit is explicitly retired in `docs/port/units/removed.tsv`, and <ref>
must be a strict ancestor of HEAD. CI passes the ref with `--against`; the
PORT_INDEX_BASE_REF environment variable is only a local convenience fallback.

A newly appended ledger row may not carry an instant older than the newest row
at the base ref; it must use the current time. `build` enforces the same rule
against the committed aggregate it is about to replace, before writing anything.
The only exception to append-only history is a timestamp correction recorded in
`docs/port/units/ledger-corrections.tsv` in the same change. At most one record
per unit may move one existing base row strictly past the newest pre-existing
row, and it may not change any other field, delete a row, or reorder rows. A
record names a unit that was live when it was recorded, so a later retirement
does not invalidate it; correction records are themselves append-only.

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
import os
import re
import subprocess
import sys
import tempfile
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
    """The ledger rows in generated aggregate order."""
    rows = [row for unit_id in sorted(index.ledger) for row in index.ledger[unit_id]]
    return tuple(sorted(rows, key=lambda row: (_timestamp(row.timestamp), row.unit)))


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


def changed_aggregates(root: Path, ref: str, queue: Path, ledger: Path) -> list[str]:
    """Paths of the generated aggregates changed on this branch since its merge base.

    The comparison is three-dot: a stale branch must not appear to revert aggregate
    changes that landed on main after it forked.
    """
    resolved = _git(root, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}").strip()
    commit = resolved.decode("ascii", "replace")
    if not commit:
        raise PortIndexError(f"cannot resolve base ref {ref!r}")
    merge_base = (
        _git(root, "merge-base", commit, "HEAD").strip().decode("ascii", "replace")
    )
    if not merge_base:
        raise PortIndexError(f"no merge base between {ref!r} and HEAD")
    listing = _git(
        root,
        "diff",
        "--name-only",
        "-z",
        merge_base,
        "HEAD",
        "--",
        _repo_relative(queue, root),
        _repo_relative(ledger, root),
    )
    return [name for name in listing.decode("utf-8", "replace").split("\0") if name]


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


def _is_subsequence(base_rows: tuple[tuple[str, ...], ...], head_rows: list[tuple[str, ...]]) -> bool:
    """True when every base row survives in order (append-only history)."""
    iterator = iter(head_rows)
    for base_row in base_rows:
        for head_row in iterator:
            if head_row == base_row:
                break
        else:
            return False
    return True


def _newest_ledger_instant(snapshot: BaseSnapshot) -> datetime | None:
    """The newest ledger instant present at the base ref, if any."""
    instants: list[datetime] = []
    for rows in snapshot.ledger_shards.values():
        for row in rows:
            try:
                instants.append(_timestamp(row[0]))
            except ValueError:
                continue
    for row in snapshot.ledger_aggregate:
        if len(row) < 2:
            continue
        try:
            instants.append(_timestamp(row[0]))
        except ValueError:
            continue
    return max(instants) if instants else None


def _ledger_sort_key(row: tuple[str, ...]) -> tuple[datetime, str]:
    return (_timestamp(row[0]), row[1] if len(row) > 1 else "")


def _aggregate_is_in_generated_order(rows: list[tuple[str, ...]]) -> bool:
    try:
        keys = [_ledger_sort_key(row) for row in rows]
    except ValueError:
        return False
    return keys == sorted(keys)


def _newest_pre_existing_instant(
    snapshot: BaseSnapshot, unit_id: str, previous: datetime
) -> datetime | None:
    """The newest base instant that is not the row a correction names."""
    instants: list[datetime] = []
    for rows in (*snapshot.ledger_shards.values(), snapshot.ledger_aggregate):
        for row in rows:
            if len(row) < 2:
                continue
            try:
                instant = _timestamp(row[0])
            except ValueError:
                continue
            if row[1] == unit_id and instant == previous:
                continue
            instants.append(instant)
    return max(instants) if instants else None


def _apply_aggregate_corrections(
    rows: list[tuple[str, ...]],
    used_corrections: list[tuple[LedgerCorrection, tuple[str, ...]]],
) -> list[tuple[str, ...]]:
    """Reflect authorized timestamp corrections in the base aggregate rows.

    Only rows that a used correction names are changed, and only their timestamp.
    Every unaffected row keeps its base relative order, and only the corrected row
    is moved to its generated position, so a correction cannot smuggle in a
    reorder of unrelated history. Without corrections the order is untouched.
    """
    if not used_corrections:
        return list(rows)
    corrected = list(rows)
    moved: list[tuple[str, ...]] = []
    moved_positions: set[int] = set()
    for correction, base_row in used_corrections:
        previous = _timestamp(correction.previous)
        for position, row in enumerate(corrected):
            if position in moved_positions:
                continue
            if len(row) < 2 or row[1] != correction.unit:
                continue
            try:
                instant = _timestamp(row[0])
            except ValueError:
                continue
            if instant == previous and tuple(row[2:]) == tuple(base_row[2:]):
                replacement = (correction.corrected, *row[1:])
                corrected[position] = replacement
                moved.append(replacement)
                moved_positions.add(position)
                break
    if not moved_positions:
        return corrected
    remaining = [
        row for position, row in enumerate(corrected) if position not in moved_positions
    ]
    for row in sorted(moved, key=_ledger_sort_key):
        key = _ledger_sort_key(row)
        at = 0
        while at < len(remaining) and _ledger_sort_key(remaining[at]) <= key:
            at += 1
        remaining.insert(at, row)
    return remaining


def validate_build_history(index: ShardIndex, ledger_path: Path) -> list[str]:
    """Reject a backdated ledger row before `build` writes the aggregates.

    `build` has no base commit, so it uses the committed aggregate it is about to
    replace as the pre-existing history. Rows already present there are exempt;
    every new row must sort at or after the newest existing row.
    """
    aggregate = _read_aggregate(ledger_path, LEDGER_HEADER)
    instants: list[datetime] = []
    for row in aggregate:
        if len(row) < 2:
            continue
        instant = _timestamp_or_none(row[0])
        if instant is not None:
            instants.append(instant)
    if not instants:
        return []
    newest = max(instants)
    newest_text = newest.strftime("%Y-%m-%dT%H:%M:%SZ")
    existing = set(aggregate)
    errors: list[str] = []
    for unit_id in sorted(index.ledger):
        for row in index.ledger[unit_id]:
            if row.fields in existing:
                continue
            instant = _timestamp_or_none(row.fields[0])
            if instant is None or instant >= newest:
                continue
            errors.append(
                f"ledger shard {unit_id!r}: row at {row.fields[0]} sorts before the "
                f"newest existing ledger row at {newest_text}; append new ledger "
                "rows using the current time, not a backdated timestamp"
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
        # A correction exists to move a row past history that is already recorded.
        # Moving it later but still ahead of that history leaves the ledger
        # misordered, so require a strictly later instant than the newest other row.
        previous_instant = _timestamp(correction.previous)
        newest_other = _newest_pre_existing_instant(
            base, correction.unit, previous_instant
        )
        if newest_other is not None and _timestamp(correction.corrected) <= newest_other:
            newest_other_text = newest_other.strftime("%Y-%m-%dT%H:%M:%SZ")
            errors.append(
                f"ledger correction for {correction.unit!r} at {correction.previous}: "
                f"corrected timestamp {correction.corrected} must sort strictly after "
                f"the newest pre-existing ledger row at {newest_other_text}; a "
                "correction may not leave the row before existing history"
            )

    newest_base = _newest_ledger_instant(base)
    newest_base_text = (
        newest_base.strftime("%Y-%m-%dT%H:%M:%SZ") if newest_base is not None else ""
    )

    def backdated_error(unit_id: str, row: tuple[str, ...]) -> str | None:
        if newest_base is None:
            return None
        instant = _timestamp_or_none(row[0])
        if instant is None or instant >= newest_base:
            return None
        return (
            f"ledger shard {unit_id!r}: new row at {row[0]} sorts before the newest "
            f"existing ledger row at {newest_base_text}; append new ledger rows using "
            "the current time, not a backdated timestamp"
        )

    # A brand-new ledger shard may only append rows at or after existing history.
    for unit_id in sorted(head_ledger_ids - base_ledger_ids):
        for row in index.ledger[unit_id]:
            message = backdated_error(unit_id, row.fields)
            if message:
                errors.append(message)

    used_corrections: list[tuple[LedgerCorrection, tuple[str, ...]]] = []

    # Ledger shards are append-only: base rows must remain an exact prefix, except
    # for a timestamp that a matching, newly recorded correction authorizes.
    for unit_id in sorted(base_ledger_ids):
        if unit_id in newly_retired:
            continue
        if unit_id not in head_ledger_ids:
            errors.append(
                f"ledger shard {unit_id!r} present at the base ref is missing at head; "
                f"restore it or retire the unit in docs/port/units/{REMOVED_NAME}"
            )
            continue
        base_rows = base.ledger_shards[unit_id]
        head_rows = [row.fields for row in index.ledger[unit_id]]
        replacements: dict[int, LedgerCorrection] = {}
        for correction in corrections_by_unit.get(unit_id, []):
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

        expected = [
            (replacements[position].corrected, *row[1:])
            if position in replacements
            else row
            for position, row in enumerate(base_rows)
        ]
        prefix_ok = tuple(head_rows[: len(base_rows)]) == tuple(expected)
        if prefix_ok:
            for position, correction in replacements.items():
                used_corrections.append((correction, base_rows[position]))
        else:
            for position, correction in replacements.items():
                base_row = base_rows[position]
                corrected_instant = _timestamp(correction.corrected)
                candidate = next(
                    (
                        index_
                        for index_, row in enumerate(head_rows)
                        if _timestamp_or_none(row[0]) == corrected_instant
                    ),
                    None,
                )
                if candidate is None:
                    if (
                        position < len(head_rows)
                        and _timestamp_or_none(head_rows[position][0])
                        == _timestamp(base_row[0])
                    ):
                        errors.append(
                            f"ledger correction for {unit_id!r} at "
                            f"{correction.previous}: recorded but not applied; the base "
                            "row still carries its previous timestamp"
                        )
                elif tuple(head_rows[candidate][1:]) != tuple(base_row[1:]):
                    errors.append(
                        f"ledger correction for {unit_id!r} at {correction.previous}: "
                        "only the timestamp may change, but another field differs"
                    )
                elif candidate != position:
                    errors.append(
                        f"ledger correction for {unit_id!r} at {correction.previous}: "
                        "the corrected row must keep its base position"
                    )

            if not replacements:
                timestamp_changed = next(
                    (
                        base_row
                        for position, base_row in enumerate(base_rows)
                        if position < len(head_rows)
                        and head_rows[position] != base_row
                        and tuple(head_rows[position][1:]) == tuple(base_row[1:])
                        and _timestamp_or_none(head_rows[position][0])
                        != _timestamp_or_none(base_row[0])
                    ),
                    None,
                )
                if timestamp_changed is not None:
                    errors.append(
                        f"ledger shard {unit_id!r}: base row timestamp "
                        f"{timestamp_changed[0]} was changed without a matching record "
                        f"in docs/port/units/{CORRECTIONS_NAME}; a timestamp edit "
                        "requires a documented correction"
                    )

            if _is_subsequence(base_rows, head_rows):
                detail = (
                    "new rows were inserted before or between existing rows; append new "
                    "rows at the end of the shard"
                )
            else:
                missing = next((row for row in base_rows if row not in head_rows), None)
                detail = (
                    "existing rows were reordered"
                    if missing is None
                    else f"row {missing!r} is missing or altered"
                )
            errors.append(
                f"ledger shard {unit_id!r}: existing rows are append-only, but {detail}"
            )

        if prefix_ok and len(head_rows) > len(base_rows):
            for row in head_rows[len(base_rows):]:
                message = backdated_error(unit_id, row)
                if message:
                    errors.append(message)

    # Base ledger aggregate rows must remain an exact prefix of the generated ledger.
    # Only timestamp corrections accepted above may alter a base aggregate row.
    head_aggregate = [row.fields for row in _ordered_ledger_rows(index)]
    base_aggregate = [
        row
        for row in base.ledger_aggregate
        if len(row) >= 2 and row[1] not in newly_retired
    ]
    # A correction may move only its own row. If the base aggregate is already out
    # of generated order, applying a correction must not silently sort it: that
    # would launder a pre-existing reorder of unrelated rows.
    if used_corrections and not _aggregate_is_in_generated_order(base_aggregate):
        errors.append(
            "ledger aggregate: the base ledger aggregate is not in generated order; "
            "a correction may not be used to reorder unrelated rows"
        )
    base_aggregate = _apply_aggregate_corrections(base_aggregate, used_corrections)
    for position, expected in enumerate(base_aggregate):
        actual = head_aggregate[position] if position < len(head_aggregate) else None
        if actual == expected:
            continue
        errors.append(
            f"ledger aggregate: base row {expected!r} is no longer at position "
            f"{position + 2}; found {actual!r}. New rows may only append after existing "
            "ledger history; a backdated row may not sort ahead of it"
        )
        break

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
    args = parser.parse_args(argv)

    try:
        index = build_index(units)
        queue_bytes, ledger_bytes = render_index(index)
    except PortIndexError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    if args.mode == "build":
        try:
            build_errors = validate_build_history(index, ledger)
        except PortIndexError as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 1
        if build_errors:
            for error in build_errors:
                print(f"error: {error}", file=sys.stderr)
            print(
                "error: refusing to write a ledger with a backdated row; the "
                "aggregates were left unchanged",
                file=sys.stderr,
            )
            return 1
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
        try:
            changed = changed_aggregates(root, args.against, queue, ledger)
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
