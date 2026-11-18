"""Shared, deterministic helpers for the port unit evidence guard.

Evidence namespaces
-------------------
A unit owns exactly these paths (the contract enforced by
``check_unit_ownership.py``):

* ``docs/port/units/<unit>.queue.tsv``
* ``docs/port/units/<unit>.ledger.tsv`` (legacy)
* ``docs/port/units/<unit>.ledger.d/*`` (per-PR fragments)
* ``docs/port/<unit>-*.md``
* ``tests/golden/<unit>-*``
* ``tests/fixtures/<unit>-*``
* ``tools/<unit>*``

Everything else is shared product, process, generated, or oracle material and
is outside the unit evidence namespace.  Aggregates (``docs/port/queue.tsv``,
``docs/port/ledger.tsv``), ``tests/baseline.json``, generated status content and
``docs/port/units/ownership.paths`` are never unit evidence and are never
PR-editable.

Path ownership map
------------------
``docs/port/units/ownership.paths`` is a bot-generated TSV with the exact header
``path<TAB>unit<TAB>commit<TAB>pull`` and one row per ``(path, owner)``.  It
extends ownership beyond evidence to product source, shared tests, non-prefixed
fixtures and tools.  A path may have several owners; any listed owner may change
it and a non-owner fails.  ``*`` marks explicitly shared material when a commit's
attribution is uncertain.  The guard reads the map from the pull request's base
commit, never its head, validates header, units, ordering, commit ancestry and
pull agreement, and fails with ``ownership map unavailable`` when the map is
missing, empty, forged or stale.  Missing/unowned product paths still require a
unit declaration and evidence; a path the map does not list stays shared only for
the purpose of ownership, not for the declaration requirement.

Ledger fragments
----------------
``docs/port/units/<unit>.ledger.d/<pr>-<slug>.tsv`` is a ledger shard: the
normal ledger header followed by one or more rows whose ``unit`` column is the
owning unit.  Its evidence column must name the pull request, a head commit
produced by this pull request and the real linux-ci run whose result it relies
on.  The guard accepts the historical free-text forms (``PR #97``, ``pull/97``,
``head 2e09983...`` and ``actions/runs/43964833995``).  The cited head must be on
the pull request's own first-parent spine after the merge base (or the merge
commit, when supplied); an unrelated ancestor or a main commit merged after the
branch point is rejected rather than treated as evidence.  Every cited head must
also resolve to a commit in the repository, so a mistyped or invented SHA fails
instead of being skipped.

With ``--verify-ci`` (always enabled by the unit-evidence CI job) every cited
run id is resolved through the read-only GitHub Actions API: the run must exist
in this repository, be a completed ``linux-ci`` run with conclusion ``success``,
and its ``head_sha`` must equal a head the fragment cites.  A run id from another
repository, a missing run, an unfinished or failed run, an unreachable API or a
disagreeing head fails closed; the check never caches an API result.

Diff paths are repository-relative with no traversal component.  Renames are
expanded to an explicit delete plus add, deletions are checked like any other
change, and every intermediate component of a changed path or symlink target is
resolved; an escape, loop, symlinked ancestor or foreign-owned target fails.

This module deliberately knows nothing about the aggregate generator.  It uses
git plumbing only: no checkout, fetch, reset, or write occurs in the worktree.
"""

from __future__ import annotations

import json
import os
import posixpath
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path

QUEUE_HEADER = "\t".join(
    ["id", "wave", "area", "unit", "depends", "status", "owner", "oracle", "test", "notes"]
)
QUEUE_COLUMNS = QUEUE_HEADER.split("\t")
LEDGER_HEADER = "\t".join(["timestamp", "unit", "from", "to", "actor", "evidence"])
LEDGER_COLUMNS = LEDGER_HEADER.split("\t")

# A queue row is evidence only after an author moves it out of the untouched
# backlog state.  The generator itself accepts any text; the guard does not.
REAL_QUEUE_STATES = frozenset({"in-progress", "done", "deferred"})

UNIT_ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
QUEUE_SHARD = re.compile(r"^([a-z0-9]+(?:-[a-z0-9]+)*)\.queue\.tsv$")
LEDGER_SHARD = re.compile(r"^([a-z0-9]+(?:-[a-z0-9]+)*)\.ledger\.tsv$")
LEDGER_FRAGMENT = re.compile(
    r"^([a-z0-9]+(?:-[a-z0-9]+)*)\.ledger\.d/(?:.+)$"
)
FRAGMENT_NAME = re.compile(r"^([1-9]\d*)-([a-z0-9][a-z0-9-]*)\.tsv$")

UNITS_DIR = "docs/port/units/"
UNITS_NON_EVIDENCE = frozenset(
    {"README.md", "removed.tsv", "ledger-corrections.tsv", "ownership.paths"}
)

# Bot-generated path ownership map.  It lives beside the unit shards so a
# reviewer sees it with the evidence, but it is never PR-editable and is read
# from the pull request's base commit, never from its head.
OWNERSHIP_MAP = "docs/port/units/ownership.paths"
OWNERSHIP_HEADER = "path\tunit\tcommit\tpull"
OWNERSHIP_ROOTS = ("src/", "tests/", "tools/")
OWNERSHIP_EXACT_EXCLUDES = frozenset({"tests/baseline.json"})
OWNERSHIP_SUFFIX_EXCLUDES = (
    ".csproj",
    ".slnx",
    ".sln",
    ".props",
    ".targets",
    ".lock.json",
    ".editorconfig",
    ".gitignore",
    ".gitattributes",
)
OWNERSHIP_NAME_EXCLUDES = frozenset({"global.json", "nuget.config", "NuGet.config"})
OWNERSHIP_PULL = re.compile(r"^pull/([1-9][0-9]*)$")
SHARED_OWNER = "*"


class GuardError(Exception):
    """A deterministic policy or input error.  The caller exits non-zero."""


@dataclass(frozen=True)
class Change:
    status: str
    path: str


@dataclass(frozen=True)
class OwnershipClaim:
    path: str
    unit: str
    commit: str
    pull: str

    @property
    def evidence(self) -> str:
        if self.pull == "-":
            return f"commit {self.commit}"
        return f"{self.pull} at commit {self.commit}"

    @property
    def shared(self) -> bool:
        return self.unit == SHARED_OWNER


