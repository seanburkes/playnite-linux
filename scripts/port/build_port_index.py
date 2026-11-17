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
fail validation. `build` sorts legacy/history rows by instant then unit and appends
per-PR ledger fragments in numeric-PR then filename order; that ordering is produced
by the parent-owned regeneration on main, and contributors never hand-order or
hand-edit an aggregate.

Ledger evidence lives in per-PR fragments,
`docs/port/units/<unit>.ledger.d/<pr>-<short-slug>.tsv`. A legacy
`<unit>.ledger.tsv` remains readable and frozen, and may be migrated verbatim to
`<unit>.ledger.d/0000-history.tsv`. Fragments are immutable after merge, so a pull
request never appends to a file another PR also writes.

A timestamp correction recorded in `docs/port/units/ledger-corrections.tsv` in
the same change may change only the timestamp of one existing base row, and only
to a later instant. At most one record per unit may do so. A record names a unit
that was live when it was recorded, so a later retirement does not invalidate it;
correction records are themselves append-only.

Pull requests validate the shard tree only. `--shards-only` skips the byte
comparison against the committed aggregates, which are generated on main by the
post-merge `port-index` workflow. `--forbid-aggregate-changes` fails when the
branch changed `docs/port/queue.tsv`, `docs/port/ledger.tsv`, `tests/baseline.json`
or generated `PORT-STATUS.md` content since its merge base with `--against`, so a
pull request owns only its unit shards and a stale branch does not appear to revert
aggregates that landed on main. Baseline and status are compared with the recorded
base (the current mainline) rather than merely with the fork point, so a stale branch
cannot restore an older floor or an older generated table. The same gate checks
fragment ownership: one unit namespace and at most one new immutable fragment named
for this PR.

`--forbid-shard-ownership` runs the same ownership check without requiring the
aggregate-ownership paths. It is the form the post-merge `port-index` writer uses
before it commits; `--forbid-aggregate-changes` implies it for pull requests. A
documented one-time migration of a legacy `<unit>.ledger.tsv` to
`<unit>.ledger.d/0000-history.tsv` is allowed only when the legacy file is deleted in
the same change and the history bytes are its verbatim content. Retirement deletes
are likewise allowed only when `removed.tsv` records the unit.

`build` also regenerates `docs/port/PORT-STATUS.md` from the shards, preserving only
the prose between the narrative markers, and, when `--capture-results` and
`--capture-run` point at a completed merged-main `linux-ci` run's artifacts, ratchets
`tests/baseline.json`. The baseline writer derives a floor from every structured
capture in the document and from the previous main baseline, refuses to lower any
recorded floor, refuses to delete or rewrite a `main_captures` identity, records every
new run's provenance even when its counts are unchanged, and preserves every earlier
capture. The narrative must begin with a hand-written heading and may not contain a
generated-table marker.

The migration PR that first adds `docs/port/units` is the one legitimate
pre-migration base: `--allow-pre-migration-base` acknowledges the history skip
and prints a notice instead of passing silently.

Exit codes: 0 success, 1 validation error or drift.
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import importlib.util
import json
from functools import lru_cache
import os
import re
import subprocess
import sys
import tempfile
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
UNITS = ROOT / "docs" / "port" / "units"
STATUS_GENERATED_PATH = "docs/port/PORT-STATUS.md"
BASELINE_GENERATED_PATH = "tests/baseline.json"
NARRATIVE_START = "<!-- port-status:narrative:start -->"
NARRATIVE_END = "<!-- port-status:narrative:end -->"
NARRATIVE_HEADING = "## Hand-written narrative"
GENERATED_STATUS_BANNER = (
    "<!-- Generated by scripts/port/build_port_index.py; edit unit shards only. -->"
)
# Lines the generator owns. A narrative that repeats one of these can show a
# reader a fabricated generated table, so it is refused rather than preserved.
FORBIDDEN_NARRATIVE_LINES = frozenset(
    {"# Port status", "## Unit summary", "## Ledger", "## Units", GENERATED_STATUS_BANNER}
)

FRAGMENT_SUFFIX = ".ledger.d"
FRAGMENT_NAME = re.compile(r"([1-9][0-9]*)-([a-z0-9]+(?:-[a-z0-9]+)*)\.tsv")
# One-time migration target, if a unit chooses to move its legacy ledger shard
# into the fragment directory. It holds the old rows verbatim and is ordered
# with history, before every per-PR fragment.
HISTORY_NAME = "0000-history.tsv"

# Task directories, not standard system roots. Changes are protected policy changes.
LOCAL_TASK_ROOTS = ("/tmp/opencode", "/srv/firstmate", "/var/lib/firstmate")
HOME_PREFIX = r"(?:\$(?:HOME\b|\{HOME\})|~)[\\/]"
LOCAL_PATH = re.compile(
    r"(?<![\w])/(?:home|Users|root)/[^\s/]+|"
    r"(?<![\w])(?:" + "|".join(map(re.escape, LOCAL_TASK_ROOTS)) + r")(?:/|\b)|"
    r"(?<![\w])" + HOME_PREFIX + r"[^\s/]+|"
    r"(?<![\w./-])~[\w.-]+|%USERPROFILE%|\$env:USERPROFILE\b|"
    r"(?<![\w])[A-Za-z]:[\\/](?:Users|Home|Documents and Settings)(?:[\\/]|\b)|"
    r"(?<![\w./-])/?firstmate/(?:data|state)(?:/|\b)|"
    r"(?<![\w.-])\.treehouse/",
    re.IGNORECASE,
)
REMOTE_URL = re.compile(
    r"\b(?:https?|ftp|ssh|git|sftp)://[^\s<>()\[\]{}|,;`\"']+|"
    r"(?<![\w/\\])(?://)?(?:[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\.[a-z]{2,})"
    r"(?::\d+)?/[^\s<>()\[\]{}|,;`\"']+",
    re.IGNORECASE,
)
UNC_PATH = re.compile(r"(?<![\w])(?:\\\\|//)[^\\/\s]+[\\/]+[^\\/\s]+")
GENERIC_HOME_PATH = re.compile(
    r"(?<![\w])" + HOME_PREFIX
    + r"(?:\.steam|\.local/share/Steam|\.local|\.config|config)(?![\w/\\.-])",
    re.IGNORECASE,
)


