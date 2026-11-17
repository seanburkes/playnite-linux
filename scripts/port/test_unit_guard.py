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


def queue_row(unit: str, status: str, notes: str = "") -> str:
    return "\t".join([unit, "W4", "data", "Thing", "-", status, "tester", "-", "-", notes])


def ledger_row(unit: str, evidence: str) -> str:
    return "\t".join(["2026-11-16T00:00:00Z", unit, "in-progress", "done", "tester", evidence])


class Fixture:
    def __init__(self) -> None:
        self._temporary = tempfile.TemporaryDirectory(prefix="port-unit-guard-")
        self.root = Path(self._temporary.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Guard Test")
        self.git("config", "user.email", "guard@example.invalid")
        self.write("docs/port/units/p4-002.queue.tsv", QUEUE_HEADER + "\n" + queue_row("p4-002", "done") + "\n")
        self.write("docs/port/units/p4-002.ledger.tsv", LEDGER_HEADER + "\n")
        self.write("docs/port/units/p4-003.queue.tsv", QUEUE_HEADER + "\n" + queue_row("p4-003", "todo") + "\n")
        self.write("docs/port/units/p4-003.ledger.tsv", LEDGER_HEADER + "\n")
        self.write("docs/port/p4-002-import.md", "# p4-002 import\n")
        self.write("docs/port/p4-003-publication.md", "# p4-003 publication\n")
        self.write("docs/port/00-DECISIONS.md", "# decisions\n")
        self.write("tests/golden/p4-003-replacement/PROVENANCE.md", "provenance\n")
        self.write("tests/fixtures/p4-002-source/README.md", "fixture\n")
        self.write("tools/p4-002-tool/README.md", "tool\n")
        self.write("src/Playnite.Core/Thing.cs", "// product\n")
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
        return self.run(
            "check_unit_evidence.py",
            "--base",
            self.base,
            "--head",
            head,
            "--pr",
            "123",
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
        self.fixture.append("src/Playnite.Core/Thing.cs", "// process change\n")
        self.fixture.write(".github/workflows/process.yml", "name: process\n")
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
        self.assertIn("not an ancestor of PR head", result.stderr)

    def test_fragment_cannot_cite_the_merge_base_as_its_head(self) -> None:
        self.fixture.set_queue("p4-003", "in-progress")
        self.fixture.add_fragment(head=self.fixture.base)
        head = self.fixture.commit("fragment citing only pre-PR evidence")

        result = self.fixture.evidence(head, "--branch", "fm/p4-003")

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("names the merge base", result.stderr)
        self.assertIn("commit produced by this pull request", result.stderr)


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
