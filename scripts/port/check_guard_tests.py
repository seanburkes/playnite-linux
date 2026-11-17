#!/usr/bin/env python3
"""Fail a pull request that deletes, renames or weakens a port guard test.

Usage:
    python3 scripts/port/check_guard_tests.py --base <sha> [--head <rev>]

This is the independent enforcement behind the guard integrity proofs: it is a
plain script, not a unittest, so deleting a ``scripts/port/test_*.py`` file (or
this module's own test file) cannot disable it.  plan-guards runs it on every
pull request.

A base test file must still exist at the same path at head (a rename is a
deletion), every base test case must still exist, and the assertion count may
not drop.  New files, cases and assertions are allowed.

Exit codes: 0 allowed, 1 removed/renamed file, removed case, fewer assertions,
or an unresolvable input.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TEST_PATH = re.compile(r"^scripts/port/test_[^/]*\.py$")
TEST_CASE = re.compile(r"^\s*def\s+(test_[A-Za-z0-9_]+)\s*\(", re.MULTILINE)
ASSERTION = re.compile(r"\bself\.assert[A-Za-z0-9_]+")


class InventoryError(Exception):
    pass


def git(repo: Path, *args: str) -> str:
    completed = subprocess.run(
        ["git", "-C", str(repo), *args], capture_output=True, text=True
    )
    if completed.returncode != 0:
        raise InventoryError(
            f"git {' '.join(args)} failed: {completed.stderr.strip()}"
        )
    return completed.stdout


def files_at(repo: Path, revision: str) -> dict[str, str]:
    listing = git(repo, "ls-tree", "-r", "--name-only", "-z", revision, "--", "scripts/port")
    files: dict[str, str] = {}
    for item in listing.split("\0"):
        if not item or not TEST_PATH.fullmatch(item):
            continue
        files[item] = git(repo, "show", f"{revision}:{item}")
    return files


def missing_file_errors(
    base_files: dict[str, str], head_files: dict[str, str]
) -> list[str]:
    return [
        f"{path}: test file present at the base is missing at head; a pull "
        "request may add a guard proof but must not delete or rename one away"
        for path in sorted(set(base_files) - set(head_files))
    ]


def integrity_errors(base: str, head: str, label: str) -> list[str]:
    errors: list[str] = []
    missing = sorted(set(TEST_CASE.findall(base)) - set(TEST_CASE.findall(head)))
    for name in missing:
        errors.append(f"{label}: test case {name} present at the base is missing at head")
    base_assertions = len(ASSERTION.findall(base))
    head_assertions = len(ASSERTION.findall(head))
    if head_assertions < base_assertions:
        errors.append(
            f"{label}: {head_assertions} assertions at head is fewer than "
            f"{base_assertions} at the base; a pull request may add a proof but "
            "must not weaken one"
        )
    return errors


def check(repo: Path, base_revision: str, head_revision: str) -> list[str]:
    base_files = files_at(repo, base_revision)
    head_files = files_at(repo, head_revision)
    errors = missing_file_errors(base_files, head_files)
    for path, original in sorted(base_files.items()):
        current = head_files.get(path)
        if current is None:
            continue
        errors.extend(integrity_errors(original, current, path))
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=REPO)
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args(argv)
    try:
        if not re.fullmatch(r"[0-9a-fA-F]{40}", args.base):
            raise InventoryError("--base must be a full 40-hex commit id")
        errors = check(args.repo.resolve(), args.base.lower(), args.head)
    except InventoryError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1
    print("port guard test files, cases and assertion counts are preserved")
    return 0


if __name__ == "__main__":
    sys.exit(main())