def contains_local_path(line: str) -> bool:
    # Strip remote targets before decoding, so encoded punctuation in a URL cannot
    # turn its path into apparent local evidence. Delimiters end the remote target.
    text = REMOTE_URL.sub("", line)
    while True:
        decoded = unquote(text)
        if decoded == text:
            break
        text = REMOTE_URL.sub("", decoded)
    if UNC_PATH.search(text):
        return True
    # JSON/Markdown escaped and mixed Windows separators denote the same path.
    text = re.sub(r"[\\/]+", "/", text)
    # Exempt only exact generic tokens, never a whole Markdown label/link/line.
    return bool(LOCAL_PATH.search(GENERIC_HOME_PATH.sub("", text)))
# Fixed, not the event base or HEAD; see docs/port/LOCAL-PATH-EXCEPTIONS.md.
PRE_GUARD_COMMIT = "0b02bf9ff395a91b3524545074f53abe403effc2"


@lru_cache(maxsize=None)
def local_path_allowance(label: str) -> Counter:
    # Fail closed if the pinned baseline is unavailable (requires full history).
    _git(ROOT, "cat-file", "-e", f"{PRE_GUARD_COMMIT}^{{commit}}")
    blob = _git_bytes(ROOT, "show", f"{PRE_GUARD_COMMIT}:{label}")
    if blob is None:
        return Counter()
    return Counter(hashlib.sha256(line.encode("utf-8")).hexdigest()
                   for line in blob.decode("utf-8").splitlines()
                   if contains_local_path(line))


def local_path_errors(text: str, label: str) -> list[str]:
    """Reject paths not present in the exact file/line multiset at the fixed baseline."""
    allowed = None
    errors = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        if not contains_local_path(line):
            continue
        if allowed is None:
            allowed = local_path_allowance(label).copy()
        digest = hashlib.sha256(line.encode("utf-8")).hexdigest()
        if allowed[digest] > 0:
            allowed[digest] -= 1
        else:
            errors.append(f"{label}:{lineno}: personal or task-local path in row; "
                          "use a repository-relative reference")
    return errors


def document_path_errors(root: Path) -> list[str]:
    """Scan documentation, including staged/new files, without inspecting oracle data."""
    paths = list((root / "docs").rglob("*"))
    paths.extend(path for path in root.iterdir() if path.suffix.lower() == ".md")
    for name in ("spikes", "tests", "tools", "packaging", "src"):
        paths.extend(path for path in (root / name).rglob("*")
                     if path.suffix.lower() == ".md")
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
    f"<unit-id>{QUEUE_SUFFIX}, <unit-id>{LEDGER_SUFFIX} and "
    f"<unit-id>{FRAGMENT_SUFFIX}/<pr>-<short-slug>.tsv"
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
    fragments: tuple[LedgerRow, ...] = ()


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
        if name.endswith(FRAGMENT_SUFFIX):
            unit = name[:-len(FRAGMENT_SUFFIX)]
            if not _UNIT_ID.fullmatch(unit) or not entry.is_dir() or entry.is_symlink():
                errors.append(f"{entry}: invalid ledger fragment directory")
                continue
            if not (units / f"{unit}{QUEUE_SUFFIX}").is_file():
                errors.append(f"{entry}: fragment namespace has no queue shard for {unit!r}")
            for fragment in sorted(entry.iterdir()):
                if (not (FRAGMENT_NAME.fullmatch(fragment.name)
                         or fragment.name == HISTORY_NAME)
                        or not fragment.is_file() or fragment.is_symlink()):
                    errors.append(
                        f"{fragment}: expected <numeric-pr>-<short-slug>.tsv "
                        f"or {HISTORY_NAME}"
                    )
                else:
                    ledger_paths.append(fragment)
            continue
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
) -> tuple[dict[str, tuple[LedgerRow, ...]], list[str], tuple[LedgerRow, ...]]:
    """Parse legacy shards, migrated history, then per-PR fragments.

    Fragments are returned separately, in numeric-PR then filename order, so the
    aggregate can place history first and every fragment after it. The per-unit
    mapping keeps the same rows for the history-preservation check.
    """
    errors: list[str] = []
    rows: dict[str, tuple[LedgerRow, ...]] = {}
    fragments: list[LedgerRow] = []
    seen_rows: dict[tuple[object, ...], str] = {}

    for path in sorted(paths, key=ledger_path_key):
        unit_id = ledger_path_unit(path)
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
            row = LedgerRow(tuple(fields))
            shard_rows.append(row)
            if _is_fragment(path):
                fragments.append(row)
        rows[unit_id] = rows.get(unit_id, ()) + tuple(shard_rows)

    return rows, errors, tuple(fragments)


def _is_fragment(path: Path) -> bool:
    return path.parent.name.endswith(FRAGMENT_SUFFIX) and path.name != HISTORY_NAME


def ledger_path_unit(path: Path) -> str:
    return (path.parent.name[:-len(FRAGMENT_SUFFIX)]
            if path.parent.name.endswith(FRAGMENT_SUFFIX)
            else path.name[:-len(LEDGER_SUFFIX)])


def ledger_path_key(path: Path) -> tuple:
    match = FRAGMENT_NAME.fullmatch(path.name)
    return ((1, int(match[1]), path.name, ledger_path_unit(path)) if match
            else (0, 0, path.name, ledger_path_unit(path)))


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

    ledger_rows, ledger_errors, fragments = _collect_ledger(ledger_paths, set(queue_rows))
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
    return ShardIndex(queue_rows, ledger_rows, removed, corrections, fragments)


def _ordered_ledger_rows(index: ShardIndex) -> tuple[LedgerRow, ...]:
    """The ledger rows in generated aggregate order.

    Legacy shards and any migrated history come first, ordered by instant, then
    unit, then the remaining fields. That key is total, so reordering a history
    shard cannot change generated bytes. Per-PR fragments follow history in
    numeric-PR then filename order, regardless of their timestamps, so a new
    fragment always lands after already-generated history.
    """
    fragment_rows = set(index.fragments)
    rows = [row for unit_id in sorted(index.ledger) for row in index.ledger[unit_id]
            if row not in fragment_rows]
    return tuple(
        sorted(
            rows,
            key=lambda row: (_timestamp(row.timestamp), row.unit, row.fields[2:]),
        )
    ) + index.fragments


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
    _, ledger_paths, _, _, discovery_errors = _discover(units)
    collected, ledger_errors, _ = _collect_ledger(ledger_paths, set(queue_shards))
    if discovery_errors or ledger_errors:
        raise PortIndexError("\n".join(discovery_errors + ledger_errors))
    ledger_shards = {unit: tuple(row.fields for row in rows)
                     for unit, rows in collected.items()}

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


