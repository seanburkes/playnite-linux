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
ambiguous.

The head-commit ``Cross-Unit-Move`` trailer is validated by the same shared
helper as the ownership guard.  Exactly the listed foreign paths it authorises
are exempt from both the foreign unit-evidence check and the map-owned foreign
path check; an unlisted path, a wrong owner, or an unpaired entry still fails.

``--verify-ci`` resolves every cited run through the read-only GitHub API: the
run must exist in this repository, be a completed successful
``linux-ci`` run, and its ``head_sha`` must equal a head the fragment cites.
The workflow always enables it.

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
    OwnershipMap,
    GuardError,
    authorised_cross_unit_move,
    bot_owned_errors,
    changed_paths,
    claim_is_allowed,
    declared_unit,
    first_map_errors,
    known_units,
    merge_base,
    ownership_hits,
    ownership_map_exists,
    parse_ledger_fragment,
    parse_queue_shard,
    product_paths,
    read_at,
    read_ownership_map,
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


def _migration_unit(changes) -> str | None:
    """Return the single unit with a paired legacy delete and history add."""
    deletes = set()
    adds = set()
    for change in changes:
        if change.status == "D" and change.path.endswith(".ledger.tsv"):
            deletes.add(change.path[: -len(".ledger.tsv")].rsplit("/", 1)[-1])
        elif (change.status == "A"
              and change.path.endswith("/0000-history.tsv")
              and ".ledger.d/" in change.path):
            directory = change.path.split(".ledger.d/", 1)[0]
            adds.add(directory.rsplit("/", 1)[-1])
    common = deletes & adds
    return common.pop() if len(common) == 1 else None


def _pure_history_migration(repo: Path, base: str, head: str, unit: str,
                            changes) -> bool:
    legacy = f"docs/port/units/{unit}.ledger.tsv"
    history = f"docs/port/units/{unit}{FRAGMENT_DIR}0000-history.tsv"
    statuses = {change.path: change.status for change in changes}
    if statuses.get(legacy) != "D" or statuses.get(history) != "A":
        return False
    base_bytes = read_at(repo, base, legacy)
    head_bytes = read_at(repo, head, history)
    return base_bytes is not None and head_bytes is not None and base_bytes == head_bytes


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

        product = product_paths(changes, units)
        map_errors = bot_owned_errors(
            changes,
            base_has_map=ownership_map_exists(repo, base),
            product_changed=bool(product),
        )
        if map_errors:
            raise GuardError("\n".join(map_errors))
        first_map_errors(repo, head, changes, ownership_map_exists(repo, base))

        expected = declared_unit(
            repo, head=head, units=units, branch=args.branch, explicit=args.unit
        )
        if expected is None:
            expected = _migration_unit(changes)
        ownership = (
            read_ownership_map(repo, base, units)
            if ownership_map_exists(repo, base)
            else read_ownership_map(repo, merge, units)
            if ownership_map_exists(repo, merge)
            else None
        )
        if ownership is None:
            ownership = OwnershipMap(
                {}, False, "ownership.paths is missing at the base and merge base"
            )
        if product and not ownership.available:
            raise GuardError(
                "ownership map unavailable ("
                + (ownership.reason or "no validated base map")
                + "); product/test/tool changes cannot be checked for foreign "
                "ownership even when the PR declares a unit. Restore or regenerate "
                "the base ownership map before changing product material. "
                "Offending paths:\n" + "\n".join(f"  {path}" for path in product)
            )
        merge_commit = (
            resolve_commit(repo, args.merge_commit, "merge commit")
            if args.merge_commit
            else None
        )
        link_errors = symlink_errors(repo, base, head, changes, ownership, expected)
        if link_errors:
            raise GuardError("\n".join(link_errors))

        touched = touched_units(changes, units, expected)
        owned = ownership_hits(changes, ownership)
        _, allowed_foreign = authorised_cross_unit_move(
            repo,
            head,
            declared=expected,
            changes=changes,
            ownership=ownership,
            touched=touched,
            units=units,
        )
        if expected is None:
            if touched or owned or product:
                details = "\n".join(
                    f"  {path} (unit {claim.unit}; {claim.evidence})"
                    for path, rows in sorted(owned.items())
                    for claim in rows
                )
                if product and not ownership.available:
                    message = (
                        "ownership map unavailable ("
                        + (ownership.reason or "no validated base map")
                        + ") and the pull request changes product/test/tool paths; "
                        "declare the owning unit with fm/<unit>, a 'Port-Unit: "
                        "<unit>' head-commit trailer, or --unit and carry its "
                        "queue transition and ledger fragment"
                    )
                else:
                    message = (
                        "the pull request changes unit-owned or product/test/tool "
                        "paths but declares no unit; name the branch fm/<unit>, "
                        "add a 'Port-Unit: <unit>' head-commit trailer, or pass "
                        "--unit"
                    )
                raise GuardError(
                    message
                    + (f":\n{details}" if details else
                       "; offending paths:\n" + "\n".join(
                           [f"  {path}" for path in product]
                           or [f"  {path}" for paths in touched.values() for path in paths]))
                )
            print("unit: (none)")
            print("process PR: no unit-owned or product/test/tool path is touched")
            return 0

        if not touched:
            details = "\n".join(
                f"  {path} (unit {claim.unit}; {claim.evidence})"
                for path, rows in sorted(owned.items())
                for claim in rows
            )
            raise GuardError(
                f"unit PR {expected} carries no unit evidence: "
                f"missing required queue transition {_queue_path(expected)} and "
                f"missing required ledger fragment "
                f"docs/port/units/{expected}{FRAGMENT_DIR}{args.pr}-<slug>.tsv"
                + (f"; unit-owned or product paths changed:\n{details}"
                   if details else
                   "; product paths changed:\n" + "\n".join(
                       f"  {path}" for path in product))
            )
        foreign = sorted(
            unit
            for unit in touched
            if unit != expected
            and any(path not in allowed_foreign for path in touched[unit])
        )
        if foreign:
            details = "\n".join(
                f"  {path} (unit {unit})"
                for unit in foreign
                for path in touched[unit]
                if path not in allowed_foreign
            )
            raise GuardError(
                f"unit PR {expected} touches foreign unit evidence:\n{details}"
            )
        foreign_owned = {
            path: rows for path, rows in owned.items()
            if not claim_is_allowed(rows, expected)
            and path not in allowed_foreign
        }
        if foreign_owned:
            details = "\n".join(
                f"  {path} (unit {claim.unit}; {claim.evidence})"
                for path, rows in sorted(foreign_owned.items())
                for claim in rows
            )
            raise GuardError(
                f"unit PR {expected} touches unit-owned paths owned only by "
                f"another unit:\n{details}"
            )

        if _pure_history_migration(repo, base, head, expected, changes):
            legacy = f"docs/port/units/{expected}.ledger.tsv"
            history = f"docs/port/units/{expected}{FRAGMENT_DIR}0000-history.tsv"
            if set(touched.get(expected, [])) <= {legacy, history}:
                print(f"unit: {expected}")
                print(f"migration: {legacy} -> {history} (verbatim)")
                return 0

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
