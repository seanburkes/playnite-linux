"""Negative proofs for the port unit ownership and evidence guards.

Every case drives the real command-line checks against a disposable git
repository.  Deleting or weakening either check makes these tests fail; no test
inspects an internal function as a substitute for the command's exit status.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "scripts" / "port"

QUEUE_HEADER = "\t".join(
    ["id", "wave", "area", "unit", "depends", "status", "owner", "oracle", "test", "notes"]
)
LEDGER_HEADER = "\t".join(["timestamp", "unit", "from", "to", "actor", "evidence"])
OWNERSHIP_HEADER = "path\tunit\tcommit\tpull"


def queue_row(unit: str, status: str, notes: str = "") -> str:
    return "\t".join([unit, "W4", "data", "Thing", "-", status, "tester", "-", "-", notes])


def ledger_row(unit: str, evidence: str) -> str:
    return "\t".join(["2026-11-16T00:00:00Z", unit, "in-progress", "done", "tester", evidence])


def claim(path: str, unit: str, pull: int = 96) -> tuple[str, str, str, str]:
    return (path, unit, "a" * 40, f"pull/{pull}")


class Fixture:
    def __init__(
        self,
        ownership: list[tuple[str, str, str, str]] | None = None,
        files: dict[str, str] | None = None,
    ) -> None:
        self._temporary = tempfile.TemporaryDirectory(prefix="port-unit-guard-")
        self.root = Path(self._temporary.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Guard Test")
        self.git("config", "user.email", "guard@example.invalid")
        self.write("docs/port/units/p4-002.queue.tsv", QUEUE_HEADER + "\n" + queue_row("p4-002", "done") + "\n")
        self.write("docs/port/units/p4-002.ledger.tsv", LEDGER_HEADER + "\n")
        self.write("docs/port/units/p4-003.queue.tsv", QUEUE_HEADER + "\n" + queue_row("p4-003", "todo") + "\n")
        self.write("docs/port/units/p4-003.ledger.tsv", LEDGER_HEADER + "\n")
        self.write("docs/port/units/p4-005.queue.tsv", QUEUE_HEADER + "\n" + queue_row("p4-005", "done") + "\n")
        self.write("docs/port/units/p4-005.ledger.tsv", LEDGER_HEADER + "\n")
        self.write("docs/port/p4-002-import.md", "# p4-002 import\n")
        self.write("docs/port/p4-003-publication.md", "# p4-003 publication\n")
        self.write("docs/port/00-DECISIONS.md", "# decisions\n")
        self.write("tests/golden/p4-003-replacement/PROVENANCE.md", "provenance\n")
        self.write("tests/fixtures/p4-002-source/README.md", "fixture\n")
        self.write("tools/p4-002-tool/README.md", "tool\n")
        self.write("src/Playnite.Core/Thing.cs", "// product\n")
        for path, content in (files or {}).items():
            self.write(path, content)
        self.commit("fixture root")
        self.set_ownership(ownership or [])
        self.base = self.commit("base")

    def cleanup(self) -> None:
        self._temporary.cleanup()

    def git(self, *args: str) -> str:
        result = subprocess.run(
            ["git", "-C", str(self.root), *args],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if result.returncode != 0:
            raise AssertionError(
                f"git {' '.join(args)} failed: {result.stderr.decode('utf-8', 'replace')}"
            )
        return result.stdout.decode("utf-8", "strict")

    def write(self, relative: str, content: str) -> None:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def append(self, relative: str, content: str) -> None:
        with (self.root / relative).open("a", encoding="utf-8") as handle:
            handle.write(content)

    def set_ownership(self, rows: list[tuple[str, str, str, str]]) -> None:
        commits: dict[int, str] = {}
        for _path, _unit, _commit, pull in rows:
            if pull.startswith("pull/") and int(pull[len("pull/"):]) not in commits:
                commits[int(pull[len("pull/"):])] = self.commit(
                    f"evidence (#{pull[len('pull/'):]})"
                )
        lines = [OWNERSHIP_HEADER]
        for path, unit, commit, pull in rows:
            if pull.startswith("pull/"):
                commit = commits[int(pull[len("pull/"):])]
            lines.append("\t".join((path, unit, commit, pull)))
        self.write("docs/port/units/ownership.paths", "\n".join(lines) + "\n")

    def commit(self, message: str) -> str:
        self.git("add", "-A")
        self.git("commit", "--allow-empty", "-q", "-m", message)
        return self.git("rev-parse", "HEAD").strip()

    def set_queue(self, unit: str, status: str, notes: str = "") -> None:
        self.write(
            f"docs/port/units/{unit}.queue.tsv",
            QUEUE_HEADER + "\n" + queue_row(unit, status, notes) + "\n",
        )

    def add_fragment(
        self,
        *,
        unit: str = "p4-003",
        pr: int = 123,
        head: str,
        run: str = "https://github.com/example/repo/actions/runs/43964833995",
        name: str | None = None,
        evidence: str | None = None,
    ) -> str:
        relative = f"docs/port/units/{unit}.ledger.d/{name or f'{pr}-evidence.tsv'}"
        if evidence is None:
            evidence = f"PR #{pr} head {head} linux-ci {run}"
        self.write(relative, LEDGER_HEADER + "\n" + ledger_row(unit, evidence) + "\n")
        return relative

    def run(self, script: str, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(SCRIPTS / script), "--repo", str(self.root), *args],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            check=False,
        )

    def ownership(self, head: str, *args: str) -> subprocess.CompletedProcess[str]:
        return self.run(
            "check_unit_ownership.py",
            "--base",
            self.base,
            "--head",
            head,
            *args,
        )

    def evidence(self, head: str, *args: str) -> subprocess.CompletedProcess[str]:
        # Pin the expected repository slug so the test is hermetic even when
        # GitHub Actions exports GITHUB_REPOSITORY for the outer checkout.
        return self.run(
            "check_unit_evidence.py",
            "--base",
            self.base,
            "--head",
            head,
            "--pr",
            "123",
            "--repo-slug",
            "example/repo",
            *args,
        )


class OwnershipGuardTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = Fixture()
        self.addCleanup(self.fixture.cleanup)

    def test_two_units_fail(self) -> None:
        self.fixture.append("docs/port/p4-002-import.md", "foreign\n")
        self.fixture.append("docs/port/p4-003-publication.md", "own\n")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=self.fixture.base)
        head = self.fixture.commit("carry two units")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("unit: MULTIPLE", result.stdout)
        self.assertIn("docs/port/p4-002-import.md (unit p4-002)", result.stdout)
        self.assertIn("docs/port/p4-003-publication.md", result.stdout)
        self.assertIn("at most one unit evidence namespace", result.stderr)

    def test_one_foreign_unit_fails(self) -> None:
        self.fixture.append("docs/port/p4-002-import.md", "foreign bytes\n")
        head = self.fixture.commit("carry a foreign unit only")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("unit: p4-003", result.stdout)
        self.assertIn("docs/port/p4-002-import.md (unit p4-002)", result.stdout)
        self.assertIn("foreign unit evidence", result.stderr)
        self.assertIn("PR owns p4-003 but touches unit p4-002", result.stderr)

    def test_only_own_unit_passes(self) -> None:
        self.fixture.append("docs/port/p4-003-publication.md", "own\n")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=self.fixture.base)
        head = self.fixture.commit("own unit only")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("unit: p4-003", result.stdout)
        self.assertIn("docs/port/units/p4-003.queue.tsv", result.stdout)

    def test_process_pr_touching_no_unit_evidence_passes(self) -> None:
        self.fixture.write(".github/workflows/process.yml", "name: process\n")
        self.fixture.append("docs/port/00-DECISIONS.md", "process note\n")
        head = self.fixture.commit("process only")

        ownership = self.fixture.ownership(head, "--branch", "fm/port-u2")
        evidence = self.fixture.evidence(head, "--branch", "fm/port-u2")

        self.assertEqual(ownership.returncode, 0, ownership.stderr)
        self.assertIn("unit: (none)", ownership.stdout)
        self.assertEqual(evidence.returncode, 0, evidence.stderr)
        self.assertIn("unit: (none)", evidence.stdout)

    def test_ambiguous_diff_fails_closed(self) -> None:
        # p4-002 is a prefix of p4-002-extra, so this document path matches
        # both unit namespaces and cannot be attributed safely.
        self.fixture.write(
            "docs/port/units/p4-002-extra.queue.tsv",
            QUEUE_HEADER + "\n" + queue_row("p4-002-extra", "todo") + "\n",
        )
        self.fixture.write("docs/port/p4-002-extra-report.md", "two unit prefixes\n")
        head = self.fixture.commit("ambiguous unit path")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("ambiguous unit attribution", result.stderr)
        self.assertIn("docs/port/p4-002-extra-report.md", result.stderr)

    def test_unrecognised_units_entry_fails_closed(self) -> None:
        self.fixture.write("docs/port/units/p4-003.queue.tsv.bak", "backup\n")
        head = self.fixture.commit("unrecognised units entry")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("unrecognised path under docs/port/units", result.stderr)

    def test_historical_96_97_shape_fails(self) -> None:
        # #97 was a p4-003 PR whose tree also carried the p4-002 import
        # documentation bytes.  Even with a valid p4-003 queue transition and
        # fragment, the foreign p4-002 evidence must fail.
        self.fixture.append("docs/port/p4-003-publication.md", "own contract\n")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=self.fixture.base)
        self.fixture.append("docs/port/p4-002-import.md", "another unit's bytes\n")
        head = self.fixture.commit("p4-003 PR carrying p4-002 bytes")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("docs/port/p4-002-import.md (unit p4-002)", result.stdout)
        self.assertIn("docs/port/units/p4-003.queue.tsv", result.stdout)
        self.assertIn("at most one unit evidence namespace", result.stderr)

    def test_cross_unit_product_file_fails_with_map_evidence(self) -> None:
        path = "src/Playnite.Core/Import/AssetCopy.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-005")], files={path: "// asset copy\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// foreign product bytes\n")
        head = self.fixture.commit("p4-003 carries p4-005 product")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn(f"{path} (unit p4-005; pull/96 at commit ", result.stdout)
        self.assertIn("foreign unit ownership", result.stderr)
        self.assertIn("owned by p4-005", result.stderr)

    def test_cross_unit_shared_test_fails(self) -> None:
        path = "tests/Playnite.Core.Tests/Import/AssetCopyTests.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-005")], files={path: "// asset tests\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// foreign test bytes\n")
        head = self.fixture.commit("p4-003 carries p4-005 tests")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn(f"{path} (unit p4-005", result.stdout)
        self.assertIn("foreign unit ownership", result.stderr)

    def test_cross_unit_fixture_and_tool_fail(self) -> None:
        fixture = "tests/fixtures/v10-assets/assets.json"
        tool = "tools/AssetCopy.CrashHost/Program.cs"
        self.fixture = Fixture(
            ownership=[claim(fixture, "p4-005"), claim(tool, "p4-005")],
            files={fixture: "{}\n", tool: "// crash host\n"},
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(fixture, "\n")
        self.fixture.append(tool, "// foreign\n")
        head = self.fixture.commit("p4-003 carries p4-005 fixture and tool")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn(f"{fixture} (unit p4-005", result.stdout)
        self.assertIn(f"{tool} (unit p4-005", result.stdout)
        self.assertIn("foreign unit ownership", result.stderr)

    def test_foreign_owned_path_without_a_declaration_fails(self) -> None:
        path = "src/Playnite.Core/Import/AssetCopy.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-005")], files={path: "// asset copy\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// undeclared change\n")
        head = self.fixture.commit("no unit declaration")

        result = self.fixture.ownership(head, "--branch", "fm/port-u2")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("declares no unit", result.stderr)
        self.assertIn(f"{path} (unit p4-005", result.stderr)

    def test_marker_declaration_is_explicit(self) -> None:
        path = "src/Playnite.Core/Import/Own.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-003", pull=123)], files={path: "// own\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// work\n")
        implementation = self.fixture.commit("implementation")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=implementation)
        head = self.fixture.commit("Port-Unit: p4-003\n\nevidence")

        ownership = self.fixture.ownership(head, "--branch", "fm/port-u2")
        evidence = self.fixture.evidence(head, "--branch", "fm/port-u2")

        self.assertEqual(ownership.returncode, 0, ownership.stderr)
        self.assertIn("unit: p4-003", ownership.stdout)
        self.assertEqual(evidence.returncode, 0, evidence.stderr)
        self.assertIn("unit: p4-003", evidence.stdout)

    def test_conflicting_marker_and_branch_fail(self) -> None:
        path = "src/Playnite.Core/Import/Own.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-003", pull=123)], files={path: "// own\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// work\n")
        head = self.fixture.commit("Port-Unit: p4-003")

        result = self.fixture.ownership(head, "--branch", "fm/p4-002")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("conflicting units", result.stderr)

    def test_first_map_may_be_added_by_a_process_pr(self) -> None:
        self.fixture.git("rm", "-q", "docs/port/units/ownership.paths")
        self.fixture.base = self.fixture.commit("base without a map")
        self.fixture.write("docs/port/units/ownership.paths", OWNERSHIP_HEADER + "\n")
        head = self.fixture.commit("seed the first map")

        result = self.fixture.ownership(head, "--branch", "fm/port-u2")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("unit: (none)", result.stdout)

    def test_first_map_addition_with_a_product_change_fails(self) -> None:
        self.fixture.git("rm", "-q", "docs/port/units/ownership.paths")
        self.fixture.base = self.fixture.commit("base without a map")
        self.fixture.write("docs/port/units/ownership.paths", OWNERSHIP_HEADER + "\n")
        self.fixture.append("src/Playnite.Core/Thing.cs", "// product\n")
        head = self.fixture.commit("seed a map with a product change")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("bot-owned", result.stderr)

    def test_bot_owned_map_cannot_be_edited_by_a_pr(self) -> None:
        self.fixture.set_ownership([claim("src/Playnite.Core/Thing.cs", "p4-003")])
        head = self.fixture.commit("edit ownership map")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("bot-owned", result.stderr)
        self.assertIn("ownership.paths", result.stderr)

    def test_ambiguous_map_rows_fail_closed(self) -> None:
        path = "src/Playnite.Core/Thing.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-003"), claim(path, "p4-003")]
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// change\n")
        head = self.fixture.commit("ambiguous map")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("duplicate owner", result.stderr)
        self.assertIn(path, result.stderr)

    def test_multi_owner_path_accepts_any_listed_owner(self) -> None:
        path = "src/Playnite.Core/Import/LibraryMigration.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-003"), claim(path, "p4-005")],
            files={path: "// shared\n"},
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// p4-003 change\n")
        head = self.fixture.commit("shared path")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("unit: p4-003", result.stdout)
        self.assertIn("ownership:", result.stdout)
        self.assertIn("p4-005", result.stdout)

    def test_multi_owner_path_rejects_a_non_owner(self) -> None:
        path = "src/Playnite.Core/Import/LibraryMigration.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-003"), claim(path, "p4-005")],
            files={path: "// shared\n"},
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// foreign change\n")
        head = self.fixture.commit("non-owner")

        result = self.fixture.ownership(head, "--branch", "fm/p4-002")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("foreign unit ownership", result.stderr)
        self.assertIn("p4-003", result.stderr)
        self.assertIn("p4-005", result.stderr)

    def test_path_traversal_diff_entry_fails_closed(self) -> None:
        diff = self.fixture.root.parent / "traversal.diff"
        diff.write_text("../escape.cs\n", encoding="utf-8")
        head = self.fixture.commit("no changes")

        result = self.fixture.run(
            "check_unit_ownership.py",
            "--head",
            head,
            "--diff-file",
            str(diff),
            "--unit",
            "p4-003",
        )

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("path traversal", result.stderr.lower())
        self.assertIn("../escape.cs", result.stderr)

    def test_diff_file_rename_charges_both_paths(self) -> None:
        path = "src/Playnite.Core/Import/AssetCopy.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-005")], files={path: "// asset copy\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        diff = self.fixture.root.parent / "rename.diff"
        diff.write_text(
            f"R100\t{path}\tsrc/Playnite.Core/Import/Moved.cs\n", encoding="utf-8"
        )
        head = self.fixture.commit("no changes")

        result = self.fixture.run(
            "check_unit_ownership.py",
            "--base",
            self.fixture.base,
            "--head",
            head,
            "--diff-file",
            str(diff),
            "--unit",
            "p4-003",
        )

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn(f"{path} (unit p4-005", result.stdout)
        self.assertIn("foreign unit ownership", result.stderr)

    def test_escaping_symlink_fails_closed(self) -> None:
        path = self.fixture.root / "docs/port/p4-003-publication.md"
        path.unlink()
        path.symlink_to("/etc/passwd")
        head = self.fixture.commit("absolute symlink")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("symlink", result.stderr)
        self.assertIn("outside the repository", result.stderr)

    def test_symlink_chain_to_a_path_outside_the_repository_fails(self) -> None:
        self.fixture.write("tests/fixtures/v10-assets/assets.json", "{}\n")
        (self.fixture.root / "tests/fixtures/v10-assets/escape").symlink_to("/etc/passwd")
        self.fixture.base = self.fixture.commit("base with escaping target")
        (self.fixture.root / "tests/fixtures/p4-003-link.json").symlink_to(
            "v10-assets/escape"
        )
        head = self.fixture.commit("chained symlink")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("symlink", result.stderr)
        self.assertIn("outside the repository", result.stderr)

    def test_symlink_to_a_foreign_owned_path_fails_closed(self) -> None:
        target = "tests/fixtures/v10-assets/assets.json"
        link = "tests/fixtures/p4-003-link.json"
        self.fixture = Fixture(
            ownership=[claim(target, "p4-005")], files={target: "{}\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        (self.fixture.root / link).symlink_to("v10-assets/assets.json")
        head = self.fixture.commit("symlink to foreign bytes")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("symlink", result.stderr)
        self.assertIn("owned by p4-005", result.stderr)

    def test_rename_and_deletion_of_a_foreign_path_fail(self) -> None:
        path = "src/Playnite.Core/Import/AssetCopy.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-005")], files={path: "// asset copy\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        (self.fixture.root / path).rename(self.fixture.root / "src/Playnite.Core/Import/Moved.cs")
        head = self.fixture.commit("rename foreign path")

        renamed = self.fixture.ownership(head, "--branch", "fm/p4-003")
        self.assertEqual(renamed.returncode, 1, renamed.stderr)
        self.assertIn(f"{path} (unit p4-005", renamed.stdout)

        (self.fixture.root / "src/Playnite.Core/Import/Moved.cs").unlink()
        head = self.fixture.commit("delete moved path")
        deleted = self.fixture.ownership(head, "--branch", "fm/p4-003")
        self.assertEqual(deleted.returncode, 1, deleted.stderr)
        self.assertIn("foreign unit ownership", deleted.stderr)

    def test_reconstructed_96_97_shape_fails_on_real_names(self) -> None:
        paths = [
            "src/Playnite.Core/Import/AssetCopy.cs",
            "tests/Playnite.Core.Tests/Import/AssetCopyTests.cs",
            "tests/fixtures/v10-assets/assets.json",
            "tools/AssetCopy.CrashHost/Program.cs",
        ]
        self.fixture = Fixture(
            ownership=[claim(path, "p4-005") for path in paths],
            files={path: "owned by p4-005\n" for path in paths},
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append("docs/port/p4-003-publication.md", "own contract\n")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=self.fixture.base)
        for path in paths:
            self.fixture.append(path, "foreign bytes\n")
        head = self.fixture.commit("p4-003 PR carrying p4-005 product and tests")

        ownership = self.fixture.ownership(head, "--branch", "fm/p4-003")
        evidence = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(ownership.returncode, 1, ownership.stderr)
        for path in paths:
            self.assertIn(f"{path} (unit p4-005", ownership.stdout)
        self.assertIn("foreign unit ownership", ownership.stderr)
        self.assertIn("owned by p4-005", ownership.stderr)
        self.assertEqual(evidence.returncode, 1, evidence.stderr)
        self.assertIn("unit-owned paths owned only by another unit", evidence.stderr)

    def test_legitimate_single_unit_still_passes(self) -> None:
        path = "src/Playnite.Core/Import/Own.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-003", pull=123)], files={path: "// own\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// implementation\n")
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=implementation)
        head = self.fixture.commit("evidence head")

        ownership = self.fixture.ownership(head, "--branch", "fm/p4-003")
        evidence = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(ownership.returncode, 0, ownership.stderr)
        self.assertIn("unit: p4-003", ownership.stdout)
        self.assertIn(f"ownership: {path}", ownership.stdout)
        self.assertEqual(evidence.returncode, 0, evidence.stderr)
        self.assertIn("unit: p4-003", evidence.stdout)

    def test_unit_declared_pr_without_fragment_fails(self) -> None:
        path = "src/Playnite.Core/Import/Own.cs"
        self.fixture = Fixture(
            ownership=[claim(path, "p4-003", pull=123)], files={path: "// own\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.append(path, "// no evidence\n")
        head = self.fixture.commit("product only")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("carries no unit evidence", result.stderr)
        self.assertIn("missing required ledger fragment", result.stderr)
        self.assertIn(f"{path} (unit p4-003", result.stderr)


    def test_product_change_with_a_missing_map_fails_closed(self) -> None:
        self.fixture.git("rm", "-q", "docs/port/units/ownership.paths")
        self.fixture.base = self.fixture.commit("base without a map")
        self.fixture.append("src/Playnite.Core/Thing.cs", "// new work\n")
        head = self.fixture.commit("product without a map")

        result = self.fixture.ownership(head, "--branch", "fm/port-u2")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("ownership map unavailable", result.stderr)
        self.assertIn("src/Playnite.Core/Thing.cs", result.stderr)

    def test_product_change_with_an_empty_map_fails_closed(self) -> None:
        self.fixture.append("src/Playnite.Core/New.cs", "// new work\n")
        head = self.fixture.commit("product with an empty map")

        result = self.fixture.ownership(head, "--branch", "fm/port-u2")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("ownership map unavailable", result.stderr)
        self.assertIn("has no rows", result.stderr)

    def test_unowned_new_product_path_still_needs_a_declaration(self) -> None:
        path = "src/Playnite.Core/Import/New.cs"
        self.fixture = Fixture(
            ownership=[claim("src/Playnite.Core/Import/Owned.cs", "p4-003")],
            files={"src/Playnite.Core/Import/Owned.cs": "// owned\n"},
        )
        self.addCleanup(self.fixture.cleanup)
        self.fixture.write(path, "// brand new\n")
        head = self.fixture.commit("unowned new path")

        result = self.fixture.ownership(head, "--branch", "fm/port-u2")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("declares no unit", result.stderr)
        self.assertIn(path, result.stderr)

    def test_unowned_new_product_path_needs_a_fragment_when_declared(self) -> None:
        self.fixture.write("src/Playnite.Core/Import/New.cs", "// brand new\n")
        head = self.fixture.commit("product only")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("carries no unit evidence", result.stderr)
        self.assertIn("src/Playnite.Core/Import/New.cs", result.stderr)

    def test_unowned_new_product_path_passes_with_its_fragment(self) -> None:
        self.fixture.write("src/Playnite.Core/Import/New.cs", "// brand new\n")
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=implementation)
        head = self.fixture.commit("evidence head")

        ownership = self.fixture.ownership(head, "--branch", "fm/p4-003")
        evidence = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(ownership.returncode, 0, ownership.stderr)
        self.assertEqual(evidence.returncode, 0, evidence.stderr)

    def test_new_file_under_another_unit_directory_is_foreign(self) -> None:
        self.fixture.write("tests/fixtures/p4-005-newdir/file.txt", "foreign\n")
        head = self.fixture.commit("foreign unit directory")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("tests/fixtures/p4-005-newdir/file.txt (unit p4-005", result.stdout)
        self.assertIn("foreign unit evidence", result.stderr)

    def test_new_file_under_another_unit_docs_directory_is_foreign(self) -> None:
        self.fixture.write("docs/port/p4-005/notes.md", "foreign\n")
        head = self.fixture.commit("foreign docs directory")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("docs/port/p4-005/notes.md (unit p4-005", result.stdout)

    def test_forged_map_commit_not_an_ancestor_fails_closed(self) -> None:
        path = "src/Playnite.Core/Thing.cs"
        self.fixture.write(
            "docs/port/units/ownership.paths",
            OWNERSHIP_HEADER + "\n" + f"{path}\tp4-003\t{'b' * 40}\tpull/96\n",
        )
        self.fixture.base = self.fixture.commit("forged map")
        self.fixture.append(path, "// change\n")
        head = self.fixture.commit("product change")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("not an ancestor of the map revision", result.stderr)

    def test_map_pull_must_agree_with_the_commit_subject(self) -> None:
        path = "src/Playnite.Core/Thing.cs"
        evidence = self.fixture.commit("evidence (#96)")
        self.fixture.write(
            "docs/port/units/ownership.paths",
            OWNERSHIP_HEADER + "\n" + f"{path}\tp4-003\t{evidence}\tpull/97\n",
        )
        self.fixture.base = self.fixture.commit("misstated evidence")
        self.fixture.append(path, "// change\n")
        head = self.fixture.commit("product change")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("does not agree with recorded pull/97", result.stderr)

    def test_map_naming_an_unknown_unit_fails_closed(self) -> None:
        path = "src/Playnite.Core/Thing.cs"
        evidence = self.fixture.commit("evidence (#96)")
        self.fixture.write(
            "docs/port/units/ownership.paths",
            OWNERSHIP_HEADER + "\n" + f"{path}\tp4-999\t{evidence}\tpull/96\n",
        )
        self.fixture.base = self.fixture.commit("unknown owner")
        self.fixture.append(path, "// change\n")
        head = self.fixture.commit("product change")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("unknown unit", result.stderr)

    def test_symlink_through_a_symlinked_ancestor_directory_fails(self) -> None:
        (self.fixture.root / "tests/fixtures/ancestor-dir").symlink_to("/etc")
        self.fixture.base = self.fixture.commit("base with an escaping ancestor")
        (self.fixture.root / "tests/fixtures/p4-003-link").symlink_to(
            "ancestor-dir/passwd"
        )
        head = self.fixture.commit("link through an escaping ancestor")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("outside the repository", result.stderr)

    def test_symlink_to_a_foreign_directory_fails(self) -> None:
        target = "tests/fixtures/v10-assets/assets.json"
        self.fixture = Fixture(
            ownership=[claim(target, "p4-005")], files={target: "{}\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        (self.fixture.root / "tests/fixtures/p4-003-dir").symlink_to("v10-assets")
        head = self.fixture.commit("link to a foreign directory")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("symlink", result.stderr)
        self.assertIn("owned by p4-005", result.stderr)

    def test_symlink_to_a_foreign_file_through_an_ancestor_fails(self) -> None:
        target = "tests/fixtures/v10-assets/assets.json"
        self.fixture = Fixture(
            ownership=[claim(target, "p4-005")], files={target: "{}\n"}
        )
        self.addCleanup(self.fixture.cleanup)
        (self.fixture.root / "tests/fixtures/ancestor-file").symlink_to(
            "v10-assets/assets.json"
        )
        self.fixture.base = self.fixture.commit("base with ancestor file")
        (self.fixture.root / "tests/fixtures/p4-003-filelink").symlink_to(
            "ancestor-file"
        )
        head = self.fixture.commit("link through an ancestor file")

        result = self.fixture.ownership(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("owned by p4-005", result.stderr)

    def test_fragment_cannot_cite_a_main_commit_merged_after_the_branch_point(self) -> None:
        branch = self.fixture.git("rev-parse", "--abbrev-ref", "HEAD").strip()
        self.fixture.git("checkout", "-q", "-b", "mainline")
        main_side = self.fixture.commit("main side commit")
        self.fixture.git("checkout", "-q", branch)
        self.fixture.write("src/Playnite.Core/Thing.cs", "// branch work\n")
        self.fixture.commit("branch commit")
        self.fixture.git("merge", "--no-ff", "-q", "-m", "merge mainline", "mainline")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=main_side)
        head = self.fixture.commit("fragment citing a main-side commit")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("unrelated ancestor's run", result.stderr)
        self.assertIn(main_side, result.stderr)


class EvidenceGuardTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = Fixture()
        self.addCleanup(self.fixture.cleanup)

    def test_unit_pr_without_fragment_fails(self) -> None:
        self.fixture.set_queue("p4-003", "in-progress")
        head = self.fixture.commit("queue only")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("missing required ledger fragment", result.stderr)
        self.assertIn("docs/port/units/p4-003.ledger.d/123-<slug>.tsv", result.stderr)

    def test_unit_branch_with_no_evidence_at_all_fails(self) -> None:
        self.fixture.append("src/Playnite.Core/Thing.cs", "// implementation\n")
        head = self.fixture.commit("implementation without evidence")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("carries no unit evidence", result.stderr)
        self.assertIn("docs/port/units/p4-003.queue.tsv", result.stderr)
        self.assertIn("docs/port/units/p4-003.ledger.d/123-<slug>.tsv", result.stderr)

    def test_fragment_and_queue_transition_passes(self) -> None:
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=implementation)
        head = self.fixture.commit("evidence head")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("unit: p4-003", result.stdout)
        self.assertIn("queue: docs/port/units/p4-003.queue.tsv status in-progress", result.stdout)
        self.assertIn("fragment: docs/port/units/p4-003.ledger.d/123-evidence.tsv", result.stdout)

    def test_unchanged_queue_row_fails(self) -> None:
        implementation = self.fixture.commit("implementation head")
        # Notes-only edit leaves the row status in todo, so it is not evidence.
        self.fixture.set_queue("p4-003", "todo", notes="pretend progress")
        self.fixture.add_fragment(head=implementation)
        head = self.fixture.commit("todo queue plus fragment")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("must move it to a real evidence state", result.stderr)
        self.assertIn("'todo'", result.stderr)

    def test_fragment_without_run_fails(self) -> None:
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(
            head=implementation,
            evidence=f"PR #123 head {implementation}",
        )
        head = self.fixture.commit("fragment without run")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("does not name the linux-ci run", result.stderr)

    def test_fragment_without_pr_fails(self) -> None:
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(
            head=implementation,
            evidence=f"head {implementation} actions/runs/43964833995",
        )
        head = self.fixture.commit("fragment without PR")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("does not name the pull request", result.stderr)

    def test_fragment_filename_must_name_current_pr(self) -> None:
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=implementation, name="999-evidence.tsv")
        head = self.fixture.commit("fragment for another PR")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("names PR #999, not the current PR #123", result.stderr)

    def test_fragment_run_must_belong_to_this_repository(self) -> None:
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(
            head=implementation,
            run="https://github.com/other/repo/actions/runs/43964833995",
        )
        head = self.fixture.commit("fragment citing another repository")
        # Supplying the slug without verification still rejects a foreign URL.
        result = self.fixture.run(
            "check_unit_evidence.py",
            "--base",
            self.fixture.base,
            "--head",
            head,
            "--pr",
            "123",
            "--branch",
            "fm/p4-003",
            "--repo-slug",
            "example/repo",
        )

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("cites CI runs outside example/repo", result.stderr)

    def test_foreign_unit_evidence_also_fails_the_evidence_check(self) -> None:
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=implementation)
        self.fixture.append("docs/port/p4-002-import.md", "foreign\n")
        head = self.fixture.commit("own evidence plus foreign bytes")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("touches foreign unit evidence", result.stderr)
        self.assertIn("docs/port/p4-002-import.md (unit p4-002)", result.stderr)

    def test_fragment_must_name_an_ancestor_head(self) -> None:
        self.fixture.set_queue("p4-003", "in-progress")
        # An all-zero id cannot resolve to an ancestor.
        self.fixture.add_fragment(head="1" * 40)
        head = self.fixture.commit("fragment naming an unrelated commit")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn(
            "not the pull request head or a commit produced by this pull request",
            result.stderr,
        )

    def test_fragment_cannot_cite_the_merge_base_as_its_head(self) -> None:
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=self.fixture.base)
        head = self.fixture.commit("fragment citing only pre-PR evidence")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("names the merge base", result.stderr)
        self.assertIn("commit produced by this pull request", result.stderr)

    def test_fragment_cannot_cite_an_unrelated_base_ancestor_run(self) -> None:
        self.fixture.commit("pre-PR commit")
        pre_pr = self.fixture.git("rev-parse", "HEAD").strip()
        self.fixture.base = self.fixture.commit("base after pre-PR commit")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=pre_pr)
        head = self.fixture.commit("fragment citing an unrelated ancestor")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("unrelated ancestor's run", result.stderr)
        self.assertIn(pre_pr, result.stderr)

    def test_fragment_can_cite_a_commit_produced_by_this_pr(self) -> None:
        implementation = self.fixture.commit("implementation head")
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=implementation)
        head = self.fixture.commit("evidence head")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"head {implementation}", result.stdout)


class WorkflowShapeTests(unittest.TestCase):
    def test_guard_workflow_runs_both_checks_without_a_label_escape(self) -> None:
        workflow = (ROOT / ".github" / "workflows" / "port-unit-guard.yml").read_text(
            encoding="utf-8"
        )

        self.assertIn("pull_request:", workflow)
        self.assertIn("check_unit_ownership.py", workflow)
        self.assertIn("check_unit_evidence.py", workflow)
        self.assertIn("name: unit-ownership", workflow)
        self.assertIn("name: unit-evidence", workflow)
        self.assertIn("github.event.pull_request.base.sha", workflow)
        self.assertIn("github.event.pull_request.head.sha", workflow)
        self.assertNotIn("labels.", workflow)
        self.assertNotIn("workflow_dispatch", workflow)
        self.assertNotIn("continue-on-error", workflow)
        self.assertNotIn("paths:", workflow)


if __name__ == "__main__":
    if shutil.which("git") is None:
        raise SystemExit("git is required for the port unit guard tests")
    unittest.main()