def _without_notes(value):
    """Copy a baseline document with human notes removed for comparison."""
    if isinstance(value, dict):
        return {key: _without_notes(item) for key, item in value.items()
                if key != "notes"}
    if isinstance(value, list):
        return [_without_notes(item) for item in value]
    return value


def _top_level_captures(document: dict) -> dict[str, object]:
    """Capture-like top-level objects with human notes removed.

    These are the historical provenance records such as ``p4_exit_capture``
    and ``new_capture``. They are not interchangeable with ``main_captures``.
    """
    markers = {"run_id", "run_url", "commit", "captured_at", "counts"}
    return {
        key: _without_notes(value)
        for key, value in document.items()
        if key != "new" and isinstance(value, dict) and markers & set(value)
    }


def _count_row_errors(project: str, row, label: str) -> list[str]:
    errors: list[str] = []
    if not isinstance(project, str) or not project.strip():
        return [f"{label}: invalid project name"]
    if not isinstance(row, dict):
        return [f"{label}: invalid count row for {project}"]
    for field in ("total", "executed", "passed", "failed", "skipped"):
        if field in row and (type(row[field]) is not int or row[field] < 0):
            errors.append(f"{label}: {project} has an invalid {field} count")
    if "executed" not in row:
        errors.append(f"{label}: {project} is missing its executed floor")
    if ("passed" in row and "executed" in row
            and type(row["passed"]) is int and type(row["executed"]) is int
            and row["passed"] > row["executed"]):
        errors.append(f"{label}: {project} passed floor exceeds its executed floor")
    return errors


def baseline_capture_floors(document: dict) -> tuple[dict[str, dict[str, int]], list[str]]:
    """Return the maximum count per project over every structured capture.

    Structured captures are the current ``new`` floor, every top-level record
    with a ``counts`` object, and every ``main_captures`` entry. Human notes are
    not evidence and are ignored, so a note edit cannot lower or raise a floor.
    """
    floors: dict[str, dict[str, int]] = {}
    errors: list[str] = []

    def merge(project: str, row: dict, label: str) -> None:
        for field in ("total", "executed", "passed"):
            value = row.get(field)
            if type(value) is not int or value < 0:
                continue
            current = floors.setdefault(project, {})
            if field not in current or value > current[field]:
                current[field] = value

    new = document.get("new")
    if new is not None:
        if not isinstance(new, dict) or not new:
            errors.append("baseline new-suite state must be a non-empty object")
        else:
            for project, row in new.items():
                errors.extend(_count_row_errors(project, row, "new"))
                if isinstance(row, dict):
                    merge(project, row, "new")

    for key in sorted(document):
        if key == "new":
            continue
        value = document[key]
        if isinstance(value, dict) and isinstance(value.get("counts"), dict):
            for project, row in value["counts"].items():
                errors.extend(_count_row_errors(project, row, f"{key}.counts"))
                if isinstance(row, dict):
                    merge(project, row, f"{key}.counts")

    captures = document.get("main_captures")
    if captures is not None:
        if not isinstance(captures, list):
            errors.append("main_captures must be a list")
        else:
            for position, capture in enumerate(captures):
                label = f"main_captures[{position}]"
                if not isinstance(capture, dict):
                    errors.append(f"{label}: must be an object")
                    continue
                counts = capture.get("counts")
                if not isinstance(counts, dict):
                    errors.append(f"{label}: must contain a counts object")
                    continue
                for project, row in counts.items():
                    errors.extend(_count_row_errors(project, row, f"{label}.counts"))
                    if isinstance(row, dict):
                        merge(project, row, f"{label}.counts")
    return floors, errors


def _removal_record_errors(project: str, record, label: str) -> list[str]:
    errors: list[str] = []
    if not isinstance(record, dict):
        return [f"{label}: invalid removal record for {project}"]
    for field in ("reason", "approved_by"):
        value = record.get(field)
        if not isinstance(value, str) or not value.strip():
            errors.append(f"{label}: removal record for {project} needs a non-empty {field}")
    if "replaced_by" in record and (
            not isinstance(record["replaced_by"], str)
            or not record["replaced_by"].strip()):
        errors.append(f"{label}: removal record for {project} has an invalid replaced_by")
    if "replacement_executed" in record and (
            type(record["replacement_executed"]) is not int
            or record["replacement_executed"] < 0):
        errors.append(
            f"{label}: removal record for {project} has an invalid replacement_executed"
        )
    return errors


def baseline_document_errors(document: dict) -> list[str]:
    """Validate the whole floor document, not only its current ``new`` block."""
    if not isinstance(document, dict):
        return ["baseline must be a JSON object"]
    floors, errors = baseline_capture_floors(document)
    removed = document.get("removed_projects", {})
    if not isinstance(removed, dict):
        errors.append("removed_projects must be an object")
        removed = {}
    else:
        for project, record in removed.items():
            errors.extend(_removal_record_errors(project, record, "removed_projects"))

    new = document.get("new")
    if new is None:
        if floors:
            errors.append(
                "baseline new-suite floor is missing but historical captures record "
                "project floors; refusing to fail open"
            )
        return errors
    if not isinstance(new, dict) or not new:
        errors.append("baseline new-suite state must be a non-empty object")
        return errors

    for project in sorted(floors):
        if project in removed:
            if project in new:
                errors.append(
                    f"{project}: removal record conflicts with a current floor entry"
                )
            continue
        row = new.get(project)
        if not isinstance(row, dict):
            errors.append(
                f"{project}: project floor is missing from 'new'; a historical "
                "capture records it, so it cannot disappear without a retirement"
            )
            continue
        for field, maximum in sorted(floors[project].items()):
            value = row.get(field)
            if type(value) is not int:
                errors.append(f"{project}: {field} floor is missing from 'new'")
            elif value < maximum:
                errors.append(
                    f"{project}: {field} floor {value} is below the historical "
                    f"capture maximum {maximum}"
                )

    for project in sorted(removed):
        if project in new:
            errors.append(f"{project}: removal record conflicts with a current floor entry")
        if project not in floors:
            errors.append(
                f"{project}: removal record is not justified by any recorded capture; "
                "a retirement needs real measured evidence"
            )
    return errors


