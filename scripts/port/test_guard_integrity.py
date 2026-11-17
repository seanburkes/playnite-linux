"""Negative proofs for the guard-test inventory policy.

The independent enforcement is ``scripts/port/check_guard_tests.py``, run by
plan-guards as a plain script so deleting any unittest file (including this one)
cannot disable it.  These tests keep the policy's own negative proofs: a
deleted file, a renamed file, a removed case and a lower assertion count all
fail.  When no base revision is available locally the live check uses HEAD so
the suite remains runnable outside CI; plan-guards supplies BASE_SHA.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))

import check_guard_tests as inventory  # noqa: E402

TEST_CASE = inventory.TEST_CASE
ASSERTION = inventory.ASSERTION


def base_revision() -> str | None:
    explicit = os.environ.get("BASE_SHA", "").strip()
    if re.fullmatch(r"[0-9a-f]{40}", explicit):
        return explicit
    completed = __import__("subprocess").run(
        ["git", "-C", str(ROOT), "rev-parse", "--verify", "--quiet",
         "origin/main^{commit}"],
        capture_output=True,
        text=True,
    )
    if completed.returncode == 0 and re.fullmatch(
        r"[0-9a-f]{40}", completed.stdout.strip()
    ):
        return completed.stdout.strip()
    return None


class GuardIntegrityTests(unittest.TestCase):
    @staticmethod
    def _init(repo: Path) -> None:
        subprocess.run(["git", "-C", str(repo), "init", "-q"], check=True)
        subprocess.run(
            ["git", "-C", str(repo), "config", "user.email", "guard@example.invalid"],
            check=True,
        )
        subprocess.run(
            ["git", "-C", str(repo), "config", "user.name", "Guard Inventory"],
            check=True,
        )

    @staticmethod
    def _commit(repo: Path, message: str) -> str:
        subprocess.run(["git", "-C", str(repo), "add", "-A"], check=True)
        subprocess.run(
            ["git", "-C", str(repo), "commit", "-q", "--allow-empty", "-m", message],
            check=True,
        )
        return subprocess.run(
            ["git", "-C", str(repo), "rev-parse", "HEAD"],
            check=True, capture_output=True, text=True,
        ).stdout.strip()

    def test_removed_case_is_detected(self) -> None:
        base = "class T:\n    def test_kept(self):\n        self.assertTrue(1)\n    def test_removed(self):\n        self.assertTrue(2)\n"
        head = "class T:\n    def test_kept(self):\n        self.assertTrue(1)\n"

        errors = inventory.integrity_errors(base, head, "test_sample.py")

        self.assertTrue(any("test_removed" in error for error in errors))

    def test_fewer_assertions_are_detected(self) -> None:
        base = "def test_one(self):\n    self.assertTrue(1)\n    self.assertFalse(0)\n"
        head = "def test_one(self):\n    self.assertTrue(1)\n"

        errors = inventory.integrity_errors(base, head, "test_sample.py")

        self.assertTrue(any("fewer than" in error for error in errors))

    def test_deleted_guard_test_file_is_detected(self) -> None:
        base = {"scripts/port/test_unit_guard.py": "def test_one(self):\n    self.assertTrue(1)\n"}
        head = {"scripts/port/test_build_port_index.py": "def test_one(self):\n    self.assertTrue(1)\n"}

        errors = inventory.missing_file_errors(base, head)

        self.assertTrue(any("test_unit_guard.py" in error for error in errors))

    def test_renamed_guard_test_file_is_detected(self) -> None:
        base = {"scripts/port/test_unit_guard.py": "def test_one(self):\n    self.assertTrue(1)\n"}
        head = {"scripts/port/test_unit_guard_renamed.py": "def test_one(self):\n    self.assertTrue(1)\n"}

        errors = inventory.missing_file_errors(base, head)

        self.assertTrue(any("test_unit_guard.py" in error for error in errors))

    def test_a_real_deleted_guard_file_is_detected(self) -> None:
        with tempfile.TemporaryDirectory(prefix="guard-inventory-") as temporary:
            repo = Path(temporary)
            self._init(repo)
            path = repo / "scripts/port/test_unit_guard.py"
            path.parent.mkdir(parents=True)
            path.write_text("def test_one(self):\n    self.assertTrue(1)\n")
            base = self._commit(repo, "base")
            path.unlink()
            self._commit(repo, "delete the guard test")

            errors = inventory.check(repo, base, "HEAD")

        self.assertTrue(any("test_unit_guard.py" in error for error in errors))

    def test_a_real_renamed_guard_file_is_detected(self) -> None:
        with tempfile.TemporaryDirectory(prefix="guard-inventory-") as temporary:
            repo = Path(temporary)
            self._init(repo)
            path = repo / "scripts/port/test_unit_guard.py"
            path.parent.mkdir(parents=True)
            path.write_text("def test_one(self):\n    self.assertTrue(1)\n")
            base = self._commit(repo, "base")
            subprocess.run(
                ["git", "-C", str(repo), "mv",
                 "scripts/port/test_unit_guard.py",
                 "scripts/port/test_unit_guard_renamed.py"],
                check=True, capture_output=True,
            )
            self._commit(repo, "rename the guard test")

            errors = inventory.check(repo, base, "HEAD")

        self.assertTrue(any("test_unit_guard.py" in error for error in errors))

    def test_current_guard_suite_keeps_every_base_file_case_and_assertion(self) -> None:
        base = base_revision()
        if base is None:
            return
        errors = inventory.check(ROOT, base, "HEAD")
        self.assertEqual([], errors, "\n".join(errors))


if __name__ == "__main__":
    unittest.main()