@dataclass(frozen=True)
class OwnershipMap:
    """The validated base map plus why protection may be unavailable."""

    claims: dict[str, tuple[OwnershipClaim, ...]]
    available: bool
    reason: str | None

    def owners(self, path: str) -> tuple[OwnershipClaim, ...]:
        return self.claims.get(path, ())


def git(repo: Path, *args: str) -> bytes:
    """Run a read-only git command without a shell and return stdout bytes."""

    command = ["git", "-C", str(repo), *args]
    env = dict(os.environ)
    env.setdefault("LC_ALL", "C")
    env.setdefault("GIT_OPTIONAL_LOCKS", "0")
    try:
        result = subprocess.run(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
            env=env,
        )
    except OSError as exc:  # pragma: no cover - a missing git binary is environmental
        raise GuardError(f"cannot run git: {exc}") from exc
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", "replace").strip()
        raise GuardError(
            f"git {' '.join(args)} failed with exit {result.returncode}: {detail}"
        )
    return result.stdout


def repo_root(value: str | None) -> Path:
    if value:
        root = Path(value).resolve()
    else:
        root = Path(__file__).resolve().parents[2]
    if not root.is_dir():
        raise GuardError(f"repository directory does not exist: {root}")
    return root


def resolve_commit(repo: Path, revision: str, label: str) -> str:
    if not revision or revision.startswith("-"):
        raise GuardError(f"{label} is empty or looks like an option: {revision!r}")
    try:
        output = git(repo, "rev-parse", "--verify", "--end-of-options", f"{revision}^{{commit}}")
    except GuardError as exc:
        raise GuardError(f"{label} {revision!r} is not a commit in this checkout") from exc
    sha = output.decode("ascii", "strict").strip().lower()
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise GuardError(f"{label} {revision!r} did not resolve to a full commit id")
    return sha


def merge_base(repo: Path, base: str, head: str) -> str:
    try:
        output = git(repo, "merge-base", "--all", base, head)
    except GuardError as exc:
        raise GuardError(f"cannot compute the merge base of {base} and {head}") from exc
    shas = [line.strip() for line in output.decode("ascii", "strict").splitlines() if line.strip()]
    if not shas:
        raise GuardError(f"{base} and {head} have no merge base")
    if len(shas) != 1:
        raise GuardError(
            f"{base} and {head} have {len(shas)} merge bases; refusing an ambiguous diff"
        )
    return shas[0]


def path_error(path: str) -> str | None:
    """Return why a diff path is unsafe, or ``None`` when it is a plain path.

    A path is repository-relative, has no traversal or empty component, and is
    not absolute in POSIX, Windows-drive, or UNC spelling.  Rejecting ``..``
    explicitly matters most for the offline diff-file mode, where the entries
    are caller-supplied rather than produced by git.
    """

    if not path:
        return "the path is empty"
    if "\0" in path or "\n" in path or "\r" in path:
        return "the path contains a control character"
    if path.startswith("/") or path.startswith("\\") or re.match(r"^[A-Za-z]:", path):
        return "the path is absolute"
    parts = path.split("/")
    if any(part in {"", ".", ".."} for part in parts):
        return "the path contains an empty, '.', or '..' component (path traversal)"
    return None


def _checked_path(path: str, source: str) -> str:
    error = path_error(path)
    if error:
        raise GuardError(f"invalid path in {source}: {path!r}: {error}")
    return path


def changed_paths(repo: Path, base: str, head: str) -> list[Change]:
    """Return name-status changes between two commits.

    Rename detection is deliberately disabled: a rename is an explicit delete
    of the old path plus an add of the new path, so both the old and new unit
    namespaces are charged instead of a path-only text match.  The parser still
    understands a scored ``R``/``C`` record and turns it into the same delete
    plus add, so a git configuration change cannot hide half a rename.
    """

    output = git(
        repo,
        "diff",
        "--no-ext-diff",
        "--no-textconv",
        "--no-renames",
        "--name-status",
        "-z",
        "--diff-filter=ACDMRTUXB",
        base,
        head,
    )
    parts = output.split(b"\0")
    if parts and parts[-1] == b"":
        parts.pop()
    changes: list[Change] = []
    index = 0
    while index < len(parts):
        try:
            status = parts[index].decode("ascii", "strict")
        except UnicodeDecodeError as exc:
            raise GuardError("git diff contains a non-ASCII status; refusing an ambiguous diff") from exc
        index += 1
        if status[0] in {"R", "C"} and re.fullmatch(r"[RC][0-9]*", status):
            pass
        elif status not in {"A", "D", "M", "T", "U", "X", "B"}:
            raise GuardError(f"unsupported or ambiguous git diff status {status!r}")
        if status[0] in {"R", "C"}:
            if index + 2 > len(parts):
                raise GuardError("git diff returned an incomplete rename record")
            try:
                old = parts[index].decode("utf-8", "strict")
                new = parts[index + 1].decode("utf-8", "strict")
            except UnicodeDecodeError as exc:
                raise GuardError(
                    "git diff contains a non-UTF-8 path; refusing an ambiguous diff"
                ) from exc
            index += 2
            changes.append(Change("D", _checked_path(old, "git diff")))
            changes.append(Change("A", _checked_path(new, "git diff")))
            continue
        if index >= len(parts):
            raise GuardError("git diff returned an incomplete name-status record")
        try:
            path = parts[index].decode("utf-8", "strict")
        except UnicodeDecodeError as exc:
            raise GuardError(
                "git diff contains a non-UTF-8 path; refusing an ambiguous diff"
            ) from exc
        index += 1
        changes.append(Change(status, _checked_path(path, "git diff")))
    changes.sort(key=lambda item: item.path)
    if len({item.path for item in changes}) != len(changes):
        raise GuardError("git diff lists the same path more than once; refusing an ambiguous diff")
    return changes


