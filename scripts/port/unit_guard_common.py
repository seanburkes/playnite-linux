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
``docs/port/ledger.tsv``), ``tests/baseline.json`` and generated status content
are never unit evidence and are never PR-editable.

Ledger fragments
----------------
``docs/port/units/<unit>.ledger.d/<pr>-<slug>.tsv`` is a ledger shard: the
normal ledger header followed by one or more rows whose ``unit`` column is the
owning unit.  Its evidence column must name the pull request, the validated
head commit and the real linux-ci run whose result it relies on.  The guard
accepts the historical free-text forms (``PR #97``, ``pull/97``,
``head 2e09983...`` and ``actions/runs/43964833995``); the head may be an
ancestor of the pull-request head because a fragment cannot name its own commit
before that commit exists.

This module deliberately knows nothing about the aggregate generator.  It uses
git plumbing only: no checkout, fetch, reset, or write occurs in the worktree.
"""

from __future__ import annotations

import json
import os
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
UNITS_NON_EVIDENCE = frozenset({"README.md", "removed.tsv", "ledger-corrections.tsv"})
QUEUE_SUFFIX = ".queue.tsv"
# Non-prefixed paths can still be owned by a unit when its queue row names them
# in the oracle/test columns. Only these roots are considered: docs/ and legacy/
# are shared or frozen, and a token there would over-claim sibling files.
DECLARED_OWNERSHIP_ROOTS = ("tests/golden/", "tests/fixtures/", "tools/")
FILE_SUFFIXES = (
    ".cs", ".csproj", ".json", ".md", ".props", ".txt", ".yaml", ".yml",
    ".sh", ".py", ".ini", ".toml", ".xml",
)



class GuardError(Exception):
    """A deterministic policy or input error.  The caller exits non-zero."""


@dataclass(frozen=True)
class Change:
    status: str
    path: str


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


def changed_paths(repo: Path, base: str, head: str) -> list[Change]:
    """Return name-status changes between two commits.

    Rename detection is deliberately disabled: a rename is a delete plus an add,
    so both the old and new unit namespaces are charged.  A rename status in the
    output therefore means git was not asked as expected and the diff is
    ambiguous.
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
    if len(parts) % 2 != 0:
        raise GuardError("git diff returned an incomplete NUL-separated name-status record")
    changes: list[Change] = []
    for index in range(0, len(parts), 2):
        try:
            status = parts[index].decode("ascii", "strict")
            path = parts[index + 1].decode("utf-8", "strict")
        except UnicodeDecodeError as exc:
            raise GuardError("git diff contains a non-UTF-8 path; refusing an ambiguous diff") from exc
        if status not in {"A", "C", "D", "M", "R", "T", "U", "X", "B"} or status in {"R", "C"}:
            raise GuardError(f"unsupported or ambiguous git diff status {status!r} for {path!r}")
        if (
            not path
            or path.startswith("/")
            or "\0" in path
            or "\n" in path
            or "\r" in path
        ):
            raise GuardError(f"invalid path in git diff: {path!r}")
        changes.append(Change(status, path))
    changes.sort(key=lambda item: item.path)
    if len({item.path for item in changes}) != len(changes):
        raise GuardError("git diff lists the same path more than once; refusing an ambiguous diff")
    return changes


def paths_from_diff_file(path: Path) -> list[Change]:
    """Read a simple one-path-per-line diff for offline ownership checks."""

    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        raise GuardError(f"cannot read diff file {path}: {exc}") from exc
    paths = text.splitlines()
    if not paths:
        raise GuardError(f"diff file {path} contains no paths")
    if any(not item.strip() for item in paths):
        raise GuardError(f"diff file {path} contains a blank path; refusing an ambiguous diff")
    result: list[Change] = []
    for item in paths:
        if item.startswith("/") or "\0" in item or "\n" in item or "\r" in item:
            raise GuardError(f"invalid path in diff file {path}: {item!r}")
        result.append(Change("M", item))
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


def _declared_prefix(token: str) -> str | None:
    """Turn one oracle/test token into a path prefix under a unit-owned root."""
    token = token.strip().strip("`'\"")
    if not token:
        return None
    lowered = token.lower()
    for prefix in ("golden:", "test:", "oracle:", "fixture:", "legacy:"):
        if lowered.startswith(prefix):
            token = token[len(prefix):].strip()
            break
    if not token.startswith(DECLARED_OWNERSHIP_ROOTS):
        return None
    token = token.rstrip(".,;")
    if "{" in token:
        token = token[: token.index("{")]
    if "*" in token:
        token = token[: token.index("*")]
    token = token.rstrip("/")
    if not token or token + "/" in DECLARED_OWNERSHIP_ROOTS:
        return None
    last = token.rsplit("/", 1)[-1]
    if "." in last and last.lower().endswith(FILE_SUFFIXES):
        parent = token.rsplit("/", 1)[0] if "/" in token else ""
        return parent + "/" if parent else None
    return token + "/"


