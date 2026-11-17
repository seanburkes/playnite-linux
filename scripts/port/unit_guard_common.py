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
``path<TAB>unit<TAB>commit<TAB>pull``.  It extends ownership beyond evidence to
product source, shared tests, non-prefixed fixtures and tools: a path is owned by
the unit whose merged commit most recently introduced or changed it.  The guard
reads the map from the pull request's base commit, never its head, and fails a
change to a path owned by another unit or any unit-owned path in a PR that
declares no unit.  A path the map does not list stays shared.

Ledger fragments
----------------
``docs/port/units/<unit>.ledger.d/<pr>-<slug>.tsv`` is a ledger shard: the
normal ledger header followed by one or more rows whose ``unit`` column is the
owning unit.  Its evidence column must name the pull request, a head commit
produced by this pull request and the real linux-ci run whose result it relies
on.  The guard accepts the historical free-text forms (``PR #97``, ``pull/97``,
``head 2e09983...`` and ``actions/runs/43964833995``).  The cited head must be
on the pull request side of the merge base (or the merge commit, when supplied);
an unrelated base-branch ancestor is rejected rather than treated as evidence.

Diff paths are repository-relative with no traversal component.  Renames are
expanded to an explicit delete plus add, deletions are checked like any other
change, and added or modified symlinks must resolve inside the repository and
must not borrow another unit's owned bytes.

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
FRAGMENT_NAME = re.compile(r"^(\d+)-([a-z0-9][a-z0-9-]*)\.tsv$")

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
            "ledger-corrections.tsv, <unit>.queue.tsv, <unit>.ledger.tsv and "
            "<unit>.ledger.d/<pr>-<slug>.tsv"
        )

    if path.startswith("docs/port/"):
        relative = path[len("docs/port/"):]
        if "/" not in relative and relative.endswith(".md"):
            return _one_unit(path, [unit for unit in units if relative.startswith(f"{unit}-")])
        return frozenset(), None

    for prefix in ("tests/golden/", "tests/fixtures/"):
        if path.startswith(prefix):
            relative = path[len(prefix):]
            return _one_unit(path, [unit for unit in units if relative.startswith(f"{unit}-")])

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


def bot_owned_errors(changes: list[Change]) -> list[str]:
    """Reject PR edits to the generated ownership map, even to identical bytes."""

    return [
        f"{change.path} is bot-owned and generated on main; a pull request must "
        "not add, edit, rename or delete the ownership map"
        for change in changes
        if change.path == OWNERSHIP_MAP
    ]


def ownership_map_exists(repo: Path, revision: str) -> bool:
    return read_at(repo, revision, OWNERSHIP_MAP) is not None


def load_ownership_map(repo: Path, revision: str) -> dict[str, OwnershipClaim]:
    """Read and validate the bot-generated map at a base revision.

    The map is deliberately read from the pull request's base, never from its
    head: a branch cannot manufacture ownership for itself.  A missing map is
    the bootstrap state before the post-merge generator first writes it.
    """

    data = read_at(repo, revision, OWNERSHIP_MAP)
    if data is None:
        return {}
    try:
        text = data.decode("utf-8", "strict")
    except UnicodeDecodeError as exc:
        raise GuardError(f"{OWNERSHIP_MAP}: not valid UTF-8") from exc
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if not lines:
        raise GuardError(f"{OWNERSHIP_MAP}: empty ownership map")
    if lines[0] != OWNERSHIP_HEADER:
        raise GuardError(f"{OWNERSHIP_MAP}:1: expected the exact ownership header")
    claims: dict[str, OwnershipClaim] = {}
    previous = ""
    for offset, line in enumerate(lines[1:], start=2):
        if not line:
            raise GuardError(f"{OWNERSHIP_MAP}:{offset}: blank row")
        fields = line.split("\t")
        if len(fields) != 4:
            raise GuardError(
                f"{OWNERSHIP_MAP}:{offset}: expected 4 tab-separated fields, "
                f"got {len(fields)}"
            )
        path, unit, commit, pull = fields
        error = path_error(path)
        if error:
            raise GuardError(f"{OWNERSHIP_MAP}:{offset}: {path!r}: {error}")
        if not UNIT_ID.fullmatch(unit):
            raise GuardError(f"{OWNERSHIP_MAP}:{offset}: invalid unit id {unit!r}")
        if not re.fullmatch(r"[0-9a-f]{40}", commit):
            raise GuardError(
                f"{OWNERSHIP_MAP}:{offset}: commit must be a full 40-hex id, got {commit!r}"
            )
        if pull != "-" and not OWNERSHIP_PULL.fullmatch(pull):
            raise GuardError(
                f"{OWNERSHIP_MAP}:{offset}: pull must be 'pull/<number>' or '-', got {pull!r}"
            )
        if path in claims:
            first = claims[path]
            raise GuardError(
                f"{OWNERSHIP_MAP}:{offset}: ambiguous ownership for {path}: "
                f"already claimed by {first.unit} ({first.evidence}), now {unit} "
                f"({pull if pull != '-' else commit}); refusing to guess"
            )
        if previous and path < previous:
            raise GuardError(
                f"{OWNERSHIP_MAP}:{offset}: rows must be sorted by path; "
                f"{path!r} follows {previous!r}"
            )
        previous = path
        claims[path] = OwnershipClaim(path, unit, commit, pull)
    return claims


def ownership_claim_for(
    path: str, claims: dict[str, OwnershipClaim]
) -> OwnershipClaim | None:
    return claims.get(path)


def ownership_hits(
    changes: list[Change], claims: dict[str, OwnershipClaim]
) -> dict[str, OwnershipClaim]:
    """Map each changed path that the base map attributes to its owner."""

    return {
        change.path: claims[change.path]
        for change in changes
        if change.path in claims
    }


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