def paths_from_diff_file(path: Path) -> list[Change]:
    """Read a name-status or one-path-per-line diff for offline checks.

    Accepted records are ``<path>`` (treated as modified), ``<status><TAB><path>``
    and ``Rnnn<TAB><old><TAB><new>``.  Renames are expanded to an explicit delete
    plus add, exactly as the git path does.
    """

    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        raise GuardError(f"cannot read diff file {path}: {exc}") from exc
    lines = text.splitlines()
    if not lines:
        raise GuardError(f"diff file {path} contains no paths")
    result: list[Change] = []
    for line in lines:
        if not line.strip():
            raise GuardError(f"diff file {path} contains a blank path; refusing an ambiguous diff")
        fields = line.split("\t")
        if len(fields) == 1:
            result.append(Change("M", _checked_path(fields[0], f"diff file {path}")))
            continue
        status = fields[0]
        if status[0] in {"R", "C"} and re.fullmatch(r"[RC][0-9]*", status):
            pass
        elif status not in {"A", "D", "M", "T", "U", "X", "B"}:
            raise GuardError(f"diff file {path} has an unsupported status {status!r}")
        if status[0] in {"R", "C"}:
            if len(fields) != 3:
                raise GuardError(
                    f"diff file {path} rename record needs status, old and new path"
                )
            result.append(Change("D", _checked_path(fields[1], f"diff file {path}")))
            result.append(Change("A", _checked_path(fields[2], f"diff file {path}")))
            continue
        if len(fields) != 2:
            raise GuardError(f"diff file {path} has a malformed record: {line!r}")
        result.append(Change(status, _checked_path(fields[1], f"diff file {path}")))
    result.sort(key=lambda entry: entry.path)
    if len({entry.path for entry in result}) != len(result):
        raise GuardError(f"diff file {path} repeats a path; refusing an ambiguous diff")
    return result


def _tree_paths(repo: Path, revision: str, directory: str) -> list[str]:
    output = git(repo, "ls-tree", "-r", "--name-only", "-z", revision, "--", directory)
    paths: list[str] = []
    for item in output.split(b"\0"):
        if not item:
            continue
        try:
            paths.append(item.decode("utf-8", "strict"))
        except UnicodeDecodeError as exc:
            raise GuardError(
                f"tree {revision} contains a non-UTF-8 path under {directory}"
            ) from exc
    return paths


def known_units(repo: Path, revisions: list[str | None]) -> set[str]:
    """Return every unit id with a shard in any supplied revision.

    ``None`` means the working tree, used only by the offline ownership mode.
    """

    units: set[str] = set()
    for revision in revisions:
        if revision is None:
            directory = repo / "docs" / "port" / "units"
            if not directory.is_dir():
                continue
            names = [str(item.relative_to(repo)) for item in sorted(directory.glob("*.queue.tsv"))]
            names += [str(item.relative_to(repo)) for item in sorted(directory.glob("*.ledger.tsv"))]
        else:
            names = _tree_paths(repo, revision, "docs/port/units")
        for name in names:
            if not name.startswith(UNITS_DIR):
                continue
            relative = name[len(UNITS_DIR):]
            match = QUEUE_SHARD.fullmatch(relative) or LEDGER_SHARD.fullmatch(relative)
            if match:
                units.add(match.group(1))
                continue
            match = LEDGER_FRAGMENT.fullmatch(relative)
            if match:
                units.add(match.group(1))
    return units


def _one_unit(path: str, matches: list[str]) -> tuple[frozenset[str], str | None]:
    unique = sorted(set(matches))
    if len(unique) > 1:
        return frozenset(), (
            f"ambiguous unit attribution for {path}: it matches {', '.join(unique)}"
        )
    if not unique:
        return frozenset(), None
    return frozenset(unique), None


def attribute_path(path: str, units: set[str]) -> tuple[frozenset[str], str | None]:
    """Map one changed path to the unit namespace it belongs to.

    An unmatched path is shared/process material.  A path that could belong to
    more than one unit, or is an unrecognised entry under ``docs/port/units``,
    is ambiguous and fails closed.
    """

    if path.startswith(UNITS_DIR):
        relative = path[len(UNITS_DIR):]
        if relative in UNITS_NON_EVIDENCE:
            return frozenset(), None
        match = QUEUE_SHARD.fullmatch(relative) or LEDGER_SHARD.fullmatch(relative)
        if match:
            return frozenset({match.group(1)}), None
        match = LEDGER_FRAGMENT.fullmatch(relative)
        if match:
            return frozenset({match.group(1)}), None
        return frozenset(), (
            f"unrecognised path under {UNITS_DIR}: {path}; "
            "the units directory accepts only README.md, removed.tsv, "
            "ledger-corrections.tsv, ownership.paths, <unit>.queue.tsv, "
            "<unit>.ledger.tsv and <unit>.ledger.d/<pr>-<slug>.tsv"
        )

    if path.startswith("docs/port/"):
        relative = path[len("docs/port/"):]
        first = relative.split("/", 1)[0]
        if "/" not in relative and relative.endswith(".md"):
            return _one_unit(
                path,
                [unit for unit in units
                 if relative.startswith(f"{unit}-")
                 or first == unit],
            )
        matches = [
            unit for unit in units
            if first == unit or first.startswith(f"{unit}-")
        ]
        return _one_unit(path, matches)

    for prefix in ("tests/golden/", "tests/fixtures/"):
        if path.startswith(prefix):
            relative = path[len(prefix):]
            first = relative.split("/", 1)[0]
            return _one_unit(
                path,
                [unit for unit in units
                 if relative.startswith(f"{unit}-")
                 or first == unit
                 or first.startswith(f"{unit}-")],
            )

    if path.startswith("tools/"):
        relative = path[len("tools/"):]
        matches = []
        for unit in units:
            if relative == unit:
                matches.append(unit)
            elif relative.startswith(unit) and relative[len(unit)] in "-._/":
                matches.append(unit)
        return _one_unit(path, matches)

    return frozenset(), None


def touched_units(
    changes: list[Change], units: set[str], expected: str | None
) -> dict[str, list[str]]:
    """Group changed paths by unit namespace, rejecting ambiguous paths."""

    known = set(units)
    if expected:
        known.add(expected)
    touched: dict[str, list[str]] = {}
    errors: list[str] = []
    for change in changes:
        attributed, error = attribute_path(change.path, known)
        if error:
            errors.append(error)
            continue
        for unit in attributed:
            touched.setdefault(unit, []).append(change.path)
    if errors:
        raise GuardError("\n".join(errors))
    return {unit: sorted(paths) for unit, paths in sorted(touched.items())}


