#!/usr/bin/env python3
"""List test projects present at a commit.

The base project list is a policy input, so only projects that identify as test
projects contribute: an ``IsTestProject`` marker or the test SDK reference. A
fixture or helper project under ``tests/`` must not create a phantom project
that a later change can "delete".
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
        if TEST_PROJECT.search(project.stdout):
            names.add(project_name(path))
    return sorted(names)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", required=True)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    args = parser.parse_args()
    try:
        names = list_test_projects(args.ref, args.repo)
    except (OSError, subprocess.SubprocessError) as error:
        print(f"::error::Cannot list base test projects: {error}")
        return 1
    for name in names:
        print(name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
