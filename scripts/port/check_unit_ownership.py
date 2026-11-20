#!/usr/bin/env python3
"""Fail a pull request that touches more than one unit's owned paths.

Usage:
    python3 scripts/port/check_unit_ownership.py --base <sha> --head <sha>
        [--branch <head-ref>] [--unit <unit>] [--merge-base <sha>]
    python3 scripts/port/check_unit_ownership.py [--base <sha>] --head <sha>
        --diff-file <path>

The diff is computed as ``git diff <merge-base(base, head)> <head>``.  Rename
detection is disabled so both the old and new namespaces are charged; an
explicit rename record is still expanded to a delete plus an add.  The base
commit's bot-generated ``docs/port/units/ownership.paths`` map attributes
product source, shared tests, fixtures and tools to every unit whose merged
commit introduced or changed them.  A ``*`` row means explicitly shared.

Exactly zero unit-owned, unit-evidence, or product/test/tool paths is a process
PR and is allowed.  Otherwise the pull request must declare one unit through
``fm/<unit>-...``, a ``Port-Unit: <unit>`` head-commit trailer, or ``--unit``.
A path owned only by other units fails with the path, the owning units and the
map evidence.  New or unowned product paths still require a declaration and,
via the evidence guard, a fragment.  If the base map is missing, empty or
invalid, product changes fail closed with ``ownership map unavailable`` unless
the PR is a unit PR with its own evidence.

An approved move out of another unit's ownership may add a head-commit
``Cross-Unit-Move: <owner>:<path>[,...]`` trailer beside ``Port-Unit``.  The
manifest must list every foreign path the PR edits with its actual owner and a
delete or modification status, add at least one new file in the declaring
unit's namespace, pair each deleted path one-to-one with its own same-basename
addition owned by the declaring unit, and pair each modified path one-to-one
with its own same-basename delete.  Everything else in this check still
applies.

This script is read-only: it never fetches, checks out, resets, or writes.
Exit codes: 0 allowed, 1 policy failure including ambiguous or invalid input.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from unit_guard_common import (  # noqa: E402
    OwnershipMap,
    GuardError,
    authorised_cross_unit_move,
    bot_owned_errors,
    changed_paths,
    claim_is_allowed,
    declared_unit,
    first_map_errors,
    format_paths,
    known_units,
    merge_base,
    ownership_hits,
    ownership_map_exists,
    paths_from_diff_file,
    product_paths,
    read_ownership_map,
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


def _migration_unit(changes) -> str | None:
    """Return the single unit with a paired legacy delete and history add."""
    deletes = {
        change.path[: -len(".ledger.tsv")].rsplit("/", 1)[-1]
        for change in changes
        if change.status == "D" and change.path.endswith(".ledger.tsv")
    }
    adds = {
        change.path.split(".ledger.d/", 1)[0].rsplit("/", 1)[-1]
        for change in changes
        if (change.status == "A" and change.path.endswith("/0000-history.tsv")
            and ".ledger.d/" in change.path)
    }
    common = deletes & adds
    return common.pop() if len(common) == 1 else None


def _claim_lines(rows_by_path) -> list[str]:
    lines: list[str] = []
    for path, rows in sorted(rows_by_path.items()):
        for claim in rows:
            lines.append(f"  {path} (unit {claim.unit}; {claim.evidence})")
    return lines


def _owners_text(rows) -> str:
    return ", ".join(f"{claim.unit} ({claim.evidence})" for claim in rows)


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
            ownership = (
                read_ownership_map(repo, base, units)
                if base and ownership_map_exists(repo, base)
                else OwnershipMap({}, False, "no readable base ownership map")
            )
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
            if ownership_map_exists(repo, base):
                ownership = read_ownership_map(repo, base, units)
            elif ownership_map_exists(repo, merge):
                ownership = read_ownership_map(repo, merge, units)
            else:
                ownership = OwnershipMap(
                    {}, False, "ownership.paths is missing at the base and merge base"
                )

        product = product_paths(changes, units)
        map_errors = bot_owned_errors(
            changes,
            base_has_map=ownership_map_exists(repo, base) if base else None,
            product_changed=bool(product),
        )
        if map_errors:
            raise GuardError("\n".join(map_errors))
        first_map_errors(
            repo, head, changes,
            ownership_map_exists(repo, base) if base else None,
        )

        declared = declared_unit(
            repo, head=head, units=units, branch=args.branch, explicit=args.unit
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

        if base is not None and merge is not None:
            link_errors = symlink_errors(repo, base, head, changes, ownership, declared)
            if link_errors:
                raise GuardError("\n".join(link_errors))

        touched = touched_units(changes, units, declared)
        owned = ownership_hits(changes, ownership)
        move, allowed_foreign = authorised_cross_unit_move(
            repo,
            head,
            declared=declared,
            changes=changes,
            ownership=ownership,
            touched=touched,
            units=units,
        )

        if declared is None:
            migration = _migration_unit(changes)
            if migration and set(touched) <= {migration} and not product:
                print(f"unit: {migration}")
                print(
                    "migration: docs/port/units/"
                    f"{migration}.ledger.tsv -> docs/port/units/"
                    f"{migration}.ledger.d/0000-history.tsv (verbatim)"
                )
                return 0
            if touched or owned or product:
                if product and not ownership.available:
                    raise GuardError(
                        "ownership map unavailable ("
                        + (ownership.reason or "no validated base map")
                        + ") and the pull request changes product/test/tool paths; "
                        "declare the owning unit with fm/<unit>, a 'Port-Unit: "
                        "<unit>' head-commit trailer, or --unit and carry its "
                        "queue transition and ledger fragment. Offending paths:\n"
                        + "\n".join(_claim_lines(owned) or [f"  {path}" for path in product])
                    )
                raise GuardError(
                    "the pull request changes unit-owned or product/test/tool paths "
                    "but declares no unit; name the branch fm/<unit>, add a "
                    "'Port-Unit: <unit>' head-commit trailer, or pass --unit. "
                    "Offending paths:\n"
                    + "\n".join(
                        _claim_lines(owned)
                        or [f"  {path}" for path in product]
                        or [
                            f"  {path}"
                            for paths in touched.values()
                            for path in paths
                        ]
                    )
                )
            print("unit: (none)")
            print("process PR: no unit-owned or product/test/tool path is touched")
            return 0

        if len(touched) > 1:
            uncovered = {
                unit: [path for path in paths if path not in allowed_foreign]
                for unit, paths in touched.items()
                if unit != declared
            }
            uncovered = {unit: paths for unit, paths in uncovered.items() if paths}
            if uncovered:
                units_named = ", ".join(touched)
                print(f"unit: MULTIPLE ({units_named})")
                print("allowed paths touched:")
                print(format_paths(touched.get(declared, [])))
                print("offending paths:")
                for unit, paths in uncovered.items():
                    for path in paths:
                        print(f"  {path} (unit {unit})")
                raise GuardError(
                    f"a pull request may carry at most one unit evidence namespace; "
                    f"it touches {len(touched)}: {units_named}"
                )

        foreign_owned = {
            path: rows
            for path, rows in owned.items()
            if not claim_is_allowed(rows, declared)
            and path not in allowed_foreign
        }
        foreign_evidence = sorted(
            unit
            for unit in touched
            if unit != declared
            and any(path not in allowed_foreign for path in touched[unit])
        )
        if foreign_owned or foreign_evidence:
            print(f"unit: {declared}")
            print("allowed paths touched:")
            own_paths = list(touched.get(declared, [])) + [
                path for path, rows in owned.items()
                if claim_is_allowed(rows, declared)
            ] + sorted(allowed_foreign)
            print(format_paths(sorted(set(own_paths))))
            print("offending paths:")
            for unit in foreign_evidence:
                for path in touched[unit]:
                    if path not in allowed_foreign:
                        print(f"  {path} (unit {unit})")
            print("\n".join(_claim_lines(foreign_owned)))
            if foreign_evidence:
                raise GuardError(
                    f"foreign unit evidence: PR owns {declared} but touches unit "
                    f"{foreign_evidence[0]}"
                )
            owners = sorted({
                claim.unit
                for rows in foreign_owned.values()
                for claim in rows
            })
            raise GuardError(
                f"foreign unit ownership: PR owns {declared} but touches paths "
                f"owned by {', '.join(owners)}"
            )

        if not touched and not owned and not product:
            raise GuardError(
                f"unit PR {declared} touches no unit-owned path and no "
                "product/test/tool material; a pull request that changes no unit "
                "material is a process PR and must not declare a unit"
            )

        print(f"unit: {declared}")
        print("allowed paths touched:")
        own_paths = list(touched.get(declared, [])) + [
            path for path, rows in owned.items()
            if claim_is_allowed(rows, declared)
        ] + sorted(allowed_foreign)
        print(format_paths(sorted(set(own_paths))))
        print("offending paths:")
        print("  (none)")
        for path, rows in sorted(owned.items()):
            if claim_is_allowed(rows, declared):
                print(f"ownership: {path} ({_owners_text(rows)})")
        for path, unit in sorted((move or {}).items()):
            print(f"cross-unit move: {path} (unit {unit})")
        return 0
    except GuardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
