#!/usr/bin/env python3
"""Require a unit pull request to carry its own queue transition and evidence.

Usage:
    python3 scripts/port/check_unit_evidence.py --base <sha> --head <sha>
        --pr <number> [--branch <head-ref>] [--unit <unit>] [--verify-ci]

A unit PR must, in the diff from the merge base to the PR head:

1. modify ``docs/port/units/<unit>.queue.tsv`` to a real state (not the
   untouched ``todo`` backlog state), and
2. add or update exactly one ``docs/port/units/<unit>.ledger.d/<pr>-<slug>.tsv``
   ledger fragment whose evidence names this PR, its validated head commit and
   the real linux-ci run it relies on.

A process PR that touches no unit evidence needs neither.  The check never
writes, never flips state, and fails closed whenever the owning unit is
ambiguous.  ``--verify-ci`` additionally checks the cited run through the
read-only GitHub API (the workflow enables it).

Exit codes: 0 allowed, 1 policy or ambiguity failure.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from unit_guard_common import (  # noqa: E402
    FRAGMENT_NAME,
    QUEUE_COLUMNS,
    REAL_QUEUE_STATES,
    GuardError,
    bot_owned_errors,
    changed_paths,
    declared_unit,
    known_units,
    load_ownership_map,
    merge_base,
    ownership_hits,
    ownership_map_exists,
    parse_ledger_fragment,
    parse_queue_shard,
    read_at,
    repo_root,
    repository_slug,
    resolve_commit,
    symlink_errors,
    touched_units,
    validate_fragment_evidence,
)

STATUS_COLUMN = QUEUE_COLUMNS.index("status")
QUEUE_SUFFIX = ".queue.tsv"
FRAGMENT_DIR = ".ledger.d/"


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", help="repository checkout (defaults to this script's checkout)")
    parser.add_argument("--base", required=True, help="pull-request base commit")
    parser.add_argument("--head", required=True, help="pull-request head commit")
    parser.add_argument("--merge-base", help="use this merge base instead of computing one")
    parser.add_argument("--pr", required=True, type=int, help="pull-request number")
    parser.add_argument("--unit", help="owning unit asserted by the caller")
    parser.add_argument("--branch", help="pull-request head ref, for example fm/p4-003")
    parser.add_argument("--repo-slug", help="owner/name used to validate cited CI runs")
    parser.add_argument(
        "--merge-commit",
        help="merged commit a fragment may describe instead of a pre-merge head",
    )
    parser.add_argument(
        "--verify-ci",
        action="store_true",
        help="verify the cited run through the read-only GitHub API",
    )
    return parser.parse_args(argv)


def _queue_path(unit: str) -> str:
    return f"docs/port/units/{unit}{QUEUE_SUFFIX}"


def _check_queue(
    repo: Path,
    base: str,
    head: str,
    unit: str,
    changes,
) -> str:
    path = _queue_path(unit)
    change = next((item for item in changes if item.path == path), None)
    if change is None:
        raise GuardError(
            f"missing required queue transition: {path} was not changed"
        )
    if change.status == "D":
        raise GuardError(
            f"missing required queue transition: {path} was deleted"
        )
    base_fields = parse_queue_shard(read_at(repo, base, path), unit, f"{path} (base)")
    head_fields = parse_queue_shard(read_at(repo, head, path), unit, f"{path} (head)")
    if head_fields is None:
        raise GuardError(f"missing required queue row: {path} is absent at head")
    status = head_fields[STATUS_COLUMN]
    if status not in REAL_QUEUE_STATES:
        allowed = ", ".join(sorted(REAL_QUEUE_STATES))
        raise GuardError(
            f"queue row {path} is {status!r}; a unit PR must move it to a real "
            f"evidence state ({allowed})"
        )
    if base_fields is not None and head_fields == base_fields:
        raise GuardError(
            f"queue row {path} was not modified; a unit PR must record its own "
            "transition instead of leaving the row untouched"
        )
    return f"{path} status {status}"


def _check_fragment(
    repo: Path,
    base: str,
    head: str,
    merge: str,
    unit: str,
    pr_number: int,
    changes,
    *,
    repo_slug: str | None,
    verify_ci: bool,
    merge_commit: str | None,
) -> str:
    prefix = f"docs/port/units/{unit}{FRAGMENT_DIR}"
    fragments = [item for item in changes if item.path.startswith(prefix)]
    if not fragments:
        raise GuardError(
            f"missing required ledger fragment: no "
            f"{prefix}{pr_number}-<slug>.tsv was added or updated"
        )
    if len(fragments) > 1:
        names = ", ".join(item.path for item in fragments)
        raise GuardError(
            f"a unit PR carries exactly one ledger fragment per unit; found "
            f"{len(fragments)}: {names}"
        )
    change = fragments[0]
    if change.status not in {"A", "M"}:
        raise GuardError(
            f"ledger fragment {change.path} must be added or updated, not "
            f"{change.status}"
        )
    name = change.path[len(prefix):]
    match = FRAGMENT_NAME.fullmatch(name)
    if match is None:
        raise GuardError(
            f"ledger fragment {change.path} must be named "
            f"{pr_number}-<short-slug>.tsv"
        )
    named_pr = int(match.group(1))
    if named_pr != pr_number:
        raise GuardError(
            f"ledger fragment {change.path} names PR #{named_pr}, "
            f"not the current PR #{pr_number}"
        )
    data = read_at(repo, head, change.path)
    if data is None:
        raise GuardError(f"ledger fragment {change.path} is absent at head")
    evidence = parse_ledger_fragment(data, unit, change.path)
    detail = validate_fragment_evidence(
        evidence,
        unit=unit,
        pr_number=pr_number,
        repo=repo,
        base=base,
        head=head,
        merge_base=merge,
        repo_slug=repo_slug,
        verify_ci=verify_ci,
        merge_commit=merge_commit,
    )
    return f"{change.path} ({detail})"


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    try:
        if args.pr <= 0:
            raise GuardError(f"--pr must be a positive pull-request number, got {args.pr}")
        repo = repo_root(args.repo)
        base = resolve_commit(repo, args.base, "base")
        head = resolve_commit(repo, args.head, "head")
        merge = (
            resolve_commit(repo, args.merge_base, "merge base")
            if args.merge_base
            else merge_base(repo, base, head)
        )
        changes = changed_paths(repo, merge, head)
        units = known_units(repo, [base, head, merge])

        map_errors = bot_owned_errors(changes)
        if map_errors:
            raise GuardError("\n".join(map_errors))

        expected = declared_unit(
            repo, head=head, units=units, branch=args.branch, explicit=args.unit
        )
        claims = (
            load_ownership_map(repo, base)
            if ownership_map_exists(repo, base)
            else load_ownership_map(repo, merge)
        )
        merge_commit = (
            resolve_commit(repo, args.merge_commit, "merge commit")
            if args.merge_commit
            else None
        )
        link_errors = symlink_errors(repo, base, head, changes, claims, expected)
        if link_errors:
            raise GuardError("\n".join(link_errors))

        touched = touched_units(changes, units, expected)
        owned = ownership_hits(changes, claims)
        if expected is None:
            if touched or owned:
                details = "\n".join(
                    f"  {path} (owned by {claim.unit}; {claim.evidence})"
                    for path, claim in sorted(owned.items())
                )
                raise GuardError(
                    "the pull request changes unit-owned material but declares no "
                    "unit; name the branch fm/<unit>, add a 'Port-Unit: <unit>' "
                    "head-commit trailer, or pass --unit"
                    + (f":\n{details}" if details else
                       f"; touched unit evidence: {', '.join(sorted(touched))}")
                )
            print("unit: (none)")
            print("process PR: no unit-owned or unit evidence path is touched")
            return 0

        if not touched:
            details = "\n".join(
                f"  {path} (owned by {claim.unit}; {claim.evidence})"
                for path, claim in sorted(owned.items())
            )
            raise GuardError(
                f"unit PR {expected} carries no unit evidence: "
                f"missing required queue transition {_queue_path(expected)} and "
                f"missing required ledger fragment "
                f"docs/port/units/{expected}{FRAGMENT_DIR}{args.pr}-<slug>.tsv"
                + (f"; unit-owned paths changed:\n{details}" if details else "")
            )
        foreign = sorted(unit for unit in touched if unit != expected)
        if foreign:
            details = "\n".join(
                f"  {path} (unit {unit})"
                for unit in foreign
                for path in touched[unit]
            )
            raise GuardError(
                f"unit PR {expected} touches foreign unit evidence:\n{details}"
            )
        foreign_owned = {
            path: claim for path, claim in owned.items() if claim.unit != expected
        }
        if foreign_owned:
            details = "\n".join(
                f"  {path} (owned by {claim.unit}; {claim.evidence})"
                for path, claim in sorted(foreign_owned.items())
            )
            raise GuardError(
                f"unit PR {expected} touches unit-owned paths owned by another "
                f"unit:\n{details}"
            )

        errors: list[str] = []
        queue_detail = None
        fragment_detail = None
        try:
            queue_detail = _check_queue(repo, base, head, expected, changes)
        except GuardError as exc:
            errors.append(str(exc))
        try:
            fragment_detail = _check_fragment(
                repo,
                base,
                head,
                merge,
                expected,
                args.pr,
                changes,
                repo_slug=repository_slug(repo, args.repo_slug),
                verify_ci=args.verify_ci,
                merge_commit=merge_commit,
            )
        except GuardError as exc:
            errors.append(str(exc))
        if errors:
            raise GuardError("\n".join(errors))

        print(f"unit: {expected}")
        print(f"queue: {queue_detail}")
        print(f"fragment: {fragment_detail}")
        return 0
    except GuardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