def declared_path_prefixes(repo: Path, revisions: list[str | None]) -> dict[str, set[str]]:
    """Collect non-prefixed unit assets declared by each unit's queue row.

    The queue row's ``oracle`` and ``test`` columns are the contract that names
    unit-owned fixtures and tools whose paths do not carry the unit prefix. A
    path that two units declare is left to ``_one_unit`` to fail as ambiguous.
    """
    declared: dict[str, set[str]] = {}
    for revision in revisions:
        if revision is None:
            continue
        try:
            names = _tree_paths(repo, revision, "docs/port/units")
        except GuardError:
            continue
        for name in names:
            if not name.startswith(UNITS_DIR) or not name.endswith(QUEUE_SUFFIX):
                continue
            relative = name[len(UNITS_DIR):]
            if "/" in relative:
                continue
            unit = relative[: -len(QUEUE_SUFFIX)]
            if not UNIT_ID.fullmatch(unit):
                continue
            data = read_at(repo, revision, name)
            if data is None:
                continue
            fields = parse_queue_shard(data, unit, name)
            for column in ("oracle", "test"):
                value = fields[QUEUE_COLUMNS.index(column)]
                for token in re.split(r"[;\s]+", value):
                    prefix = _declared_prefix(token)
                    if prefix:
                        declared.setdefault(unit, set()).add(prefix)
    # A prefix two units claim is not an exclusive owner. Keep it shared rather
    # than failing every unrelated PR that touches the tool or golden root.
    owners: dict[str, set[str]] = {}
    for unit, prefixes in declared.items():
        for prefix in prefixes:
            owners.setdefault(prefix, set()).add(unit)
    return {
        unit: {prefix for prefix in prefixes if len(owners[prefix]) == 1}
        for unit, prefixes in declared.items()
    }


def attribute_path(path: str, units: set[str],
                   declared: dict[str, set[str]] | None = None
                   ) -> tuple[frozenset[str], str | None]:
    """Map one changed path to the unit namespace it belongs to.

    An unmatched path is shared/process material.  A path that could belong to
    more than one unit, or is an unrecognised entry under ``docs/port/units``,
    is ambiguous and fails closed. A non-prefixed path named by exactly one
    unit's queue oracle/test columns is attributed to that unit.
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

    matches: list[str] = []
    for prefix in ("tests/golden/", "tests/fixtures/"):
        if path.startswith(prefix):
            relative = path[len(prefix):]
            matches.extend(unit for unit in units if relative.startswith(f"{unit}-"))
    if path.startswith("tools/"):
        relative = path[len("tools/"):]
        for unit in units:
            if relative == unit:
                matches.append(unit)
            elif relative.startswith(unit) and relative[len(unit)] in "-._/":
                matches.append(unit)
    if not matches and declared:
        for unit, prefixes in declared.items():
            if any(path.startswith(prefix) for prefix in prefixes):
                matches.append(unit)
    if path.startswith(DECLARED_OWNERSHIP_ROOTS):
        return _one_unit(path, matches)

    return frozenset(), None


def touched_units(
    changes: list[Change], units: set[str], expected: str | None,
    declared: dict[str, set[str]] | None = None,
) -> dict[str, list[str]]:
    """Group changed paths by unit namespace, rejecting ambiguous paths."""

    known = set(units)
    if expected:
        known.add(expected)
    touched: dict[str, list[str]] = {}
    errors: list[str] = []
    for change in changes:
        attributed, error = attribute_path(change.path, known, declared)
        if error:
            errors.append(error)
            continue
        for unit in attributed:
            touched.setdefault(unit, []).append(change.path)
    if errors:
        raise GuardError("\n".join(errors))
    return {unit: sorted(paths) for unit, paths in sorted(touched.items())}


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


def validate_fragment_evidence(
    evidence: list[str],
    *,
    unit: str,
    pr_number: int,
    repo: Path,
    head: str,
    merge_base: str,
    repo_slug: str | None,
    verify_ci: bool,
) -> str:
    """Check the fragment names this PR, a produced head commit, and its CI run."""

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
    for candidate in head_candidates:
        try:
            resolved = resolve_commit(repo, candidate, "fragment head")
        except GuardError:
            continue
        try:
            git(repo, "merge-base", "--is-ancestor", resolved, head)
        except GuardError:
            continue
        valid_heads.append(resolved)
    if not valid_heads:
        raise GuardError(
            f"fragment for {unit} names head {', '.join(head_candidates)}, "
            f"which is not an ancestor of PR head {head}"
        )
    claimed_head = valid_heads[0]
    if claimed_head == merge_base:
        raise GuardError(
            f"fragment for {unit} names the merge base {merge_base} as its head; "
            "the evidence must name a commit produced by this pull request"
        )

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