def _resolve_symlink(path: str, target: str) -> str:
    if not target:
        raise GuardError(f"symlink {path} has an empty target")
    if target.startswith("/") or target.startswith("\\") or re.match(r"^[A-Za-z]:", target):
        raise GuardError(
            f"symlink {path} points outside the repository: {target!r}"
        )
    joined = posixpath.normpath(posixpath.join(posixpath.dirname(path), target))
    if joined in {"", ".", ".."} or joined.startswith("../") or joined.startswith("/"):
        raise GuardError(
            f"symlink {path} escapes the repository root: {target!r}"
        )
    return joined


def _resolve_symlink_chain(repo: Path, head: str, path: str, target: str) -> str:
    """Follow in-repository symlink targets and reject chains that escape or loop."""

    resolved = _resolve_symlink(path, target)
    seen = {path}
    for _depth in range(16):
        if resolved in seen:
            raise GuardError(f"symlink {path} resolves through a loop at {resolved}")
        seen.add(resolved)
        entry = _tree_entry(repo, head, resolved)
        if entry is None or entry[0] != "120000":
            return resolved
        data = git(repo, "cat-file", "blob", entry[1])
        try:
            next_target = data.decode("utf-8", "strict")
        except UnicodeDecodeError as exc:
            raise GuardError(f"symlink {resolved} has a non-UTF-8 target") from exc
        if "\0" in next_target or "\n" in next_target or "\r" in next_target:
            raise GuardError(f"symlink {resolved} has a control character in its target")
        resolved = _resolve_symlink(resolved, next_target)
    raise GuardError(f"symlink {path} has too many chained targets to resolve safely")


def symlink_errors(
    repo: Path,
    base: str,
    head: str,
    changes: list[Change],
    claims: dict[str, OwnershipClaim],
    declared: str | None,
) -> list[str]:
    """Reject symlinks in added or modified paths that escape or borrow bytes.

    Deletions are allowed: removing a previously unsafe link is a repair.  A
    link that stays inside the repository is still checked against the ownership
    map, so a unit's own namespace cannot point at another unit's bytes.
    """

    errors: list[str] = []
    for change in changes:
        if change.status == "D":
            continue
        entry = _tree_entry(repo, head, change.path)
        if entry is None:
            raise GuardError(f"{change.path}: changed path is absent at head")
        mode, sha = entry
        if mode != "120000":
            continue
        data = git(repo, "cat-file", "blob", sha)
        try:
            target = data.decode("utf-8", "strict")
        except UnicodeDecodeError as exc:
            raise GuardError(f"symlink {change.path} has a non-UTF-8 target") from exc
        if "\0" in target or "\n" in target or "\r" in target:
            raise GuardError(f"symlink {change.path} has a control character in its target")
        try:
            resolved = _resolve_symlink_chain(repo, head, change.path, target)
        except GuardError as exc:
            errors.append(str(exc))
            continue
        claim = claims.get(resolved)
        if claim is not None and claim.unit != declared:
            errors.append(
                f"symlink {change.path} points at {resolved}, owned by unit "
                f"{claim.unit} ({claim.evidence}); the PR declares {declared or '(none)'}"
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
    saw_merge_base = False
    for candidate in head_candidates:
        try:
            resolved = resolve_commit(repo, candidate, "fragment head")
        except GuardError:
            continue
        if resolved == merge_base:
            saw_merge_base = True
            continue
        produced = resolved == head
        if not produced and merge_commit is not None and resolved == merge_commit:
            produced = True
        if not produced:
            # A PR-side commit is reachable from the head but not from the base
            # tip.  Reachability from the merge base alone would accept a
            # base-branch commit merely merged into the branch.
            produced = (
                _is_ancestor(repo, resolved, head)
                and not _is_ancestor(repo, resolved, base)
            )
        if produced:
            valid_heads.append(resolved)
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
    for run_id, slug in runs:
        if expected_slug and slug and slug != expected_slug:
            mismatched.append(f"{slug}/actions/runs/{run_id}")
            continue
        candidates.append((run_id, slug))
    if not candidates:
        raise GuardError(
            f"fragment for {unit} cites CI runs outside {expected_slug}: "
            f"{', '.join(mismatched)}"
        )

    if not verify_ci:
        return f"head {claimed_head}, run {candidates[0][0]}"

    reasons: list[str] = []
    for run_id, _slug in candidates:
        try:
            verify_ci_run(expected_slug, run_id, claimed_head)
        except GuardError as exc:
            reasons.append(str(exc))
            continue
        return f"head {claimed_head}, run {run_id}"
    raise GuardError(
        f"fragment for {unit} cites no real successful linux-ci run for its head: "
        + "; ".join(reasons)
    )


def verify_ci_run(repo_slug: str, run_id: str, expected_head: str) -> None:
    """Verify a linux-ci run through the read-only GitHub API, failing closed."""

    if not repo_slug:
        raise GuardError("cannot verify a CI run without a repository slug")
    url = f"https://api.github.com/repos/{repo_slug}/actions/runs/{run_id}"
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
        raise GuardError(f"GitHub API rejected run {run_id}: HTTP {exc.code}") from exc
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        raise GuardError(f"cannot verify run {run_id}: {exc}") from exc

    head = str(payload.get("head_sha", "")).lower()
    if head != expected_head:
        raise GuardError(
            f"run {run_id} was executed on {head or 'an unknown commit'}, "
            f"not the fragment head {expected_head}"
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
    repository = str(payload.get("repository", {}).get("full_name", "")).lower()
    if repository and repository != repo_slug:
        raise GuardError(
            f"run {run_id} belongs to {repository}, not {repo_slug}"
        )


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
