#!/usr/bin/env python3
"""Fail a pull request that touches more than one unit's owned paths.

Usage:
    python3 scripts/port/check_unit_ownership.py --base <sha> --head <sha>
        [--branch <head-ref>] [--unit <unit>] [--merge-base <sha>]
    python3 scripts/port/check_unit_ownership.py --head <sha> --diff-file <path>

The diff is computed as ``git diff <merge-base(base, head)> <head>``.  Rename
detection is disabled so both the old and new namespaces are charged; an
explicit rename record is still expanded to a delete plus an add.  The base
commit's bot-generated ``docs/port/units/ownership.paths`` map attributes
product source, shared tests, fixtures and tools to the unit whose merged
commit introduced or last changed them.

Exactly zero unit-owned or unit-evidence paths is a process PR and is allowed.
Exactly one is allowed only when the pull request declares that unit through
``fm/<unit>-...``, a ``Port-Unit: <unit>`` head-commit trailer, or ``--unit``.
A path owned by another unit, or any unit-owned path with no declaration, fails
with the path, the owning unit and its map evidence.  A path owned by no unit
stays shared.  Ambiguous input fails closed.

This script is read-only: it never fetches, checks out, resets, or writes.
Exit codes: 0 allowed, 1 policy failure including ambiguous or invalid input.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from unit_guard_common import (  # noqa: E402
    GuardError,
    bot_owned_errors,
    changed_paths,
    declared_unit,
    format_paths,
    known_units,
    load_ownership_map,
    merge_base,
    ownership_hits,
    ownership_map_exists,
    paths_from_diff_file,
    repo_root,
    resolve_commit,
    symlink_errors,
    touched_units,
)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", help="repository checkout (defaults to this script's checkout)")
    parser.add_argument("--base", help="pull-request base commit")
    parser.add_argument("--head", help="pull-request head commit")
    parser.add_argument("--merge-base", help="use this merge base instead of computing one")
    parser.add_argument("--diff-file", help="offline diff, one path or name-status record per line")
    parser.add_argument("--unit", help="owning unit asserted by the caller")
    parser.add_argument("--branch", help="pull-request head ref, for example fm/p4-003")
    return parser.parse_args(argv)


def _claim_lines(claims) -> list[str]:
    return [
        f"  {claim.path} (unit {claim.unit}; {claim.evidence})"
        for claim in sorted(claims.values(), key=lambda item: item.path)
    ]


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    try:
        repo = repo_root(args.repo)
        if not args.head:
            raise GuardError("--head is required")
        head = resolve_commit(repo, args.head, "head")

        if args.diff_file:
            changes = paths_from_diff_file(Path(args.diff_file))
            units = known_units(repo, [head, None])
            # Offline mode still takes ownership from a base commit, never from
            # the head tree the caller is asking about.
            base = resolve_commit(repo, args.base, "base") if args.base else None
            claims = load_ownership_map(repo, base) if base else {}
            merge = None
        else:
            if not args.base:
                raise GuardError("--base is required unless --diff-file is used")
            base = resolve_commit(repo, args.base, "base")
            merge = (
                resolve_commit(repo, args.merge_base, "merge base")
                if args.merge_base
                else merge_base(repo, base, head)
            )
            changes = changed_paths(repo, merge, head)
            units = known_units(repo, [base, head, merge])
            claims = (
                load_ownership_map(repo, base)
                if ownership_map_exists(repo, base)
                else load_ownership_map(repo, merge)
            )

        map_errors = bot_owned_errors(changes)
        if map_errors:
            raise GuardError("\n".join(map_errors))

        declared = declared_unit(
            repo, head=head, units=units, branch=args.branch, explicit=args.unit
        )

        if base is not None and merge is not None:
            link_errors = symlink_errors(repo, base, head, changes, claims, declared)
            if link_errors:
                raise GuardError("\n".join(link_errors))

        touched = touched_units(changes, units, declared)
        owned = ownership_hits(changes, claims)

        if declared is None:
            if touched or owned:
                details = _claim_lines(owned)
                if not details:
                    details = [
                        "  unit evidence touched: " + ", ".join(sorted(touched))
                    ]
                raise GuardError(
                    "the pull request changes unit-owned paths but declares no unit; "
                    "name the branch fm/<unit>, add a 'Port-Unit: <unit>' head-commit "
                    "trailer, or pass --unit. Offending paths:\n"
                    + "\n".join(details)
                )
            print("unit: (none)")
            print("process PR: no unit-owned or unit evidence path is touched")
            return 0

        if len(touched) > 1:
            units_named = ", ".join(touched)
            print(f"unit: MULTIPLE ({units_named})")
            print("allowed paths touched:")
            print(format_paths(touched.get(declared, [])))
            print("offending paths:")
            for unit, paths in touched.items():
                if unit == declared:
                    continue
                for path in paths:
                    print(f"  {path} (unit {unit})")
            raise GuardError(
                f"a pull request may carry at most one unit evidence namespace; "
                f"it touches {len(touched)}: {units_named}"
            )

        foreign_owned = {
            path: claim for path, claim in owned.items() if claim.unit != declared
        }
        foreign_evidence = sorted(unit for unit in touched if unit != declared)
        if foreign_owned or foreign_evidence:
            print(f"unit: {declared}")
            print("allowed paths touched:")
            own_paths = list(touched.get(declared, [])) + [
                path for path, claim in owned.items() if claim.unit == declared
            ]
            print(format_paths(sorted(set(own_paths))))
            print("offending paths:")
            for unit in foreign_evidence:
                for path in touched[unit]:
                    print(f"  {path} (unit {unit})")
            print("\n".join(_claim_lines(foreign_owned)))
            if foreign_evidence:
                raise GuardError(
                    f"foreign unit evidence: PR owns {declared} but touches unit "
                    f"{foreign_evidence[0]}"
                )
            owners = sorted({claim.unit for claim in foreign_owned.values()})
            raise GuardError(
                f"foreign unit ownership: PR owns {declared} but touches paths "
                f"owned by {', '.join(owners)}"
            )

        if not touched and not owned:
            raise GuardError(
                f"unit PR {declared} touches no unit-owned path and no unit evidence; "
                "a pull request that changes no unit material is a process PR and "
                "must not declare a unit"
            )

        print(f"unit: {declared}")
        print("allowed paths touched:")
        own_paths = list(touched.get(declared, [])) + [
            path for path, claim in owned.items() if claim.unit == declared
        ]
        print(format_paths(sorted(set(own_paths))))
        print("offending paths:")
        print("  (none)")
        for claim in sorted(
            (claim for claim in owned.values() if claim.unit == declared),
            key=lambda item: item.path,
        ):
            print(f"ownership: {claim.path} ({claim.unit}; {claim.evidence})")
        return 0
    except GuardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
