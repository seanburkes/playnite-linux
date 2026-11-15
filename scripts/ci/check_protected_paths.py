#!/usr/bin/env python3
"""Fail when protected policy paths change outside the oracle-change route."""

import argparse
from pathlib import Path
import subprocess
import sys


def merge_base(base, repo):
    result = subprocess.run(
        ["git", "merge-base", base, "HEAD"],
        cwd=repo, capture_output=True, text=True, check=False)
    value = result.stdout.strip()
    if result.returncode != 0 or not value:
        raise RuntimeError(result.stderr.strip()
                           or f"cannot compute merge base for {base}")
    return value


def protected_changes(base, paths, repo):
    fork_point = merge_base(base, repo)
    result = subprocess.run(
        ["git", "diff", "--name-only", fork_point, "HEAD", "--", *paths],
        cwd=repo, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or "git diff failed")
    return [line for line in result.stdout.splitlines() if line.strip()]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--paths", nargs="+", required=True)
    parser.add_argument("--error", default="Protected oracle artifacts changed. "
                        "An oracle change carries the oracle-change label and passes the "
                        "protected-path gate; review is adversarial on the owner's behalf, "
                        "with no second reviewer and no approval gate.")
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    args = parser.parse_args()
    try:
        changed = protected_changes(args.base, args.paths, args.repo)
    except (OSError, RuntimeError) as error:
        print(f"::error::Cannot compare protected paths: {error}")
        return 1
    if not changed:
        return 0
    print(f"::error::{args.error}")
    for path in changed:
        print(path)
    return 1


if __name__ == "__main__":
    sys.exit(main())