def bot_owned_errors(
    changes: list[Change],
    *,
    base_has_map: bool | None = None,
    product_changed: bool = False,
) -> list[str]:
    """Reject PR edits to the generated ownership map, even to identical bytes.

    The one bootstrap exception is the first change that adds the map when the
    base does not have it and the PR touches no product/test/tool path.  That
    lets the seeding tooling PR commit the generated map; a product PR cannot
    use it to forge ownership, and every later edit is bot-owned.
    """

    errors: list[str] = []
    for change in changes:
        if change.path != OWNERSHIP_MAP:
            continue
        adding_first_map = (
            change.status == "A" and base_has_map is False and not product_changed
        )
        if adding_first_map:
            continue
        errors.append(
            f"{change.path} is bot-owned and generated on main; a pull request "
            "must not add, edit, rename or delete the ownership map"
        )
    return errors


def ownership_candidate(path: str, units: set[str]) -> bool:
    """True when a path is product/test/tool material that needs an owner.

    Unit evidence namespaces stay in the existing attribution check; generated
    files and shared build metadata stay outside.  The file name only selects
    the roots the ownership map covers; it never decides an owner.
    """

    if not path.startswith(OWNERSHIP_ROOTS):
        return False
    if path in OWNERSHIP_EXACT_EXCLUDES:
        return False
    if any(path.endswith(suffix) for suffix in OWNERSHIP_SUFFIX_EXCLUDES):
        return False
    if Path(path).name in OWNERSHIP_NAME_EXCLUDES:
        return False
    if "/bin/" in path or "/obj/" in path:
        return False
    for prefix in ("tests/golden/", "tests/fixtures/"):
        if path.startswith(prefix):
            relative = path[len(prefix):]
            first = relative.split("/", 1)[0]
            if any(
                relative.startswith(f"{unit}-")
                or first == unit
                or first.startswith(f"{unit}-")
                for unit in units
            ):
                return False
    if path.startswith("tools/"):
        relative = path[len("tools/"):]
        for unit in units:
            if relative == unit or (
                relative.startswith(unit) and relative[len(unit)] in "-._/"
            ):
                return False
    return True


def product_paths(changes: list[Change], units: set[str]) -> list[str]:
    return [
        change.path
        for change in changes
        if ownership_candidate(change.path, units)
    ]


def ownership_map_exists(repo: Path, revision: str) -> bool:
    return read_at(repo, revision, OWNERSHIP_MAP) is not None


def read_ownership_map(
    repo: Path, revision: str, units: set[str] | None = None
) -> OwnershipMap:
    """Read and validate the bot-generated map at a base revision.

    The map is deliberately read from the pull request's base, never from its
    head: a branch cannot manufacture ownership for itself.  A missing or empty
    map is reported as unavailable rather than treated as permission, so product
    changes in that state still require an explicit unit declaration and
    evidence.  A malformed row fails closed with ``ownership map unavailable``.
    """

    data = read_at(repo, revision, OWNERSHIP_MAP)
    if data is None:
        return OwnershipMap({}, False, f"{OWNERSHIP_MAP} is missing at {revision}")
    try:
        text = data.decode("utf-8", "strict")
    except UnicodeDecodeError as exc:
        raise GuardError(f"ownership map unavailable: {OWNERSHIP_MAP} is not UTF-8") from exc
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if not lines:
        raise GuardError(f"ownership map unavailable: {OWNERSHIP_MAP} is empty")
    if lines[0] != OWNERSHIP_HEADER:
        raise GuardError(
            f"ownership map unavailable: {OWNERSHIP_MAP}:1: expected the exact "
            f"ownership header {OWNERSHIP_HEADER!r}"
        )
    claims: dict[str, list[OwnershipClaim]] = {}
    previous: tuple[str, str] | None = None
    ancestors = {
        line.strip()
        for line in git(repo, "rev-list", revision).decode("ascii", "strict").splitlines()
        if line.strip()
    }
    subjects: dict[str, str] = {}
    for offset, line in enumerate(lines[1:], start=2):
        if not line:
            raise GuardError(f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: blank row")
        fields = line.split("\t")
        if len(fields) != 4:
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: expected 4 "
                f"tab-separated fields, got {len(fields)}"
            )
        path, unit, commit, pull = fields
        error = path_error(path)
        if error:
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: {path!r}: {error}"
            )
        if unit != SHARED_OWNER and not UNIT_ID.fullmatch(unit):
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: invalid unit "
                f"id {unit!r}"
            )
        if units is not None and unit != SHARED_OWNER and unit not in units:
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: unknown unit "
                f"{unit!r}"
            )
        if not re.fullmatch(r"[0-9a-f]{40}", commit):
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: commit must "
                f"be a full 40-hex id, got {commit!r}"
            )
        if commit not in ancestors:
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: commit "
                f"{commit} is not an ancestor of the map revision {revision}; "
                "refusing a forged or stale base map"
            )
        if pull != "-" and not OWNERSHIP_PULL.fullmatch(pull):
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: pull must be "
                f"'pull/<number>' or '-', got {pull!r}"
            )
        if pull != "-":
            wanted = int(pull[len("pull/"):])
            if commit not in subjects:
                subjects[commit] = git(
                    repo, "log", "-1", "--format=%s", commit
                ).decode("utf-8", "replace")
            subject = subjects[commit]
            recorded = re.search(r"\(#([1-9][0-9]*)\)", subject)
            if recorded is None or int(recorded.group(1)) != wanted:
                raise GuardError(
                    f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: commit "
                    f"{commit} does not agree with recorded {pull} ({subject!r})"
                )
        key = (path, unit)
        if previous is not None and key < previous:
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: rows must be "
                f"sorted by path then unit; {key!r} follows {previous!r}"
            )
        previous = key
        if any(existing.unit == unit for existing in claims.get(path, ())):
            raise GuardError(
                f"ownership map unavailable: {OWNERSHIP_MAP}:{offset}: duplicate "
                f"owner {unit!r} for {path}; refusing an ambiguous base map"
            )
        claims.setdefault(path, []).append(OwnershipClaim(path, unit, commit, pull))
    if not claims:
        return OwnershipMap({}, False, f"{OWNERSHIP_MAP} has no rows at {revision}")
    return OwnershipMap(
        {path: tuple(sorted(rows, key=lambda claim: claim.unit))
         for path, rows in claims.items()},
        True,
        None,
    )


