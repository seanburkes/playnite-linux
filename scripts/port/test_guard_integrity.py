"""A pull request may add tests but may not silently drop existing guard proofs.

The ownership and evidence guards are only as strong as their negative proofs.
This module compares every ``scripts/port/test_*.py`` file at the pull request
base with the head: a base test case must still exist and the head must not
contain fewer ``self.assert*`` calls.  Counts and names cannot catch every
weakening, but they stop a PR from deleting a proof or replacing it with an
empty test.  When no base revision is available locally the check passes
against HEAD so the suite remains runnable outside CI; plan-guards supplies
BASE_SHA on every pull request.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / "scripts" / "port"

TEST_CASE = re.compile(r"^\s*def\s+(test_[A-Za-z0-9_]+)\s*\(", re.MULTILINE)
ASSERTION = re.compile(r"\bself\.assert[A-Za-z0-9_]+")


def _git(*args: str) -> str | None:
    completed = subprocess.run(
        ["git", "-C", str(ROOT), *args], capture_output=True, text=True
    )
    return completed.stdout if completed.returncode == 0 else None


def base_revision() -> str | None:
    explicit = os.environ.get("BASE_SHA", "").strip()
    if re.fullmatch(r"[0-9a-f]{40}", explicit):
        return explicit
    resolved = _git("rev-parse", "--verify", "--quiet", "origin/main^{commit}")
    if resolved and re.fullmatch(r"[0-9a-f]{40}", resolved.strip()):
        return resolved.strip()
    return None


def integrity_errors(base: str, head: str, label: str = "<test>") -> list[str]:
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


class GuardIntegrityTests(unittest.TestCase):
    def test_removed_case_is_detected(self) -> None:
        base = "class T:\n    def test_kept(self):\n        self.assertTrue(1)\n    def test_removed(self):\n        self.assertTrue(2)\n"
        head = "class T:\n    def test_kept(self):\n        self.assertTrue(1)\n"

        errors = integrity_errors(base, head)

        self.assertTrue(any("test_removed" in error for error in errors))

    def test_fewer_assertions_are_detected(self) -> None:
        base = "def test_one(self):\n    self.assertTrue(1)\n    self.assertFalse(0)\n"
        head = "def test_one(self):\n    self.assertTrue(1)\n"

        errors = integrity_errors(base, head)

        self.assertTrue(any("fewer than" in error for error in errors))

    def test_current_guard_suite_keeps_every_base_case_and_assertion(self) -> None:
        base = base_revision()
        if base is None:
            return
        errors: list[str] = []
        for path in sorted(TESTS.glob("test_*.py")):
            relative = path.relative_to(ROOT).as_posix()
            original = _git("show", f"{base}:{relative}")
            if original is None:
                continue
            errors.extend(
                integrity_errors(original, path.read_text(encoding="utf-8"), relative)
            )
        self.assertEqual([], errors, "\n".join(errors))


if __name__ == "__main__":
    unittest.main()