def _capture_identity(capture: dict) -> tuple:
    return tuple(
        capture.get(field) for field in
        ("run_id", "run_url", "commit", "captured_at", "counts")
    )


def baseline_change_errors(head: dict, expected: dict) -> list[str]:
    """Explain why a head baseline differs from the recorded mainline document."""
    details = baseline_document_errors(head)
    expected_floors, _ = (
        baseline_capture_floors(expected) if isinstance(expected, dict) else ({}, [])
    )
    head_new = head.get("new") if isinstance(head, dict) else None
    expected_new = expected.get("new") if isinstance(expected, dict) else None
    head_removed = head.get("removed_projects") if isinstance(head, dict) else None
    expected_removed = expected.get("removed_projects") if isinstance(expected, dict) else None
    if not isinstance(head_removed, dict):
        head_removed = {}
    if not isinstance(expected_removed, dict):
        expected_removed = {}

    for project, row in (expected_new or {}).items():
        current = (head_new or {}).get(project)
        if not isinstance(current, dict):
            details.append(
                f"{project}: recorded project floor is missing; the baseline is bot-owned"
            )
            continue
        for field in ("total", "executed", "passed"):
            value = row.get(field)
            current_value = current.get(field)
            if value is not None and (
                    type(current_value) is not int or current_value < value):
                details.append(
                    f"{project}: {field} floor {current_value} is below the recorded "
                    f"base {value}"
                )
    for project, fields in expected_floors.items():
        if project in head_removed:
            continue
        row = (head_new or {}).get(project) or {}
        for field, maximum in fields.items():
            value = row.get(field)
            if type(value) is not int or value < maximum:
                details.append(
                    f"{project}: {field} floor {value} is below the historical "
                    f"capture maximum {maximum}"
                )

    expected_captures = expected.get("main_captures") if isinstance(expected, dict) else None
    head_captures = head.get("main_captures") if isinstance(head, dict) else None
    expected_captures = expected_captures if isinstance(expected_captures, list) else []
    head_captures = head_captures if isinstance(head_captures, list) else []
    for capture in expected_captures:
        if not isinstance(capture, dict):
            continue
        identity = _capture_identity(capture)
        if not any(isinstance(item, dict) and _capture_identity(item) == identity
                   for item in head_captures):
            details.append(
                f"main_captures entry for run {capture.get('run_id')} was removed or "
                "rewritten; capture history is append-only"
            )

    expected_top_level = _top_level_captures(expected) if isinstance(expected, dict) else {}
    head_top_level = _top_level_captures(head) if isinstance(head, dict) else {}
    for key, value in expected_top_level.items():
        if head_top_level.get(key) != value:
            details.append(
                f"capture record {key} was removed or rewritten; capture history is "
                "append-only"
            )

    for project, record in expected_removed.items():
        if head_removed.get(project) != record:
            details.append(
                f"prior removal record for {project} was deleted or edited; retirement "
                "records are immutable"
            )
    if not details:
        details.append(
            "baseline differs from the recorded base; the baseline is bot-owned"
        )
    return details