def first_map_errors(
    repo: Path, head: str, changes: list[Change], base_has_map: bool | None
) -> list[str]:
    """A first map must equal a deterministic regeneration of the head history."""

    adding = [
        change for change in changes
        if change.path == OWNERSHIP_MAP and change.status == "A"
    ]
    if base_has_map is not False or not adding:
        return []
    try:
        import build_port_index  # local import avoids a cycle at module load

        units = repo / "docs" / "port" / "units"
        index = build_port_index.build_index(units)
        expected = build_port_index.render_ownership_map(repo, units, index)
    except Exception as exc:  # fail closed on any generator error
        raise GuardError(
            f"ownership map unavailable: cannot regenerate the first map: {exc}"
        ) from exc
    actual = read_at(repo, head, OWNERSHIP_MAP)
    if actual != expected:
        raise GuardError(
            "ownership map unavailable: the first map does not match a "
            "deterministic regeneration of the head history; a process PR may "
            "seed only the generated map, not arbitrary ownership"
        )
    return []


def ownership_hits(
    changes: list[Change], ownership: OwnershipMap
) -> dict[str, tuple[OwnershipClaim, ...]]:
    """Map each changed path that the base map attributes to all of its owners."""

    return {
        change.path: ownership.claims[change.path]
        for change in changes
        if change.path in ownership.claims
    }


def claim_is_allowed(claims: tuple[OwnershipClaim, ...], unit: str | None) -> bool:
    if not claims:
        return True
    if unit is None:
        return False
    return any(claim.shared or claim.unit == unit for claim in claims)


def claim_owners(claims: tuple[OwnershipClaim, ...]) -> str:
    return ", ".join(claim.unit for claim in claims)


def _tree_entry(repo: Path, revision: str, path: str) -> tuple[str, str] | None:
    output = git(repo, "--literal-pathspecs", "ls-tree", "-z", revision, "--", path)
    entries = [item for item in output.split(b"\0") if item]
    if not entries:
        return None
    if len(entries) != 1:
        raise GuardError(f"{revision}:{path}: git returned more than one tree entry")
    try:
        header, name = entries[0].split(b"\t", 1)
        mode, kind, sha = header.decode("ascii", "strict").split(" ")
        decoded = name.decode("utf-8", "strict")
    except (ValueError, UnicodeDecodeError) as exc:
        raise GuardError(f"{revision}:{path}: malformed git tree entry") from exc
    if decoded != path:
        raise GuardError(f"{revision}:{path}: git returned tree entry {decoded!r}")
    return mode, sha


def _read_link(repo: Path, revision: str, path: str) -> str | None:
    entry = _tree_entry(repo, revision, path)
    if entry is None or entry[0] != "120000":
        return None
    data = git(repo, "cat-file", "blob", entry[1])
    try:
        target = data.decode("utf-8", "strict")
    except UnicodeDecodeError as exc:
        raise GuardError(f"symlink {path} has a non-UTF-8 target") from exc
    if not target:
        raise GuardError(f"symlink {path} has an empty target")
    if "\0" in target or "\n" in target or "\r" in target:
        raise GuardError(f"symlink {path} has a control character in its target")
    if target.startswith("/") or target.startswith("\\") or re.match(r"^[A-Za-z]:", target):
        raise GuardError(f"symlink {path} points outside the repository: {target!r}")
    return target


def resolve_path(repo: Path, revision: str, path: str) -> str:
    """Resolve every symlink component of a path and reject escapes or loops.

    The returned path is repository-relative.  A component that does not exist
    at ``revision`` is kept as written, so a new path can be checked before it is
    committed.  Intermediate components are resolved even when the final
    component is an ordinary file.
    """

    error = path_error(path)
    if error:
        raise GuardError(f"{path!r}: {error}")
    parts = path.split("/")
    resolved: list[str] = []
    visited: set[str] = set()
    steps = 0
    while parts:
        part = parts.pop(0)
        if part in {"", "."}:
            continue
        if part == "..":
            if not resolved:
                raise GuardError(f"{path} escapes the repository root")
            resolved.pop()
            continue
        candidate = "/".join(resolved + [part])
        target = _read_link(repo, revision, candidate)
        if target is None:
            resolved.append(part)
            continue
        steps += 1
        if steps > 64:
            raise GuardError(f"{path} has too many chained symlinks to resolve safely")
        if candidate in visited:
            raise GuardError(f"{path} resolves through a symlink loop at {candidate}")
        visited.add(candidate)
        parts = target.split("/") + parts
    return "/".join(resolved)


def resolve_symlink_chain(repo: Path, revision: str, path: str) -> str:
    """Resolve the link at ``path`` through all of its components."""

    target = _read_link(repo, revision, path)
    if target is None:
        return resolve_path(repo, revision, path)
    combined = posixpath.normpath(posixpath.join(posixpath.dirname(path), target))
    if combined in {"", ".", ".."} or combined.startswith("../") or combined.startswith("/"):
        raise GuardError(f"symlink {path} escapes the repository root: {target!r}")
    return resolve_path(repo, revision, combined)


