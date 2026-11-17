#!/usr/bin/env python3
"""Fail a pull request that touches more than one unit evidence namespace.

Usage:
    python3 scripts/port/check_unit_ownership.py --base <sha> --head <sha>
        [--branch <head-ref>] [--unit <unit>] [--merge-base <sha>]
    python3 scripts/port/check_unit_ownership.py --head <sha> --diff-file <path>

The diff is computed as ``git diff <merge-base(base, head)> <head>``.  Rename
detection is disabled so both the old and new namespaces are charged.  Exactly
zero unit namespaces is a process PR and is allowed only when the diff touches
no unit evidence at all.  Exactly one is allowed.  Two or more fail with every
offending path and its unit.  When a branch or ``--unit`` names the owning unit,
touching one different (foreign) unit also fails.

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
    changed_paths,
    declared_path_prefixes,
    derive_branch_unit,
    format_paths,
    known_units,
    merge_base,
    paths_from_diff_file,
    repo_root,
    resolve_commit,
    touched_units,
)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", help="repository checkout (defaults to this script's checkout)")
    parser.add_argument("--base", help="pull-request base commit")
    parser.add_argument("--head", help="pull-request head commit")
    parser.add_argument("--merge-base", help="use this merge base instead of computing one")
    parser.add_argument("--diff-file", help="offline diff, one path per line")
    parser.add_argument("--unit", help="owning unit asserted by the caller")
    parser.add_argument("--branch", help="pull-request head ref, for example fm/p4-003")
    return parser.parse_args(argv)


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
            declared = declared_path_prefixes(repo, [head])
        else:
            if not args.base:
                raise GuardError("--base is required unless --diff-file is used")
            base = resolve_commit(repo, args.base, "base")
            if args.merge_base:
                merge = resolve_commit(repo, args.merge_base, "merge base")
            else:
                merge = merge_base(repo, base, head)
            changes = changed_paths(repo, merge, head)
            units = known_units(repo, [base, head, merge])
            declared = declared_path_prefixes(repo, [base, head, merge])

        expected = args.unit
        if expected is not None and not expected:
            raise GuardError("--unit must not be empty")
        branch_unit = derive_branch_unit(args.branch, units)
        if expected and branch_unit and expected != branch_unit:
            raise GuardError(
                f"--unit {expected} conflicts with branch {args.branch!r}, "
                f"which names {branch_unit}"
            )
        expected = expected or branch_unit

        touched = touched_units(changes, units, expected, declared)
        if not touched:
            print("unit: (none)")
            print("process PR: no unit evidence namespace is touched")
            return 0

        if len(touched) == 1:
            unit = next(iter(touched))
            paths = touched[unit]
            if expected and unit != expected:
                print(f"unit: {expected}")
                print("allowed paths touched:")
                print("  (none)")
                print("offending paths:")
                print("\n".join(f"  {path} (unit {unit})" for path in paths))
                raise GuardError(
                    f"foreign unit evidence: PR owns {expected} but touches unit {unit}"
                )
            print(f"unit: {unit}")
            print("allowed paths touched:")
            print(format_paths(paths))
            print("offending paths:")
            print("  (none)")
            return 0

        units_named = ", ".join(touched)
        print(f"unit: MULTIPLE ({units_named})")
        print("allowed paths touched:")
        if expected and expected in touched:
            print(format_paths(touched[expected]))
        else:
            print("  (none)")
        print("offending paths:")
        for unit, paths in touched.items():
            if expected and unit == expected:
                continue
            for path in paths:
                print(f"  {path} (unit {unit})")
        raise GuardError(
            f"a pull request may carry at most one unit evidence namespace; "
            f"it touches {len(touched)}: {units_named}"
        )
    except GuardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