def changed_aggregates(root: Path, ref: str, queue: Path, ledger: Path,
                       recorded_base: str | None = None,
                       index: ShardIndex | None = None,
                       allow_baseline_note_edits: bool = False) -> list[str]:
    """Generated files the head may not change, with an exact reason.

    Queue and ledger tolerate a stale branch carrying either the fork-point or
    the recorded event-base content: GitHub's synthetic merge supplies the
    current mainline copy for a clean PR. Baseline and status do not use that
    tolerance. Status must match the recorded base after the hand-written
    narrative is removed, and the baseline must match it apart from notes; a
    change that lowers a floor or removes a capture is reported explicitly.
    Comparing with the recorded base (not the fork point alone) also catches a
    stale branch that silently restores an older generated file, which a
    three-dot diff cannot see.
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
    expected_side = recorded if recorded_base is not None else merge_base
    if index is None:
        index = build_index(queue.parent / "units")

    paths = (_repo_relative(queue, root), _repo_relative(ledger, root),
             BASELINE_GENERATED_PATH, STATUS_GENERATED_PATH)
    listing = _git(
        root, "diff", "--name-only", "-z", merge_base, "HEAD", "--", *paths
    )
    diff_changed = {
        name for name in listing.decode("utf-8", "replace").split("\0") if name
    }
    failures: list[str] = []

    for name in sorted(diff_changed):
        if name in {BASELINE_GENERATED_PATH, STATUS_GENERATED_PATH}:
            continue
        head = _git_bytes(root, "show", f"HEAD:{name}")
        allowed = {
            content for content in (
                _git_bytes(root, "show", f"{merge_base}:{name}"),
                _git_bytes(root, "show", f"{recorded}:{name}"),
            ) if content is not None
        }
        if head is None or head not in allowed:
            failures.append(name)

    head_status = _git_bytes(root, "show", f"HEAD:{STATUS_GENERATED_PATH}")
    expected_status = _git_bytes(root, "show", f"{expected_side}:{STATUS_GENERATED_PATH}")
    if head_status is not None or expected_status is not None:
        if head_status is None or expected_status is None:
            failures.append(
                f"{STATUS_GENERATED_PATH}: generated status is present on only one side"
            )
        else:
            try:
                head_normal = generated_status_content(head_status)
                expected_normal = generated_status_content(expected_status)
            except PortIndexError as exc:
                failures.append(f"{STATUS_GENERATED_PATH}: {exc}")
            else:
                if head_normal != expected_normal:
                    failures.append(
                        f"{STATUS_GENERATED_PATH}: generated content differs from the "
                        "recorded base; only the hand-written narrative window is editable"
                    )
                elif head_status != expected_status:
                    # A narrative-only edit still has to obey the hand-written
                    # contract, so it cannot add a fake generated table.
                    try:
                        narrative(head_status.decode("utf-8"))
                    except (UnicodeDecodeError, PortIndexError) as exc:
                        failures.append(f"{STATUS_GENERATED_PATH}: {exc}")

    head_baseline = _git_bytes(root, "show", f"HEAD:{BASELINE_GENERATED_PATH}")
    expected_baseline = _git_bytes(
        root, "show", f"{expected_side}:{BASELINE_GENERATED_PATH}"
    )
    if head_baseline is not None or expected_baseline is not None:
        if head_baseline is None or expected_baseline is None:
            failures.append(
                f"{BASELINE_GENERATED_PATH}: baseline is present on only one side; "
                "the baseline is bot-owned and cannot be added or removed"
            )
        else:
            try:
                head_document = json.loads(head_baseline)
                expected_document = json.loads(expected_baseline)
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                failures.append(f"{BASELINE_GENERATED_PATH}: invalid baseline JSON: {exc}")
            else:
                if _without_notes(head_document) == _without_notes(expected_document):
                    if head_baseline != expected_baseline and not allow_baseline_note_edits:
                        failures.append(
                            f"{BASELINE_GENERATED_PATH}: baseline notes may change only "
                            "in a reviewed oracle-change PR; floors and captures are "
                            "bot-owned"
                        )
                else:
                    details = baseline_change_errors(head_document, expected_document)
                    failures.append(
                        f"{BASELINE_GENERATED_PATH}: " + "; ".join(details)
                    )
    return failures



def generated_status_content(content: bytes) -> bytes:
    try:
        text = content.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise PortIndexError(f"{STATUS_GENERATED_PATH}: invalid UTF-8: {exc}") from exc
    if NARRATIVE_START not in text and NARRATIVE_END not in text:
        return content
    if text.count(NARRATIVE_START) != 1 or text.count(NARRATIVE_END) != 1:
        raise PortIndexError("PORT-STATUS narrative requires exactly one start/end marker pair")
    before, rest = text.split(NARRATIVE_START)
    if NARRATIVE_END in before or NARRATIVE_END not in rest:
        raise PortIndexError("PORT-STATUS narrative markers are reversed")
    _, after = rest.split(NARRATIVE_END)
    return (before + NARRATIVE_START + NARRATIVE_END + after).encode("utf-8")


def narrative(text: str) -> str:
    if text.count(NARRATIVE_START) != 1 or text.count(NARRATIVE_END) != 1:
        raise PortIndexError("PORT-STATUS narrative requires exactly one start/end marker pair")
    before, rest = text.split(NARRATIVE_START)
    if NARRATIVE_END in before or NARRATIVE_END not in rest:
        raise PortIndexError("PORT-STATUS narrative markers are reversed")
    body = rest.split(NARRATIVE_END)[0]
    lines = [line.strip() for line in body.splitlines() if line.strip()]
    if not lines or lines[0] != NARRATIVE_HEADING:
        raise PortIndexError(
            "PORT-STATUS narrative must begin with "
            f"{NARRATIVE_HEADING!r} so hand-written prose is distinguishable "
            "from generated tables"
        )
    for line in lines:
        if line in FORBIDDEN_NARRATIVE_LINES:
            raise PortIndexError(
                "PORT-STATUS narrative may not contain the generated marker "
                f"{line!r}; a narrative cannot impersonate generated output"
            )
    return body


def render_status(index: ShardIndex, previous: str) -> bytes:
    if NARRATIVE_START in previous or NARRATIVE_END in previous:
        prose = narrative(previous)
    else:
        # One-time migration: retain the limitations, decisions and blockers,
        # not the stale hand-maintained boards the shard tables replace.
        prose = "\n" + NARRATIVE_HEADING + "\n"
        if previous.strip():
            prose += (
                "\n## Deferred / known limitations"
                + previous.partition("## Deferred / known limitations")[2].partition("## Metrics")[0]
            )

    lines = ["# Port status", "",
             "<!-- Generated by scripts/port/build_port_index.py; edit unit shards only. -->",
             "", NARRATIVE_START + prose + NARRATIVE_END, "",
             "## Unit summary", "", "| Status | Units |", "| --- | ---: |"]
    counts = Counter(row.fields[5] for row in index.queue.values())
    lines.extend(f"| {status} | {count} |" for status, count in sorted(counts.items()))
    lines.extend(["", "## Ledger", "", "| Metric | Value |", "| --- | ---: |",
                  f"| Ledger rows | {sum(len(rows) for rows in index.ledger.values())} |",
                  f"| Ledger fragments | {len(index.fragments)} |"])
    lines.extend(["", "## Units", "",
                  "| Unit | Wave | Area | Work | Status | Evidence |",
                  "| --- | --- | --- | --- | --- | --- |"])

    def cell(value):
        return value.replace("|", "\\|").replace("\n", " ")

    for row in sorted(index.queue.values(),
                      key=lambda row: (_wave_key(row.fields[1]), row.fields[0])):
        fields = row.fields
        evidence = f"`units/{fields[0]}.*`; {len(index.ledger.get(fields[0], ()))} ledger rows"
        lines.append("| " + " | ".join(map(cell, (*fields[:4], fields[5], evidence))) + " |")
    return ("\n".join(lines) + "\n").encode("utf-8")


def _units_changes(root: Path, ref: str) -> list[tuple[str, str]]:
    """NUL-separated name-status changes under docs/port/units."""
    listing = _git(
        root, "diff", "--name-status", "--no-renames", "-z", ref, "HEAD", "--",
        "docs/port/units",
    )
    parts = listing.split(b"\0")
    if parts and parts[-1] == b"":
        parts.pop()
    if len(parts) % 2 != 0:
        raise PortIndexError("git diff returned an incomplete NUL-separated change record")
    changes = []
    for position in range(0, len(parts), 2):
        try:
            status = parts[position].decode("ascii", "strict")
            name = parts[position + 1].decode("utf-8", "strict")
        except UnicodeDecodeError as exc:
            raise PortIndexError(
                "docs/port/units diff contains a non-UTF-8 path; refusing an ambiguous diff"
            ) from exc
        if len(status) != 1 or status not in "ACDMRTUXB":
            raise PortIndexError(f"unexpected diff record status {status!r} for {name!r}")
        changes.append((status, name))
    return changes


def _record_rows(content: bytes | None, header: str, label: str) -> dict[str, str]:
    if content is None:
        return {}
    try:
        text = content.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise PortIndexError(f"{label}: not valid UTF-8") from exc
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if not lines:
        raise PortIndexError(f"{label}: empty shared unit file")
    if lines[0] != header:
        raise PortIndexError(f"{label}: expected the exact header")
    rows: dict[str, str] = {}
    for line in lines[1:]:
        fields = line.split("\t")
        if not fields[0].strip() or fields[0] in rows:
            raise PortIndexError(f"{label}: malformed or duplicate record {line!r}")
        rows[fields[0]] = line
    return rows


def _shared_record_errors(root: Path, ref: str, expected: str | None) -> list[str]:
    """A unit PR may change shared retirement/correction records only for itself."""
    if expected is None:
        return []
    errors = []
    for name, header in ((REMOVED_NAME, REMOVED_HEADER),
                         (CORRECTIONS_NAME, CORRECTIONS_HEADER)):
        path = f"docs/port/units/{name}"
        head = _git_bytes(root, "show", f"HEAD:{path}")
        base = _git_bytes(root, "show", f"{ref}:{path}")
        if head == base:
            continue
        head_rows = _record_rows(head, header, f"{path} (head)")
        base_rows = _record_rows(base, header, f"{path} (base)")
        for unit_id in sorted(set(head_rows) | set(base_rows)):
            if unit_id == expected:
                continue
            if head_rows.get(unit_id) != base_rows.get(unit_id):
                errors.append(
                    f"{path}: a PR owning {expected} may not add, edit, or delete "
                    f"the record for {unit_id}"
                )
    return errors


def _removed_units_at_head(root: Path) -> set[str]:
    content = _git_bytes(root, "show", f"HEAD:docs/port/units/{REMOVED_NAME}")
    if content is None:
        return set()
    try:
        text = content.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise PortIndexError(f"{REMOVED_NAME}: not valid UTF-8") from exc
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if not lines or lines[0] != REMOVED_HEADER:
        raise PortIndexError(f"{REMOVED_NAME}: expected the exact retirement header")
    units = set()
    for line in lines[1:]:
        fields = line.split("\t")
        if len(fields) != len(REMOVED_COLUMNS) or not fields[0].strip():
            raise PortIndexError(f"{REMOVED_NAME}: malformed retirement row {line!r}")
        units.add(fields[0])
    return units


def shard_ownership_errors(root: Path, ref: str, unit: str | None = None,
                           pr: str | None = None) -> list[str]:
    """Existing evidence is frozen; a PR adds only its own evidence namespace.

    A documented legacy-to-history migration is allowed when the legacy file is
    deleted in the same change and the new history bytes are its verbatim
    content. A retirement may delete the legacy file only when removed.tsv
    records that unit.
    """
    changes = _units_changes(root, ref)
    errors = []
    owned = set()
    fragments = []
    history_adds: dict[str, str] = {}
    legacy_deletes: dict[str, str] = {}
    queue_deletes: dict[str, str] = {}
    retired = _removed_units_at_head(root)

    for status, name in changes:
        path = Path(name)
        if path.parent.name.endswith(FRAGMENT_SUFFIX):
            unit_id = path.parent.name[:-len(FRAGMENT_SUFFIX)]
            owned.add(unit_id)
            if path.name == HISTORY_NAME:
                if status != "A":
                    errors.append(
                        f"{name}: a one-time history migration may only add "
                        f"{HISTORY_NAME}; history is immutable once written"
                    )
                else:
                    history_adds[unit_id] = name
                continue
            match = FRAGMENT_NAME.fullmatch(path.name)
            fragments.append(name)
            if status != "A" or match is None or (pr and match[1] != str(pr)):
                errors.append(
                    f"{name}: a PR may only add one immutable "
                    f"<its-pr>-<short-slug>.tsv ledger fragment"
                )
        elif path.name.endswith(QUEUE_SUFFIX):
            unit_id = path.name[:-len(QUEUE_SUFFIX)]
            owned.add(unit_id)
            if status == "D":
                queue_deletes[unit_id] = name
        elif path.name.endswith(LEDGER_SUFFIX):
            unit_id = path.name[:-len(LEDGER_SUFFIX)]
            owned.add(unit_id)
            if status == "D":
                legacy_deletes[unit_id] = name
            else:
                errors.append(f"{name}: legacy history is frozen; add a ledger fragment instead")

    for unit_id, name in sorted(queue_deletes.items()):
        if unit_id not in retired:
            errors.append(
                f"{name}: a queue shard may be deleted only when removed.tsv "
                "records the unit's retirement"
            )

    for unit_id, name in sorted(history_adds.items()):
        legacy = f"docs/port/units/{unit_id}{LEDGER_SUFFIX}"
        if legacy not in legacy_deletes.values():
            errors.append(
                f"{name}: a one-time history migration must delete {legacy} in "
                "the same change"
            )
            continue
        base_content = _git_bytes(root, "show", f"{ref}:{legacy}")
        head_content = _git_bytes(root, "show", f"HEAD:{name}")
        if base_content is None or head_content is None or base_content != head_content:
            errors.append(
                f"{name}: history migration must copy {legacy} verbatim; rows "
                "may not be added, removed, reordered, or reworded"
            )

    for unit_id, name in sorted(legacy_deletes.items()):
        if unit_id in history_adds:
            continue
        if unit_id not in retired:
            errors.append(
                f"{name}: legacy history is frozen; delete it only when removed.tsv "
                f"records the unit's retirement or when it is migrated verbatim to "
                f"docs/port/units/{unit_id}{FRAGMENT_SUFFIX}/{HISTORY_NAME}"
            )

    if len(owned) > 1 or (unit is not None and owned - {unit}):
        errors.append(
            "a PR owns exactly one unit queue/evidence namespace: "
            + ", ".join(sorted(owned))
        )
    if len(fragments) > 1:
        errors.append("a PR may add exactly one ledger fragment")
    expected = unit or (next(iter(owned)) if len(owned) == 1 else None)
    errors.extend(_shared_record_errors(root, ref, expected))
    return errors



def capture_baseline(baseline: Path, results: Path, run: dict, root: Path) -> bytes:
    """Validate downloaded merged-main JUnit/exit artifacts before raising floors."""
    if not isinstance(run, dict):
        raise PortIndexError("floor capture run must be a JSON object")
    if (run.get("event") != "push" or run.get("head_branch") != "main"
            or run.get("conclusion") != "success" or run.get("name") != "linux-ci"
            or not re.fullmatch(r"[0-9a-f]{40}", run.get("head_sha", ""))
            or not isinstance(run.get("id"), int)
            or not isinstance(run.get("html_url"), str)
            or not isinstance(run.get("updated_at"), str)):
        raise PortIndexError("floor capture requires a successful main-push linux-ci run")
    if run["head_sha"] != _git(root, "rev-parse", "HEAD").decode().strip():
        raise PortIndexError("floor capture run head does not match checked-out merged main")
    document = json.loads(baseline.read_text())
    if not isinstance(document, dict):
        raise PortIndexError("baseline must be a JSON object")
    previous_document = None
    previous_bytes = _git_bytes(root, "show", f"HEAD^:{BASELINE_GENERATED_PATH}")
    if previous_bytes is not None:
        try:
            previous_document = json.loads(previous_bytes)
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise PortIndexError(f"previous main baseline is invalid JSON: {exc}") from exc
        if not isinstance(previous_document, dict):
            raise PortIndexError("previous main baseline must be a JSON object")
        previous_errors = baseline_document_errors(previous_document)
        if previous_errors:
            raise PortIndexError(
                "previous main baseline is invalid: " + "; ".join(previous_errors)
            )
    document_errors = baseline_document_errors(document)
    if document_errors:
        raise PortIndexError("baseline document is invalid: " + "; ".join(document_errors))
    spec = importlib.util.spec_from_file_location(
        "linux_results", ROOT / "scripts/ci/check_linux_test_results.py")
    checker = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(checker)
    report = json.loads((results / "test-counts.json").read_text())
    if report.get("failures") or not report.get("projects"):
        raise PortIndexError("floor capture artifact reports failures or no projects")
    projects = report["projects"]
    discovered = _git(
        root, "ls-tree", "-r", "--name-only", "HEAD", "--", "tests"
    ).decode().splitlines()
    expected = set()
    for name in discovered:
        if name.endswith(".csproj") and re.search(
                rb"<IsTestProject>\s*true\s*</IsTestProject>|Microsoft\.NET\.Test\.Sdk",
                _git(root, "show", f"HEAD:{name}")):
            expected.add(Path(name).stem)
    if set(projects) != expected:
        raise PortIndexError("floor artifact project set differs from merged-main test projects")
    counted = document.get("new") or {}
    if not isinstance(counted, dict):
        raise PortIndexError("baseline new suite must be an object")
    retired = set(document.get("removed_projects") or {})
    missing = set(counted) - set(projects) - retired
    if missing:
        raise PortIndexError(
            "floor capture omits recorded project(s) without a retirement record: "
            + ", ".join(sorted(missing))
        )

    # Historical floors come from every structured capture in the head document
    # and from the previous main baseline. A floor may only rise; a tampered
    # document cannot talk the bot into accepting a lower value.
    floors, floor_errors = baseline_capture_floors(document)
    if floor_errors:
        raise PortIndexError("baseline capture floors are invalid: " + "; ".join(floor_errors))
    previous_floors: dict[str, dict[str, int]] = {}
    if previous_document is not None:
        previous_floors, previous_floor_errors = baseline_capture_floors(previous_document)
        if previous_floor_errors:
            raise PortIndexError(
                "previous main capture floors are invalid: "
                + "; ".join(previous_floor_errors)
            )
        previous_removed = previous_document.get("removed_projects") or {}
        current_removed = document.get("removed_projects") or {}
        if not isinstance(previous_removed, dict) or not isinstance(current_removed, dict):
            raise PortIndexError("removed_projects must be an object in both baselines")
        for project, record in previous_removed.items():
            if current_removed.get(project) != record:
                raise PortIndexError(
                    f"removed_projects entry for {project} was deleted or rewritten; "
                    "retirement records are immutable"
                )
        previous_captures = previous_document.get("main_captures") or []
        current_captures = document.get("main_captures") or []
        if not isinstance(previous_captures, list) or not isinstance(current_captures, list):
            raise PortIndexError("main_captures must be a list")
        for entry in previous_captures:
            if not isinstance(entry, dict):
                continue
            identity = _capture_identity(entry)
            if not any(isinstance(item, dict) and _capture_identity(item) == identity
                       for item in current_captures):
                raise PortIndexError(
                    f"main_captures entry for run {entry.get('run_id')} was removed or "
                    "rewritten; capture history is append-only"
                )

        previous_top_level = _top_level_captures(previous_document)
        current_top_level = _top_level_captures(document)
        for key, value in previous_top_level.items():
            if current_top_level.get(key) != value:
                raise PortIndexError(
                    f"capture record {key} was removed or rewritten; capture history "
                    "is append-only"
                )

    for project in sorted(retired):
        if project in projects:
            raise PortIndexError(
                f"{project}: removal record conflicts with a test project that still "
                "exists at merged main"
            )
        if project not in floors and project not in previous_floors:
            raise PortIndexError(
                f"{project}: removal record is not justified by any recorded capture; "
                "a retirement needs real measured evidence"
            )

    observed = {}
    for project, result in sorted(projects.items()):
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", project):
            raise PortIndexError("invalid project name in floor artifact")
        directory = results / project
        counts = checker.read_counts(directory / f"{project}.xml")
        exit_code = int((directory / "exit-code.txt").read_text())
        if (exit_code != 0 or result.get("exit_code") != 0 or result.get("counts") != counts
                or result.get("outcome") != "pass" or counts["failed"] or counts["skipped"]
                or counts["executed"] == 0):
            raise PortIndexError(f"{project}: invalid or unsuccessful floor artifact")
        floor = dict(previous_floors.get(project, {}))
        for field, value in floors.get(project, {}).items():
            floor[field] = max(value, floor.get(field, value))
        for field in ("total", "executed", "passed"):
            if counts[field] < floor.get(field, 0):
                raise PortIndexError(
                    f"{project}: refusing to lower recorded {field} floor "
                    f"{floor.get(field)}; the merged-main run measured {counts[field]}"
                )
        observed[project] = counts
    for project in sorted(set(floors) | set(previous_floors)):
        if project in retired or project in observed:
            continue
        raise PortIndexError(
            f"{project}: recorded project is missing from the floor artifact without "
            "a retirement record"
        )

    # Preserve every pre-existing capture and its provenance under its original key.
    captures = document.setdefault("main_captures", [])
    if not isinstance(captures, list):
        raise PortIndexError("main_captures must be a list")
    capture = {"run_id": run["id"], "run_url": run["html_url"],
               "commit": run["head_sha"], "captured_at": run["updated_at"],
               "counts": observed,
               "notes": "Counts parsed from this merged-main linux-ci run's downloaded "
                        "JUnit and MTP exit artifacts; all previous capture records are "
                        "retained."}
    identity = ("run_id", "run_url", "commit", "captured_at", "counts")
    matches = [item for item in captures
               if isinstance(item, dict) and item.get("run_id") == run["id"]]
    if len(matches) > 1 and any(
            {key: item.get(key) for key in identity}
            != {key: matches[0].get(key) for key in identity}
            for item in matches[1:]):
        raise PortIndexError(
            "floor capture run is recorded more than once with different provenance")
    if matches and {key: matches[0].get(key) for key in identity} != {
            key: capture.get(key) for key in identity}:
        raise PortIndexError("floor capture run already recorded with different provenance")
    if not matches:
        # Every new run records provenance, including a same-count run. The run
        # id keeps a replayed capture idempotent.
        if observed == counted:
            capture["notes"] = (
                "Counts parsed from this merged-main linux-ci run's downloaded JUnit "
                "and MTP exit artifacts; they equal the current floor. The run is "
                "recorded as provenance without changing the number."
            )
        captures.append(capture)
    document["new"] = observed
    empty = document.get("empty_projects")
    if isinstance(empty, dict):
        for project in observed:
            empty.pop(project, None)
    final_errors = baseline_document_errors(document)
    if final_errors:
        raise PortIndexError(
            "captured baseline would be invalid: " + "; ".join(final_errors)
        )
    return (json.dumps(document, indent=2, ensure_ascii=False) + "\n").encode()



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
    base_loader=None,
    ancestor_guard=None,
) -> int:
    # One shard base owns every output and repository/history operation.
    units = units.resolve()
    root = units.parents[2]
    queue = units.parent / "queue.tsv"
    ledger = units.parent / "ledger.tsv"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("build", "check"))
    parser.add_argument("--capture-results", type=Path)
    parser.add_argument("--capture-run", type=Path)
    parser.add_argument("--unit", help="unit owned by this PR")
    parser.add_argument("--pr", default=os.environ.get("PORT_INDEX_PR"), help="PR number for its new fragment")
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
            "fail when the change against --against edits generated or bot-owned "
            "files, and enforce one-unit shard ownership (the pull request gate); "
            "requires --against"
        ),
    )
    parser.add_argument(
        "--forbid-shard-ownership",
        action="store_true",
        help=(
            "enforce one-unit shard ownership and fragment/migration/retirement "
            "rules against --against without checking the generated aggregates "
            "(the post-merge writer gate); requires --against"
        ),
    )
    parser.add_argument(
        "--allow-baseline-note-edits",
        action="store_true",
        default=os.environ.get("PORT_BASELINE_NOTE_EDITS") == "true",
        help=(
            "allow a reviewed oracle-change PR to edit human notes in "
            "tests/baseline.json; floors, captures and removals remain immutable"
        ),
    )
    parser.add_argument(
        "--recorded-base",
        metavar="REF",
        default=None,
        help=(
            "recorded event base (the current mainline) used to reject a stale "
            "baseline or status; falls back to RECORDED_BASE"
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
        status = root / STATUS_GENERATED_PATH
        baseline = root / BASELINE_GENERATED_PATH
        try:
            if bool(args.capture_results) != bool(args.capture_run):
                raise PortIndexError(
                    "floor capture requires both --capture-results and --capture-run")
            baseline_bytes = (capture_baseline(baseline, args.capture_results,
                              json.loads(args.capture_run.read_text()), root)
                              if args.capture_results else None)
            status_bytes = render_status(
                index, status.read_text() if status.exists() else "")
        except (PortIndexError, OSError, ValueError, KeyError, TypeError) as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 1
        queue.write_bytes(queue_bytes)
        ledger.write_bytes(ledger_bytes)
        status.write_bytes(status_bytes)
        if baseline_bytes is not None:
            baseline.write_bytes(baseline_bytes)
        print(f"wrote {queue}, {ledger} and {status}")
        return 0

    forbid_ownership = args.forbid_aggregate_changes or args.forbid_shard_ownership
    if forbid_ownership:
        if not args.against:
            flag = ("--forbid-aggregate-changes" if args.forbid_aggregate_changes
                    else "--forbid-shard-ownership")
            print(f"error: {flag} requires --against", file=sys.stderr)
            return 1
        recorded_base = (args.recorded_base
                         or os.environ.get("RECORDED_BASE") or None)
        try:
            changed = (changed_aggregates(
                root, args.against, queue, ledger, recorded_base, index,
                args.allow_baseline_note_edits,
            ) if args.forbid_aggregate_changes else [])
            ownership = shard_ownership_errors(root, args.against, args.unit, args.pr)
        except PortIndexError as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 1
        if changed or ownership:
            print(
                "error: pull requests must not modify generated or bot-owned files. "
                "Edit only your own unit shards under docs/port/units/ (one queue "
                "shard and at most one new <pr>-<slug>.tsv ledger fragment) and leave "
                "docs/port/queue.tsv, docs/port/ledger.tsv, docs/port/PORT-STATUS.md "
                "and tests/baseline.json to the post-merge port-index workflow; drop "
                "those file changes from this PR.",
                file=sys.stderr,
            )
            for name in changed:
                print(f"  changed: {name}", file=sys.stderr)
            for error in ownership:
                print(f"  ownership: {error}", file=sys.stderr)
            return 1

    if not args.shards_only:
        queue_ok = _check_file(queue, queue_bytes)
        ledger_ok = _check_file(ledger, ledger_bytes)
        status = root / STATUS_GENERATED_PATH
        try:
            if status.exists():
                status_ok = _check_file(status, render_status(index, status.read_text()))
            else:
                print(f"error: {status} is missing; run the generator build", file=sys.stderr)
                status_ok = False
        except PortIndexError as exc:
            print(f"error: {exc}", file=sys.stderr)
            status_ok = False
        if not (queue_ok and ledger_ok and status_ok):
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