def symlink_errors(
    repo: Path,
    base: str,
    head: str,
    changes: list[Change],
    ownership: OwnershipMap,
    declared: str | None,
) -> list[str]:
    """Reject symlinks that escape or borrow bytes through any component.

    Deletions are allowed: removing a previously unsafe link is a repair.  Every
    proper prefix of a changed path is resolved, so a symlinked intermediate
    component cannot hide an escape.  A link that stays inside the repository is
    still checked against every owner in the map.
    """

    errors: list[str] = []
    for change in changes:
        revision = base if change.status == "D" else head
        parts = change.path.split("/")
        for index in range(1, len(parts)):
            prefix = "/".join(parts[:index])
            try:
                resolved = resolve_path(repo, revision, prefix)
            except GuardError as exc:
                errors.append(str(exc))
                continue
            if resolved != prefix:
                errors.append(
                    f"changed path {change.path} traverses symlink {prefix} -> "
                    f"{resolved}; a symlinked intermediate component is ambiguous "
                    "and must be removed before the change can be reviewed"
                )

    for change in changes:
        if change.status == "D":
            continue
        entry = _tree_entry(repo, head, change.path)
        if entry is None:
            raise GuardError(f"{change.path}: changed path is absent at head")
        mode, _sha = entry
        if mode != "120000":
            continue
        try:
            resolved = resolve_symlink_chain(repo, head, change.path)
        except GuardError as exc:
            errors.append(str(exc))
            continue
        claims = ownership.claims.get(resolved, ())
        if claims:
            allowed = claim_is_allowed(claims, declared)
        else:
            # A directory target can expose files owned by another unit even when
            # the directory itself has no row in the map.  Every claim in the
            # subtree must allow the declaration; mixed ownership is refused.
            prefix = resolved.rstrip("/") + "/"
            subtree = [
                claim
                for path, rows in ownership.claims.items()
                if path.startswith(prefix)
                for claim in rows
            ]
            allowed = all(
                claim_is_allowed((claim,), declared) for claim in subtree
            ) if subtree else True
            claims = tuple(subtree)
        if not allowed:
            owners = ", ".join(
                f"{claim.unit} ({claim.evidence})" for claim in claims
            )
            errors.append(
                f"symlink {change.path} points at {resolved}, owned by {owners}; "
                f"the PR declares {declared or '(none)'}"
            )
    return errors


def derive_marker_unit(repo: Path, head: str, units: set[str]) -> str | None:
    """Return a ``Port-Unit: <unit>`` trailer from the pull request head commit."""

    try:
        message = git(repo, "log", "-1", "--format=%B", head).decode("utf-8", "strict")
    except UnicodeDecodeError as exc:
        raise GuardError("the pull request head commit message is not valid UTF-8") from exc
    found: set[str] = set()
    for line in message.splitlines():
        match = re.fullmatch(r"(?i)port-unit\s*:\s*(\S+)\s*", line)
        if not match:
            continue
        candidate = match.group(1)
        if not UNIT_ID.fullmatch(candidate):
            raise GuardError(
                f"Port-Unit marker names invalid unit id {candidate!r}; "
                "use lowercase letters, digits and hyphens"
            )
        if candidate not in units:
            raise GuardError(
                f"Port-Unit marker names unknown unit {candidate!r}; "
                "add its queue shard or correct the marker"
            )
        found.add(candidate)
    if len(found) > 1:
        raise GuardError(
            "the pull request names more than one unit in its Port-Unit markers: "
            + ", ".join(sorted(found))
        )
    return next(iter(found), None)


def declared_unit(
    repo: Path,
    *,
    head: str,
    units: set[str],
    branch: str | None,
    explicit: str | None,
) -> str | None:
    """Resolve the unit a pull request declares, failing on disagreement."""

    if explicit is not None and not explicit:
        raise GuardError("--unit must not be empty")
    if explicit is not None and not UNIT_ID.fullmatch(explicit):
        raise GuardError(f"--unit {explicit!r} is not a valid unit id")
    branch_unit = derive_branch_unit(branch, units)
    marker_unit = derive_marker_unit(repo, head, units)
    named = [value for value in (explicit, branch_unit, marker_unit) if value is not None]
    if len(set(named)) > 1:
        raise GuardError(
            "the pull request declares conflicting units: "
            + ", ".join(sorted(set(named)))
        )
    if explicit and explicit not in units:
        raise GuardError(
            f"--unit {explicit!r} is not a known unit; add its queue shard first"
        )
    return named[0] if named else None


def derive_branch_unit(branch: str | None, units: set[str]) -> str | None:
    """Return the unit named by a ``fm/<unit>`` (or ``<unit>-...``) branch.

    Unknown branches are process branches.  A branch that names known units
    ambiguously fails closed.
    """

    if not branch:
        return None
    candidate = branch
    for prefix in ("refs/heads/", "refs/remotes/origin/"):
        if candidate.startswith(prefix):
            candidate = candidate[len(prefix):]
    if candidate.startswith("fm/"):
        candidate = candidate[len("fm/"):]
    else:
        candidate = candidate.rsplit("/", 1)[-1]
    if not candidate:
        return None
    matches = [
        unit
        for unit in units
        if candidate == unit or candidate.startswith(f"{unit}-")
    ]
    if not matches:
        return None
    longest = max(len(unit) for unit in matches)
    best = sorted({unit for unit in matches if len(unit) == longest})
    if len(best) > 1:
        raise GuardError(
            f"branch {branch!r} ambiguously names units {', '.join(best)}"
        )
    return best[0]


def read_at(repo: Path, revision: str, path: str) -> bytes | None:
    """Read a file at a commit, returning ``None`` when it does not exist."""

    try:
        git(repo, "cat-file", "-e", f"{revision}:{path}")
    except GuardError:
        return None
    return git(repo, "show", f"{revision}:{path}")


def parse_queue_shard(data: bytes | None, unit: str, label: str) -> list[str] | None:
    if data is None:
        return None
    try:
        text = data.decode("utf-8", "strict")
    except UnicodeDecodeError as exc:
        raise GuardError(f"{label}: not valid UTF-8") from exc
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if not lines:
        raise GuardError(f"{label}: empty queue shard")
    if lines[0] != QUEUE_HEADER:
        raise GuardError(f"{label}:1: expected the exact queue header")
    rows = lines[1:]
    if len(rows) != 1:
        raise GuardError(f"{label}: expected exactly one queue row, got {len(rows)}")
    fields = rows[0].split("\t")
    if len(fields) != len(QUEUE_COLUMNS):
        raise GuardError(
            f"{label}:2: expected {len(QUEUE_COLUMNS)} tab-separated fields, got {len(fields)}"
        )
    if fields[0] != unit:
        raise GuardError(f"{label}:2: queue id {fields[0]!r} does not match unit {unit!r}")
    return fields


