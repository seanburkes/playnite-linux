#!/usr/bin/env python3
"""List test projects at a commit or in the working tree.

Only projects that identify as test projects contribute: an ``IsTestProject``
marker or the test SDK reference. A fixture or helper project under ``tests/``
must not create a phantom project in either discovery mode.
"""

import argparse
from pathlib import Path
import re
import subprocess
import sys

TEST_PROJECT = re.compile(
    r"<IsTestProject>\s*true\s*</IsTestProject>|Microsoft\.NET\.Test\.Sdk")


def project_name(value):
    name = Path(value).name
    return name[:-len(".csproj")] if name.endswith(".csproj") else name


def is_test_project(content):
    return TEST_PROJECT.search(content) is not None


def list_test_projects(ref, repo):
    tree = subprocess.run(
        ["git", "ls-tree", "-r", "--name-only", ref, "--", "tests"],
        cwd=repo, capture_output=True, text=True, check=True)
    names = set()
    for path in tree.stdout.splitlines():
        if not path.endswith(".csproj") or "/obj/" in path or "/bin/" in path:
            continue
        project = subprocess.run(
            ["git", "show", f"{ref}:{path}"],
            cwd=repo, capture_output=True, text=True, check=True)
        if is_test_project(project.stdout):
            names.add(project_name(path))
    return sorted(names)


def list_worktree_projects(root):
    projects = []
    for path in sorted((root / "tests").rglob("*.csproj")):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        if is_test_project(path.read_text()):
            projects.append(str(path.resolve()))
    return projects


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref")
    parser.add_argument("--worktree", type=Path)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    args = parser.parse_args()
    if bool(args.ref) == bool(args.worktree):
        parser.error("give exactly one of --ref or --worktree")
    try:
        if args.worktree:
            for project in list_worktree_projects(args.worktree):
                print(project)
        else:
            for name in list_test_projects(args.ref, args.repo):
                print(name)
    except (OSError, subprocess.SubprocessError) as error:
        print(f"::error::Cannot list test projects: {error}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