def parse_ledger_fragment(data: bytes, unit: str, label: str) -> list[str]:
    """Validate a fragment's ledger shape and return its evidence cells."""

    try:
        text = data.decode("utf-8", "strict")
    except UnicodeDecodeError as exc:
        raise GuardError(f"{label}: not valid UTF-8") from exc
    if "\0" in text:
        raise GuardError(f"{label}: contains a NUL byte")
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if not lines:
        raise GuardError(f"{label}: empty ledger fragment")
    if lines[0] != LEDGER_HEADER:
        raise GuardError(f"{label}:1: expected the exact ledger header")
    rows = lines[1:]
    if not rows:
        raise GuardError(f"{label}: needs at least one ledger evidence row")
    evidence: list[str] = []
    for offset, line in enumerate(rows, start=2):
        if not line:
            raise GuardError(f"{label}:{offset}: blank row in ledger fragment")
        fields = line.split("\t")
        if len(fields) != len(LEDGER_COLUMNS):
            raise GuardError(
                f"{label}:{offset}: expected {len(LEDGER_COLUMNS)} tab-separated "
                f"fields, got {len(fields)}"
            )
        if fields[1] != unit:
            raise GuardError(
                f"{label}:{offset}: ledger unit {fields[1]!r} does not match {unit!r}"
            )
        if not fields[0].strip():
            raise GuardError(f"{label}:{offset}: timestamp is empty")
        evidence.append(fields[5])
    return evidence


def _pr_references(text: str) -> set[int]:
    references: set[int] = set()
    for match in re.finditer(r"(?:pull/|PR\s*#\s*|pr\s*[:=]\s*)(\d+)", text, re.IGNORECASE):
        references.add(int(match.group(1)))
    return references


def _head_references(text: str) -> list[str]:
    found: list[tuple[int, str]] = []
    for match in re.finditer(r"(?<![0-9A-Za-z])([0-9a-fA-F]{40})(?![0-9A-Za-z])", text):
        found.append((match.start(), match.group(1).lower()))
    for match in re.finditer(
        r"\b(?:head|commit)\b\s*(?:commit\s*)?[:=#]?\s*([0-9a-fA-F]{7,39})(?![0-9A-Za-z])",
        text,
        re.IGNORECASE,
    ):
        found.append((match.start(1), match.group(1).lower()))
    ordered: list[str] = []
    seen: set[str] = set()
    for _position, candidate in sorted(found):
        if candidate not in seen:
            seen.add(candidate)
            ordered.append(candidate)
    return ordered


def _run_references(text: str) -> list[tuple[str, str | None]]:
    references: list[tuple[str, str | None]] = []
    for match in re.finditer(
        r"https?://(?:www\.)?github\.com/([^/\s]+)/([^/\s]+)/actions/runs/(\d+)",
        text,
        re.IGNORECASE,
    ):
        slug = f"{match.group(1)}/{match.group(2)}".lower()
        references.append((match.group(3), slug))
    for match in re.finditer(
        r"(?:actions/runs/|(?:ci[_-]?run|run)\s*[:=#]?\s*)(\d{5,})(?![0-9])",
        text,
        re.IGNORECASE,
    ):
        run_id = match.group(1)
        if all(existing != run_id for existing, _ in references):
            references.append((run_id, None))
    return references


def _is_ancestor(repo: Path, ancestor: str, descendant: str) -> bool:
    try:
        git(repo, "merge-base", "--is-ancestor", ancestor, descendant)
    except GuardError:
        return False
    return True


def _pr_commits(repo: Path, merge_base: str, base: str, head: str) -> set[str]:
    """Commits on the pull request's own first-parent spine after the fork.

    Main commits merged into the branch sit on a second-parent chain, and every
    commit reachable from the merge base or the event base is excluded, so a
    rebased or fast-forwarded branch cannot cite a main commit's real run.
    """

    output = git(repo, "rev-list", "--first-parent", head, f"^{merge_base}", f"^{base}")
    return {
        line.strip()
        for line in output.decode("ascii", "strict").splitlines()
        if re.fullmatch(r"[0-9a-f]{40}", line.strip())
    }


def validate_fragment_evidence(
    evidence: list[str],
    *,
    unit: str,
    pr_number: int,
    repo: Path,
    base: str,
    head: str,
    merge_base: str,
    repo_slug: str | None,
    verify_ci: bool,
    merge_commit: str | None = None,
) -> str:
    """Check the fragment names this PR, a commit it produced, and its CI run.

    A cited head must be the pull request head, a commit on the pull request
    side of the merge base, or the merge commit the fragment describes.  Any
    other ancestor -- including an older commit from the base branch -- is an
    unrelated run and cannot certify this change.
    """

    text = "\n".join(evidence)
    prs = _pr_references(text)
    if not prs:
        raise GuardError(
            f"fragment for {unit} does not name the pull request; "
            f"expected PR #{pr_number} or .../pull/{pr_number}"
        )
    if pr_number not in prs:
        named = ", ".join(f"#{item}" for item in sorted(prs))
        raise GuardError(
            f"fragment for {unit} names PR {named}, not the current PR #{pr_number}"
        )

    head_candidates = _head_references(text)
    if not head_candidates:
        raise GuardError(
            f"fragment for {unit} does not name the head commit; "
            "expected a 40-hex commit id or 'head <sha>'"
        )
    valid_heads: list[str] = []
    unresolved: list[str] = []
    saw_merge_base = False
    pr_commits = _pr_commits(repo, merge_base, base, head)
    for candidate in head_candidates:
        try:
            resolved = resolve_commit(repo, candidate, "fragment head")
        except GuardError:
            unresolved.append(candidate)
            continue
        if resolved == merge_base:
            saw_merge_base = True
            continue
        produced = resolved in pr_commits
        if not produced and merge_commit is not None and resolved == merge_commit:
            produced = True
        if produced and resolved not in valid_heads:
            valid_heads.append(resolved)
    if unresolved:
        raise GuardError(
            f"fragment for {unit} names head {', '.join(unresolved)}, which does "
            "not resolve to a commit in this repository; it is not the pull request "
            f"head or a commit produced by this pull request (merge base "
            f"{merge_base}), so it cannot certify this change"
        )
    if not valid_heads:
        if saw_merge_base:
            raise GuardError(
                f"fragment for {unit} names the merge base {merge_base} as its head; "
                "the evidence must name a commit produced by this pull request"
            )
        raise GuardError(
            f"fragment for {unit} names head {', '.join(head_candidates)}, "
            f"which is not the pull request head or a commit produced by this pull "
            f"request (merge base {merge_base}); an unrelated ancestor's run cannot "
            "certify this change"
        )
    claimed_head = valid_heads[0]

    runs = _run_references(text)
    if not runs:
        raise GuardError(
            f"fragment for {unit} does not name the linux-ci run it relies on; "
            "expected .../actions/runs/<id> or ci_run=<id>"
        )

    expected_slug = (repo_slug or "").lower()
    candidates: list[tuple[str, str | None]] = []
    mismatched: list[str] = []
    seen_runs: set[str] = set()
    for run_id, slug in runs:
        if expected_slug and slug and slug != expected_slug:
            mismatched.append(f"{slug}/actions/runs/{run_id}")
            continue
        if run_id not in seen_runs:
            seen_runs.add(run_id)
            candidates.append((run_id, slug))
    if mismatched:
        raise GuardError(
            f"fragment for {unit} cites CI runs outside {expected_slug}: "
            f"{', '.join(mismatched)}"
        )
    if not candidates:
        raise GuardError(
            f"fragment for {unit} cites no linux-ci run in {expected_slug or 'this repository'}"
        )

    if not verify_ci:
        return f"head {claimed_head}, run {candidates[0][0]}"

    cited = frozenset(valid_heads)
    reasons: list[str] = []
    verified: list[str] = []
    for run_id, _slug in candidates:
        try:
            verify_ci_run(expected_slug, run_id, cited)
        except GuardError as exc:
            reasons.append(str(exc))
            continue
        verified.append(run_id)
    if reasons:
        raise GuardError(
            f"fragment for {unit} cites a CI run that does not prove its head: "
            + "; ".join(reasons)
        )
    return f"head {claimed_head}, run {', '.join(verified)}"


def _github_api_base() -> str:
    """Return the Actions API base; ``GITHUB_API_URL`` is the Actions default."""

    base = os.environ.get("GITHUB_API_URL", "https://api.github.com").strip().rstrip("/")
    parsed = urllib.parse.urlparse(base)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise GuardError(f"GITHUB_API_URL must be an http(s) URL, got {base!r}")
    return base


def verify_ci_run(repo_slug: str, run_id: str, expected_heads: frozenset[str]) -> str:
    """Verify a run through the read-only GitHub API, failing closed.

    The run must exist in ``repo_slug``, be a completed successful ``linux-ci``
    run, and report a ``head_sha`` equal to one of the heads the fragment cites.
    A 404, a foreign repository, a pending or failed run, an API error or an
    unreachable service is an error, never a skipped or cached success.
    """

    if not repo_slug:
        raise GuardError("cannot verify a CI run without a repository slug")
    if not expected_heads:
        raise GuardError(f"cannot verify run {run_id} without a cited head")
    url = f"{_github_api_base()}/repos/{repo_slug}/actions/runs/{run_id}"
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "playnite-linux-port-unit-guard",
    }
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            payload = json.load(response)
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            raise GuardError(
                f"run {run_id} does not exist in {repo_slug} (GitHub API HTTP 404)"
            ) from exc
        raise GuardError(
            f"cannot verify run {run_id} in {repo_slug}: GitHub API HTTP {exc.code}"
        ) from exc
    except (urllib.error.URLError, TimeoutError, OSError, ValueError) as exc:
        raise GuardError(
            f"cannot verify run {run_id} in {repo_slug}: the GitHub API is "
            f"unreachable or returned invalid JSON ({exc})"
        ) from exc

    if not isinstance(payload, dict):
        raise GuardError(f"run {run_id} returned an unexpected API payload")
    head = str(payload.get("head_sha", "")).strip().lower()
    if not re.fullmatch(r"[0-9a-f]{40}", head):
        raise GuardError(
            f"run {run_id} reported no full head_sha; cannot prove the commit it ran on"
        )
    if head not in expected_heads:
        cited = ", ".join(sorted(expected_heads))
        raise GuardError(
            f"run {run_id} was executed on head {head}, not the cited head "
            f"({cited}); the fragment's run id and head SHA must agree with the API"
        )
    if payload.get("status") != "completed":
        raise GuardError(f"run {run_id} is not completed")
    if payload.get("conclusion") != "success":
        raise GuardError(
            f"run {run_id} concluded {payload.get('conclusion')!r}, not 'success'"
        )
    name = str(payload.get("name", ""))
    path = str(payload.get("path", ""))
    if name != "linux-ci" and "linux-ci" not in path:
        raise GuardError(f"run {run_id} is not the linux-ci workflow")
    repository = payload.get("repository")
    full_name = ""
    if isinstance(repository, dict):
        full_name = str(repository.get("full_name") or "").strip().lower()
    if not full_name:
        raise GuardError(
            f"run {run_id} did not report its repository; cannot prove it belongs "
            f"to {repo_slug}"
        )
    if full_name != repo_slug:
        raise GuardError(
            f"run {run_id} belongs to {full_name}, not {repo_slug}"
        )
    return head


def repository_slug(repo: Path, explicit: str | None) -> str | None:
    if explicit:
        if not re.fullmatch(r"[^/\s]+/[^/\s]+", explicit):
            raise GuardError(f"repository slug must be owner/name, got {explicit!r}")
        return explicit.lower()
    environment = os.environ.get("GITHUB_REPOSITORY")
    if environment:
        return environment.lower()
    try:
        remote = git(repo, "remote", "get-url", "origin").decode("utf-8", "strict").strip()
    except GuardError:
        return None
    parsed = urllib.parse.urlparse(remote)
    if parsed.scheme in {"http", "https"} and parsed.hostname == "github.com":
        path = parsed.path.strip("/")
        if path.endswith(".git"):
            path = path[:-4]
        if re.fullmatch(r"[^/\s]+/[^/\s]+", path):
            return path.lower()
    match = re.fullmatch(r"git@github\.com:([^/\s]+/[^/\s]+?)(?:\.git)?", remote)
    if match:
        return match.group(1).lower()
    return None


def format_paths(paths: list[str]) -> str:
    return "\n".join(f"  {path}" for path in paths)
