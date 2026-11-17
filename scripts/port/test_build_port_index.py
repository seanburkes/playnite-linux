import io
import json
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

import build_port_index as port_index  # noqa: E402

QUEUE_HEADER = port_index.QUEUE_HEADER
LEDGER_HEADER = port_index.LEDGER_HEADER


def queue_row(
    unit_id,
    wave="W1",
    area="repo",
    unit="Thing",
    depends="-",
    status="todo",
    owner="captain",
    oracle="-",
    test="-",
    notes="",
):
    return "\t".join(
        [unit_id, wave, area, unit, depends, status, owner, oracle, test, notes]
    )


def ledger_row(
    timestamp,
    unit,
    from_status="todo",
    to_status="in-progress",
    actor="tester",
    evidence="evidence",
):
    return "\t".join([timestamp, unit, from_status, to_status, actor, evidence])


class PortIndexTests(unittest.TestCase):
    def write_fragment(self, unit, name, *rows):
        directory = self.units / f"{unit}.ledger.d"
        directory.mkdir(exist_ok=True)
        path = directory / name
        path.write_text(LEDGER_HEADER + "\n" + "".join(row + "\n" for row in rows))
        return path

    def test_fragments_follow_history_then_numeric_pr_then_filename_not_timestamp(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        history = ledger_row("2026-11-15T00:00:00Z", "p1-001", evidence="history")
        self.write_ledger_shard("p1-001", history)
        rows = [ledger_row(f"2026-11-{day}T00:00:00Z", "p1-001", evidence=name)
                for day, name in (("14", "10-last"), ("13", "2-z"), ("12", "2-a"))]
        for name, row in zip(("10-last.tsv", "2-z.tsv", "2-a.tsv"), rows):
            self.write_fragment("p1-001", name, row)
        before = self.render()
        self.assertEqual(before[1].decode().splitlines(), [LEDGER_HEADER, history, rows[2], rows[1], rows[0]])
        self.assertEqual(self.render(), before)
        snapshot = port_index.snapshot_from_dir(self.units, self.queue, self.ledger)
        self.assertEqual(len(snapshot.ledger_shards["p1-001"]), 4)

    def test_fragment_only_pr_passes_without_oracle_change_and_is_immutable_after_merge(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        path = self.write_fragment("p1-001", "123-proof.tsv", ledger_row("2026-11-15T00:00:00Z", "p1-001"))
        merged = self.commit("fragment only")
        status, output = self.check_with_git("--shards-only", "--against", base,
                                             "--forbid-aggregate-changes", "--pr", "123")
        self.assertEqual(0, status, output)
        self.assertEqual([], port_index.shard_ownership_errors(self.root, base, pr="123"))
        self.assertTrue(port_index.shard_ownership_errors(self.root, base, pr="124"))
        path.write_text(path.read_text().replace("evidence", "rewritten"))
        self.commit("rewrite fragment")
        self.assertTrue(port_index.shard_ownership_errors(self.root, merged))

    def test_cross_unit_queue_or_fragments_fail_ownership(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        base = self.start_git_history()
        self.write_queue_shard("p1-001", queue_row("p1-001", status="done"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.commit("two units")
        self.assertIn("exactly one unit", " ".join(port_index.shard_ownership_errors(self.root, base)))

    def test_legacy_append_is_rejected_by_pr_ownership(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001")
        base = self.start_git_history()
        self.write_ledger_shard("p1-001", ledger_row("2026-11-15T00:00:00Z", "p1-001"))
        self.commit("legacy append")
        self.assertIn("legacy history is frozen", " ".join(port_index.shard_ownership_errors(self.root, base)))

    def test_baseline_edit_fails_pr_guard_even_if_counts_rise(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        baseline = self.root / "tests/baseline.json"
        baseline.parent.mkdir()
        baseline.write_text('{"new": {}}\n')
        self.build()
        base = self.start_git_history()
        self.git("add", "tests")
        self.git("commit", "-q", "-m", "record baseline")
        base = self.git("rev-parse", "HEAD")
        baseline.write_text('{"new": {"Core": {"executed": 999}}}\n')
        self.git("add", "tests")
        self.commit("baseline edit")
        status, output = self.check_with_git("--shards-only", "--against", base, "--forbid-aggregate-changes")
        self.assertEqual(1, status)
        self.assertIn("changed: tests/baseline.json", output)

    def test_status_narrative_is_editable_but_generated_content_is_not(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        path = self.root / "docs/port/PORT-STATUS.md"
        path.write_text(f"# Port status\n{port_index.NARRATIVE_START}\nprose\n{port_index.NARRATIVE_END}\n")
        self.build()
        base = self.start_git_history()
        path.write_text(path.read_text().replace("prose", "revised prose"))
        self.commit("narrative only")
        self.assertEqual([], port_index.changed_aggregates(self.root, base, self.queue, self.ledger))
        path.write_text(path.read_text().replace("## Units", "## Made up"))
        self.commit("generated status edit")
        self.assertIn("docs/port/PORT-STATUS.md", port_index.changed_aggregates(self.root, base, self.queue, self.ledger))

    def test_status_fragment_regeneration_is_idempotent(self):
        self.write_queue_shard("p1-001", queue_row("p1-001", status="done"))
        self.write_fragment("p1-001", "123-proof.tsv", ledger_row("2026-11-15T00:00:00Z", "p1-001"))
        status = self.root / "docs/port/PORT-STATUS.md"
        status.write_text(f"{port_index.NARRATIVE_START}\nretained\n{port_index.NARRATIVE_END}\n")
        self.build()
        first = [path.read_bytes() for path in (self.queue, self.ledger, status)]
        self.build()
        self.assertEqual(first, [path.read_bytes() for path in (self.queue, self.ledger, status)])
        self.assertIn("| done | 1 |", status.read_text())
        self.assertIn("retained", status.read_text())

    def floor_fixture(self, floor=1):
        baseline = self.root / "tests/baseline.json"
        baseline.parent.mkdir()
        project = baseline.parent / "Core.csproj"
        project.write_text("<Project><IsTestProject>true</IsTestProject></Project>")
        old = {"new": {"Core": {"total": floor, "executed": floor, "passed": floor, "failed": 0, "skipped": 0}},
               "old_capture": {"notes": "historical provenance unchanged", "run_id": 1}, "empty_projects": {}}
        baseline.write_text(json.dumps(old))
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        head = self.start_git_history()
        self.git("add", "tests")
        self.git("commit", "-q", "-m", "record project and floor")
        head = self.git("rev-parse", "HEAD")
        results = self.root / "results"
        directory = results / "Core"
        directory.mkdir(parents=True)
        (directory / "Core.xml").write_text('<testsuite tests="2"><testcase name="a"/><testcase name="b"/></testsuite>')
        (directory / "exit-code.txt").write_text("0\n")
        counts = {"total": 2, "executed": 2, "passed": 2, "failed": 0, "skipped": 0}
        (results / "test-counts.json").write_text(json.dumps({"failures": [], "projects": {
            "Core": {"counts": counts, "exit_code": 0, "outcome": "pass"}}}))
        run = {"id": 123, "event": "push", "head_branch": "main", "conclusion": "success", "name": "linux-ci",
               "head_sha": head, "html_url": "https://example.invalid/actions/runs/123", "updated_at": "2026-11-17T00:00:00Z"}
        return baseline, results, run, old

    def test_floor_capture_uses_actual_cases_preserves_history_and_deduplicates(self):
        baseline, results, run, old = self.floor_fixture()
        first = port_index.capture_baseline(baseline, results, run, self.root)
        document = json.loads(first)
        self.assertEqual(document["old_capture"], old["old_capture"])
        self.assertEqual(2, document["new"]["Core"]["executed"])
        self.assertEqual(1, len(document["main_captures"]))
        baseline.write_bytes(first)
        self.assertEqual(first, port_index.capture_baseline(baseline, results, run, self.root))
        # The provenance note is editable history: re-capturing the same run must
        # not rewrite a hand-annotated record, and a same-count later run is a no-op.
        document["main_captures"][0]["notes"] += " Annotated after the fact."
        annotated = (json.dumps(document, indent=2) + "\n").encode()
        baseline.write_bytes(annotated)
        self.assertEqual(annotated, port_index.capture_baseline(baseline, results, run, self.root))
        run["id"] += 1
        self.assertEqual(annotated, port_index.capture_baseline(baseline, results, run, self.root))

    def test_floor_lowering_refuses_before_any_file_is_written(self):
        baseline, results, run, _ = self.floor_fixture(floor=3)
        before = baseline.read_bytes()
        with self.assertRaisesRegex(port_index.PortIndexError, "refusing to lower"):
            port_index.capture_baseline(baseline, results, run, self.root)
        self.assertEqual(before, baseline.read_bytes())

    def test_floor_capture_refuses_other_heads_pr_runs_and_fabricated_summary(self):
        baseline, results, run, _ = self.floor_fixture()
        for field, value in (("head_sha", "f" * 40), ("head_branch", "feature"), ("event", "pull_request"), ("conclusion", "failure")):
            with self.subTest(field=field), self.assertRaises(port_index.PortIndexError):
                port_index.capture_baseline(baseline, results, dict(run, **{field: value}), self.root)
        (results / "Core/exit-code.txt").write_text("8")
        with self.assertRaisesRegex(port_index.PortIndexError, "invalid or unsuccessful"):
            port_index.capture_baseline(baseline, results, run, self.root)

    def test_floor_capture_refuses_a_missing_recorded_project_without_retirement(self):
        baseline, results, run, _ = self.floor_fixture()
        document = json.loads(baseline.read_text())
        document["new"]["Gone.Tests"] = {
            "total": 5, "executed": 5, "passed": 5, "failed": 0, "skipped": 0,
        }
        baseline.write_text(json.dumps(document))
        with self.assertRaisesRegex(port_index.PortIndexError, "without a retirement record"):
            port_index.capture_baseline(baseline, results, run, self.root)

    def test_local_path_categories_name_the_offending_document_row(self):
        paths = ("$HOME/task-proof", "${HOME}/task-proof", "~/task-proof", r"C:\Users\alice\proof.log",
                 r"C:\Home\alice\proof.log", "C:/Home/alice/proof.log",
                 "/home/alice/firstmate/state/task.status", "/Users/alice/proof",
                 "/root/proof", "/pool/.treehouse/repo/docs")
        paths += tuple(f"{root}/task/proof.log" for root in port_index.LOCAL_TASK_ROOTS)
        for path in paths:
            with self.subTest(path=path):
                errors = port_index.local_path_errors("heading\nproof: " + path,
                                                      "docs/proof.md")
                self.assertEqual(len(errors), 1)
                self.assertIn("docs/proof.md:2:", errors[0])
                self.assertNotIn(path, errors[0])

    def test_repository_relative_references_are_allowed(self):
        self.assertEqual([], port_index.local_path_errors(
            "scripts/ci/test_ci_policy.py\ndocs/port/units/p2-ci-cost.ledger.tsv\n"
            "data/proof.log\nhttps://example.invalid/review",
            "docs/proof.md"))

    def test_conventional_home_and_system_paths_are_allowed(self):
        paths = ("~/.steam", "~/.local/share/Steam", "$HOME/.steam", "${HOME}/.config",
                 "~/.local", "$HOME/config", "docs/firstmate/data/proof.md", "/dev/null",
                 "/proc/meminfo", "/usr/bin", "/etc", "/sys", "/bin", "/sbin", "/lib",
                 "/run", "/media", "/Volumes", "/tmp", "/var", "/mnt", "/opt", "/srv")
        for path in paths:
            with self.subTest(path=path):
                self.assertEqual([], port_index.local_path_errors(
                    "Generic reference: " + path, "docs/proof.md"))

    def test_generic_markdown_label_does_not_exempt_private_link_target(self):
        for line in ("[~/.steam](/home/alice/firstmate/data/proof)",
                     "[~/.local/share/Steam]($HOME/tasks/private-proof)",
                     "~/.steam/firstmate/data/private-proof",
                     "~/.steam plus /home/alice/private-proof",
                     "/dev/null plus /home/alice/private-proof"):
            with self.subTest(line=line):
                self.assertEqual(1, len(port_index.local_path_errors(line, "docs/proof.md")))

    def test_alternate_personal_path_spellings_are_rejected(self):
        paths = ("~alice", "~alice/private-proof", "%USERPROFILE%",
                 "$env:USERPROFILE", r"C:\\Users\\alice\\proof",
                 r"C:\/Users\/alice\/proof", r"firstmate\\data\\task",
                 r"firstmate\/data\/task", r"\\server\share\proof",
                 "//server/share/proof",
                 r"\\server.example.org\share\proof",
                 "%2Fhome%2Falice%2Fproof", "%252Fhome%252Falice%252Fproof",
                 "C%3A%5CUsers%5Calice%5Cproof", "%25USERPROFILE%25%5Cproof",
                 "%5C%5Cserver%5Cshare%5Cproof", "firstmate%5C%5Cdata%5Ctask")
        for path in paths:
            with self.subTest(path=path):
                errors = port_index.local_path_errors(path, "docs/proof.md")
                self.assertEqual(1, len(errors))
                self.assertIn("docs/proof.md:1:", errors[0])

    def test_task_words_without_path_boundaries_are_allowed(self):
        for text in ("notfirstmate/data/task", "some.treehouse/task", "a.treehouse/task",
                     "firstmate and .treehouse are names", "docs/firstmate/data/task",
                     r"docs\\firstmate\\data\\task"):
            with self.subTest(text=text):
                self.assertEqual([], port_index.local_path_errors(text, "docs/proof.md"))

    def test_remote_target_ends_before_following_local_path(self):
        remote = "https://api.example.org/users/alice/home/root"
        for text in (f"[remote]({remote})/home/alice/proof",
                     f"{remote},/home/alice/proof",
                     f"{remote};/home/alice/proof",
                     f"{remote}|/home/alice/proof",
                     "https://example.org/%2Fhome%2Falice%2Fproof /home/bob/proof"):
            with self.subTest(text=text):
                self.assertEqual(1, len(port_index.local_path_errors(text, "docs/proof.md")))
        self.assertEqual([], port_index.local_path_errors(
            "https://example.org/%2Fhome%2Falice%2Fproof", "docs/proof.md"))

    def test_remote_urls_do_not_hide_a_local_path_on_the_same_line(self):
        target = "api.example.org/users/alice/home/root/firstmate/data/.treehouse/task"
        urls = [scheme + target for scheme in ("https://", "git://", "sftp://", "//", "")]
        for url in urls:
            self.assertEqual([], port_index.local_path_errors(url, "docs/proof.md"))
        for line in (urls[0], f"`{urls[1]}`", f"[review]({urls[2]})", f"<{urls[3]}>", urls[4]):
            with self.subTest(line=line):
                self.assertEqual([], port_index.local_path_errors(line, "docs/proof.md"))
                errors = port_index.local_path_errors(line + " proof: /home/alice/private/log",
                                                      "docs/proof.md")
                self.assertEqual(1, len(errors))
                self.assertIn("docs/proof.md:1:", errors[0])

    def test_missing_fixed_baseline_fails_closed(self):
        with patch.object(port_index, "PRE_GUARD_COMMIT", "f" * 40):
            port_index.local_path_allowance.cache_clear()
            with self.assertRaises(port_index.PortIndexError):
                port_index.local_path_errors("/home/alice/private/proof", "docs/proof.md")
        port_index.local_path_allowance.cache_clear()

    def test_markdown_in_every_documentation_location_is_scanned(self):
        for directory in ("spikes", "tests", "tools", "packaging", "src"):
            with self.subTest(directory=directory):
                path = self.root / directory / "component" / "README.md"
                path.parent.mkdir(parents=True)
                path.write_text("heading\n$HOME/private-proof\n")
                errors = port_index.document_path_errors(self.root)
                self.assertTrue(any(f"{directory}/component/README.md:2:" in error
                                    for error in errors))

    def test_local_path_in_queue_or_ledger_fails_render(self):
        for kind in ("queue", "ledger"):
            with self.subTest(kind=kind):
                self.write_queue_shard("p1-001", queue_row(
                    "p1-001", notes="/home/alice/proof" if kind == "queue" else ""))
                if kind == "ledger":
                    self.write_ledger_shard("p1-001", ledger_row(
                        "2026-11-15T00:00:00Z", "p1-001", evidence="/home/alice/proof"))
                with self.assertRaisesRegex(
                        port_index.PortIndexError, rf"p1-001\.{kind}\.tsv:2:"):
                    self.render()

    def test_historical_exceptions_are_exact_file_and_content_bound(self):
        for name in ("p2-002", "p2-ci-cost"):
            label = f"docs/port/units/{name}.ledger.tsv"
            lines = (port_index.ROOT / label).read_text().splitlines()
            for line in lines:
                if not port_index.LOCAL_PATH.search(line):
                    continue
                with self.subTest(unit=name, timestamp=line.split("\t")[0]):
                    self.assertEqual([], port_index.local_path_errors(line, label))
                    self.assertTrue(port_index.local_path_errors(line + " edited", label))
                    self.assertTrue(port_index.local_path_errors(line, "docs/copied.md"))
                    self.assertTrue(port_index.local_path_errors(line + "\n" + line, label))

    def test_build_and_check_reject_document_paths_before_writing_aggregates(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        (self.root / "docs/proof.md").write_text("heading\n/home/alice/proof\n")
        for mode in ("build", "check"):
            with self.subTest(mode=mode):
                stderr = io.StringIO()
                with redirect_stderr(stderr):
                    status = port_index.main([mode, "--shards-only"],
                                             units=self.units)
                self.assertEqual(1, status)
                self.assertIn("docs/proof.md:2:", stderr.getvalue())
                self.assertFalse(self.queue.exists())
                self.assertFalse(self.ledger.exists())

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="p1-011-")
        self.root = Path(self.temp.name)
        self.units = self.root / "docs" / "port" / "units"
        self.units.mkdir(parents=True)
        self.queue = self.root / "docs" / "port" / "queue.tsv"
        self.ledger = self.root / "docs" / "port" / "ledger.tsv"

    def tearDown(self):
        self.temp.cleanup()

    def write_queue_shard(self, unit_id, *rows, header=QUEUE_HEADER):
        body = "".join(row + "\n" for row in rows)
        (self.units / f"{unit_id}.queue.tsv").write_text(header + "\n" + body)

    def write_ledger_shard(self, unit_id, *rows, header=LEDGER_HEADER):
        body = "".join(row + "\n" for row in rows)
        (self.units / f"{unit_id}.ledger.tsv").write_text(header + "\n" + body)

    def write_corrections(self, *records, header=None):
        header = header or port_index.CORRECTIONS_HEADER
        body = "".join("\t".join(record) + "\n" for record in records)
        (self.units / port_index.CORRECTIONS_NAME).write_text(header + "\n" + body)

    def render(self):
        return port_index.render(self.units)

    def check(self):
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            status = port_index.main(
                ["check"],
                units=self.units,
            )
        return status, stdout.getvalue() + stderr.getvalue()

    def run_build(self):
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            status = port_index.main(
                ["build"],
                units=self.units,
            )
        return status, stdout.getvalue() + stderr.getvalue()

    def build(self):
        status, _ = self.run_build()
        self.assertEqual(status, 0)

    def write_base(self, queue_shards, ledger_shards=None, removed=None, corrections=None):
        """Materialise a base checkout and return its snapshot."""
        base = self.root / "base" / "docs" / "port"
        base_units = base / "units"
        base_units.mkdir(parents=True)
        for unit_id, row in queue_shards.items():
            (base_units / f"{unit_id}.queue.tsv").write_text(
                QUEUE_HEADER + "\n" + row + "\n"
            )
        for unit_id, rows in (ledger_shards or {}).items():
            body = "".join(row + "\n" for row in rows)
            (base_units / f"{unit_id}.ledger.tsv").write_text(
                LEDGER_HEADER + "\n" + body
            )
        if removed:
            body = "".join(
                f"{unit}\t{reason}\t{approved_by}\n"
                for unit, (reason, approved_by) in removed.items()
            )
            (base_units / "removed.tsv").write_text(
                port_index.REMOVED_HEADER + "\n" + body
            )
        if corrections:
            body = "".join("\t".join(record) + "\n" for record in corrections)
            (base_units / port_index.CORRECTIONS_NAME).write_text(
                port_index.CORRECTIONS_HEADER + "\n" + body
            )

        base_index = port_index.build_index(base_units)
        queue_bytes, ledger_bytes = port_index.render_index(base_index)
        base_queue = base / "queue.tsv"
        base_ledger = base / "ledger.tsv"
        base_queue.write_bytes(queue_bytes)
        base_ledger.write_bytes(ledger_bytes)
        return port_index.snapshot_from_dir(base_units, base_queue, base_ledger)

    def check_against(
        self,
        base,
        ref="BASE",
        *,
        ancestor_ok=True,
        allow_pre_migration_base=False,
    ):
        argv = ["check", "--against", ref]
        if allow_pre_migration_base:
            argv.append("--allow-pre-migration-base")
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            status = port_index.main(
                argv,
                units=self.units,
                base_loader=lambda _: base,
                ancestor_guard=lambda _: ancestor_ok,
            )
        return status, stdout.getvalue() + stderr.getvalue()

    def git(self, *args):
        completed = subprocess.run(
            ["git", "-C", str(self.root), *args],
            check=True,
            capture_output=True,
            text=True,
        )
        return completed.stdout.strip()

    def start_git_history(self):
        self.git("init", "-q")
        self.git("config", "user.email", "port-index@example.invalid")
        self.git("config", "user.name", "Port Index Test")
        self.git("add", "docs")
        self.git("commit", "-q", "-m", "base")
        return self.git("rev-parse", "HEAD")

    def commit(self, message):
        self.git("add", "docs")
        self.git("commit", "-q", "-m", message)
        return self.git("rev-parse", "HEAD")

    def check_with_git(self, *args):
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            status = port_index.main(
                ["check", *args],
                units=self.units,
            )
        return status, stdout.getvalue() + stderr.getvalue()

    def test_queue_is_sorted_by_wave_then_id_and_headers_are_exact(self):
        self.write_queue_shard("p2-001", queue_row("p2-001", wave="W2"))
        self.write_queue_shard("p1-002", queue_row("p1-002", wave="W1"))
        self.write_queue_shard("p10-001", queue_row("p10-001", wave="W10"))
        self.write_queue_shard("p1-001", queue_row("p1-001", wave="W1"))

        queue_bytes, _ = self.render()
        lines = queue_bytes.decode("utf-8").splitlines()

        self.assertEqual(lines[0], QUEUE_HEADER)
        self.assertEqual(
            [line.split("\t")[0] for line in lines[1:]],
            ["p1-001", "p1-002", "p2-001", "p10-001"],
        )

    def test_ledger_is_sorted_by_instant_then_unit(self):
        for unit_id in ("a", "b", "c"):
            self.write_queue_shard(unit_id, queue_row(unit_id))

        # Raw string order would put b first; the instant is 17:00Z,
        # tied with a, so a precedes b and c follows both.
        self.write_ledger_shard(
            "c", ledger_row("2026-11-14T17:30:00Z", "c")
        )
        self.write_ledger_shard(
            "b", ledger_row("2026-11-14T10:00:00-07:00", "b")
        )
        self.write_ledger_shard(
            "a", ledger_row("2026-11-14T17:00:00Z", "a")
        )

        _, ledger_bytes = self.render()
        lines = ledger_bytes.decode("utf-8").splitlines()

        self.assertEqual(lines[0], LEDGER_HEADER)
        self.assertEqual(
            [line.split("\t")[1] for line in lines[1:]],
            ["a", "b", "c"],
        )

    def test_empty_shard_directory_is_rejected(self):
        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("no queue shards found", str(raised.exception))

    def test_duplicate_unit_ids_are_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-001"))

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("duplicate unit id", str(raised.exception))

    def test_filename_and_id_must_agree(self):
        self.write_queue_shard("p1-001", queue_row("p1-002"))

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("does not match filename", str(raised.exception))

    def test_missing_columns_are_rejected(self):
        self.write_queue_shard(
            "p1-001",
            queue_row("p1-001"),
            header="\t".join(port_index.QUEUE_COLUMNS[:-1]),
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("header mismatch", str(raised.exception))

    def test_wrong_column_order_is_rejected(self):
        columns = list(port_index.QUEUE_COLUMNS)
        columns[1], columns[2] = columns[2], columns[1]
        self.write_queue_shard(
            "p1-001",
            queue_row("p1-001"),
            header="\t".join(columns),
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("header mismatch", str(raised.exception))

    def test_ragged_queue_row_is_rejected(self):
        self.write_queue_shard("p1-001", "\t".join(queue_row("p1-001").split("\t")[:-1]))

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("expected 10 columns", str(raised.exception))

    def test_blank_line_is_rejected(self):
        (self.units / "p1-001.queue.tsv").write_text(
            QUEUE_HEADER + "\n" + queue_row("p1-001") + "\n\n"
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("blank line", str(raised.exception))

    def test_queue_shard_needs_exactly_one_row(self):
        self.write_queue_shard(
            "p1-001", queue_row("p1-001"), queue_row("p1-001", wave="W2")
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("exactly one queue row", str(raised.exception))

    def test_ledger_shard_needs_a_queue_shard(self):
        self.write_ledger_shard("p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001"))

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("no queue shard", str(raised.exception))

    def test_ledger_unit_must_match_filename(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-002"))

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("does not match filename", str(raised.exception))

    def test_naive_timestamp_is_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001", ledger_row("2026-11-14T17:00:00", "p1-001"))

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("needs a UTC offset", str(raised.exception))

    def test_rebuild_is_byte_idempotent(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001"))

        self.build()
        first_queue = self.queue.read_bytes()
        first_ledger = self.ledger.read_bytes()

        self.build()

        self.assertEqual(self.queue.read_bytes(), first_queue)
        self.assertEqual(self.ledger.read_bytes(), first_ledger)
        self.assertEqual(first_queue, self.render()[0])
        self.assertEqual(first_ledger, self.render()[1])

    def test_check_accepts_generated_aggregates(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001"))
        self.build()

        status, output = self.check()

        self.assertEqual(status, 0, output)
        self.assertIn("port index is current", output)

    def test_check_rejects_a_hand_edited_queue(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        self.queue.write_text(
            QUEUE_HEADER + "\n" + queue_row("p1-001", status="done") + "\n"
        )

        status, output = self.check()

        self.assertEqual(status, 1)
        self.assertIn("is not generated output", output)
        self.assertIn(f"-{queue_row('p1-001', status='done')}", output)
        self.assertIn(f"+{queue_row('p1-001')}", output)

    def test_check_rejects_a_hand_edited_ledger(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001"))
        self.build()
        self.ledger.write_text(
            LEDGER_HEADER + "\n" + ledger_row("2026-11-14T18:00:00Z", "p1-001") + "\n"
        )

        status, output = self.check()

        self.assertEqual(status, 1)
        self.assertIn("is not generated output", output)

    def test_duplicate_ledger_rows_are_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        row = ledger_row("2026-11-14T17:00:00Z", "p1-001")
        self.write_ledger_shard("p1-001", row, row)

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("duplicate ledger row", str(raised.exception))

    def test_duplicate_ledger_rows_at_the_same_instant_are_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-14T17:00:00Z", "p1-001"),
            ledger_row("2026-11-14T12:00:00-05:00", "p1-001"),
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("duplicate ledger row", str(raised.exception))

    def test_unexpected_file_under_units_is_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        (self.units / "p1-002.queue.tsv.bak").write_text("backup")

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("unexpected entry", str(raised.exception))

    def test_wrong_case_suffix_is_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        (self.units / "p1-002.QUEUE.TSV").write_text(
            QUEUE_HEADER + "\n" + queue_row("p1-002") + "\n"
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("unexpected entry", str(raised.exception))

    def test_shard_in_a_subdirectory_is_rejected(self):
        nested = self.units / "nested"
        nested.mkdir()
        (nested / "p1-002.queue.tsv").write_text(
            QUEUE_HEADER + "\n" + queue_row("p1-002") + "\n"
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("unexpected entry", str(raised.exception))

    def test_directory_named_like_a_shard_is_rejected(self):
        (self.units / "p1-001.queue.tsv").mkdir()

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("not a regular file", str(raised.exception))

    def test_invalid_unit_id_in_filename_is_rejected(self):
        (self.units / "p1_001.queue.tsv").write_text(
            QUEUE_HEADER + "\n" + queue_row("p1_001") + "\n"
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("invalid unit id", str(raised.exception))

    def test_invalid_unit_id_in_row_is_rejected(self):
        (self.units / "p1-001.queue.tsv").write_text(
            QUEUE_HEADER + "\n" + queue_row("P1-001") + "\n"
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("invalid unit id", str(raised.exception))

    def test_invalid_utf8_shard_is_a_clean_error(self):
        (self.units / "p1-001.queue.tsv").write_bytes(b"\xff\xfe not utf-8")

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("invalid UTF-8", str(raised.exception))

    def test_check_against_base_accepts_unchanged_history(self):
        row = queue_row("p1-001")
        base = self.write_base(
            {"p1-001": row},
            {"p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")]},
        )
        self.write_queue_shard("p1-001", row)
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001")
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_migrating_a_legacy_ledger_into_history_loses_no_rows(self):
        row1 = ledger_row("2026-11-14T17:00:00Z", "p1-001", evidence="first")
        row2 = ledger_row("2026-11-15T17:00:00Z", "p1-001", evidence="second")
        base = self.write_base(
            {"p1-001": queue_row("p1-001")}, {"p1-001": [row1, row2]}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        directory = self.units / "p1-001.ledger.d"
        directory.mkdir()
        (directory / port_index.HISTORY_NAME).write_text(
            LEDGER_HEADER + "\n" + row1 + "\n" + row2 + "\n"
        )

        _, ledger_bytes = self.render()
        self.assertEqual([LEDGER_HEADER, row1, row2],
                         ledger_bytes.decode().splitlines())
        self.build()
        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)

    def test_history_file_is_first_and_fragments_keep_their_pr_order(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        history = ledger_row("2026-11-15T00:00:00Z", "p1-001", evidence="history")
        early = ledger_row("2026-11-01T00:00:00Z", "p1-001", evidence="2-early")
        late = ledger_row("2026-11-20T00:00:00Z", "p1-001", evidence="10-late")
        directory = self.units / "p1-001.ledger.d"
        directory.mkdir()
        (directory / port_index.HISTORY_NAME).write_text(
            LEDGER_HEADER + "\n" + history + "\n")
        self.write_fragment("p1-001", "10-late.tsv", late)
        self.write_fragment("p1-001", "2-early.tsv", early)

        _, ledger_bytes = self.render()

        self.assertEqual([LEDGER_HEADER, history, early, late],
                         ledger_bytes.decode().splitlines())
        snapshot = port_index.snapshot_from_dir(self.units, self.queue, self.ledger)
        self.assertEqual(3, len(snapshot.ledger_shards["p1-001"]))

    def test_history_migration_must_not_keep_the_legacy_row_too(self):
        row = ledger_row("2026-11-15T00:00:00Z", "p1-001")
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001", row)
        directory = self.units / "p1-001.ledger.d"
        directory.mkdir()
        (directory / port_index.HISTORY_NAME).write_text(LEDGER_HEADER + "\n" + row + "\n")

        with self.assertRaisesRegex(port_index.PortIndexError, "duplicate ledger row"):
            self.render()

    def test_check_against_base_rejects_a_deleted_queue_shard(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("queue shard 'p1-002'", output)
        self.assertIn("missing at head", output)

    def test_check_against_base_rejects_a_renamed_unit(self):
        base = self.write_base({"p1-001": queue_row("p1-001")})
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("renamed", output)
        self.assertIn("p1-001", output)
        self.assertIn("p1-002", output)

    def test_check_against_base_rejects_a_deleted_ledger_row(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001")},
            {
                "p1-001": [
                    ledger_row("2026-11-14T17:00:00Z", "p1-001"),
                    ledger_row(
                        "2026-11-14T18:00:00Z", "p1-001", to_status="done"
                    ),
                ]
            },
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001")
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("append-only", output)
        self.assertIn("missing or altered", output)

    def test_check_against_base_rejects_an_edited_base_row(self):
        original = ledger_row("2026-11-14T17:00:00Z", "p1-001", actor="alice")
        base = self.write_base(
            {"p1-001": queue_row("p1-001")}, {"p1-001": [original]}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001", actor="bob")
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("append-only", output)
        self.assertIn("missing or altered", output)

    def test_check_against_base_rejects_a_lost_aggregate_row(self):
        base = self.write_base({"p1-001": queue_row("p1-001")})
        extra = tuple(queue_row("p1-002").split("\t"))
        base = port_index.BaseSnapshot(
            base.queue_shards,
            base.ledger_shards,
            base.queue_aggregate + (extra,),
            base.ledger_aggregate,
            base.removed,
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("queue aggregate row for 'p1-002'", output)

    def test_check_against_base_rejects_a_deleted_retirement_record(self):
        base = self.write_base(
            {"p1-002": queue_row("p1-002")},
            removed={"p1-001": ("retired", "captain")},
        )
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("retirement record for 'p1-001' was deleted", output)

    def test_check_against_base_allows_reordered_rows_with_the_same_content(self):
        first = ledger_row("2026-11-14T17:00:00Z", "p1-001", to_status="done")
        second = ledger_row(
            "2026-11-14T17:00:00Z", "p1-001", from_status="done", to_status="released"
        )
        base = self.write_base(
            {"p1-001": queue_row("p1-001")}, {"p1-001": [first, second]}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001", second, first)
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_check_against_base_rejects_a_retained_row_plus_a_changed_copy(self):
        original = ledger_row("2026-11-14T17:00:00Z", "p1-001")
        base = self.write_base(
            {"p1-001": queue_row("p1-001")}, {"p1-001": [original]}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", original, ledger_row("2026-11-14T18:00:00Z", "p1-001")
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("matches base row", output)
        self.assertIn("except its timestamp", output)

    def test_check_against_base_rejects_a_retained_row_plus_a_corrected_copy(self):
        original = ledger_row("2026-11-14T17:00:00Z", "p1-001")
        base = self.write_base(
            {"p1-001": queue_row("p1-001")}, {"p1-001": [original]}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", original, ledger_row("2026-11-14T18:00:00Z", "p1-001")
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T18:00:00Z",
                "tester",
                "wrong timestamp",
            )
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("still present at head alongside the corrected copy", output)
        self.assertIn("does not append one", output)

    def test_check_against_base_rejects_a_row_moved_to_another_unit(self):
        row = ledger_row("2026-11-14T17:00:00Z", "p1-001")
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")},
            {"p1-001": [row], "p1-002": []},
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.write_ledger_shard("p1-001")
        self.write_ledger_shard(
            "p1-002", ledger_row("2026-11-14T17:00:00Z", "p1-002")
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("ledger shard 'p1-001'", output)
        self.assertIn("missing or altered", output)

    def test_ledger_order_is_total_for_rows_sharing_an_instant_and_unit(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        released = ledger_row(
            "2026-11-14T17:00:00Z", "p1-001", from_status="done", to_status="released"
        )
        started = ledger_row("2026-11-14T17:00:00Z", "p1-001", to_status="done")

        self.write_ledger_shard("p1-001", released, started)
        _, first = self.render()

        self.write_ledger_shard("p1-001", started, released)
        _, second = self.render()

        # The tiebreak is content, not shard position, so the bytes are stable.
        self.assertEqual(first, second)
        transitions = [line.split("\t")[3] for line in first.decode().splitlines()[1:]]
        self.assertEqual(transitions, ["released", "done"])

    def test_check_against_base_rejects_a_duplicated_ledger_row(self):
        row = ledger_row("2026-11-14T17:00:00Z", "p1-001")
        base = self.write_base({"p1-001": queue_row("p1-001")}, {"p1-001": [row]})
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard("p1-001", row, row)

        # Introduced duplicates are rejected while the index is built, before the
        # history comparison: the content set may never grow a second copy of a row.
        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("duplicate ledger row", output)

    def test_recorded_retirement_passes(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")},
            {"p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")]},
        )
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        (self.units / "removed.tsv").write_text(
            port_index.REMOVED_HEADER + "\np1-001\tmerged into p1-002\tcaptain\n"
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_appending_a_ledger_row_passes(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001")},
            {"p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")]},
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-14T17:00:00Z", "p1-001"),
            ledger_row("2026-11-14T18:00:00Z", "p1-001", to_status="done"),
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)

    def test_timestamp_correction_with_matching_record_passes(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")},
            {
                "p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")],
                "p1-002": [ledger_row("2026-11-14T18:00:00Z", "p1-002")],
            },
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T19:00:00Z", "p1-001")
        )
        self.write_ledger_shard(
            "p1-002", ledger_row("2026-11-14T18:00:00Z", "p1-002")
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "merged out of order",
            )
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_timestamp_change_without_a_correction_record_fails(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001")},
            {"p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")]},
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T19:00:00Z", "p1-001")
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("without a matching record", output)
        self.assertIn("ledger-corrections.tsv", output)

    def test_correction_that_edits_another_field_fails(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001")},
            {"p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001", actor="alice")]},
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T19:00:00Z", "p1-001", actor="bob")
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "merged out of order",
            )
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("only the timestamp may change", output)

    def test_one_correction_cannot_move_multiple_rows(self):
        first = ledger_row("2026-11-14T17:00:00Z", "p1-001")
        second = ledger_row("2026-11-14T18:00:00Z", "p1-001", to_status="done")
        base = self.write_base(
            {"p1-001": queue_row("p1-001")}, {"p1-001": [first, second]}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-14T19:00:00Z", "p1-001"),
            ledger_row("2026-11-14T20:00:00Z", "p1-001", to_status="done"),
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "merged out of order",
            )
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("missing or altered", output)

    def test_new_earlier_sorting_row_passes_and_build_orders_it(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001")},
            {"p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")]},
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-14T17:00:00Z", "p1-001"),
            ledger_row("2026-11-14T16:00:00Z", "p1-001", to_status="done"),
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)
        lines = self.ledger.read_text().splitlines()[1:]
        self.assertEqual(
            [line.split("\t")[0] for line in lines],
            ["2026-11-14T16:00:00Z", "2026-11-14T17:00:00Z"],
        )

    def test_build_accepts_a_new_earlier_sorting_row(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001")
        )
        self.build()

        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-14T17:00:00Z", "p1-001"),
            ledger_row("2026-11-14T16:00:00Z", "p1-001", to_status="done"),
        )

        status, output = self.run_build()

        self.assertEqual(status, 0, output)
        lines = self.ledger.read_text().splitlines()[1:]
        self.assertEqual(
            [line.split("\t")[0] for line in lines],
            ["2026-11-14T16:00:00Z", "2026-11-14T17:00:00Z"],
        )

    def test_build_accepts_an_appended_row_after_the_newest(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001")
        )
        self.build()
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-14T17:00:00Z", "p1-001"),
            ledger_row("2026-11-14T18:00:00Z", "p1-001", to_status="done"),
        )

        status, output = self.run_build()

        self.assertEqual(status, 0, output)
        self.assertIn(
            ledger_row("2026-11-14T18:00:00Z", "p1-001", to_status="done"),
            self.ledger.read_text(),
        )

    def test_correction_may_still_sort_before_other_units(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")},
            {
                "p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")],
                "p1-002": [ledger_row("2026-11-14T18:00:00Z", "p1-002")],
            },
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:30:00Z", "p1-001")
        )
        self.write_ledger_shard(
            "p1-002", ledger_row("2026-11-14T18:00:00Z", "p1-002")
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T17:30:00Z",
                "tester",
                "wrong timestamp, corrected to the recorded time",
            )
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_a_correction_may_not_backdate_a_row(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T18:00:00Z", "p1-001")
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T18:00:00Z",
                "2026-11-14T17:00:00Z",
                "tester",
                "moved earlier",
            )
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("must be later than previous timestamp", str(raised.exception))

    def test_second_correction_for_the_same_unit_is_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001")
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T18:00:00Z",
                "tester",
                "merged out of order",
            ),
            (
                "p1-001",
                "2026-11-14T18:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "corrected again",
            ),
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("multiple correction records", str(raised.exception))
        self.assertIn("corrected at most once", str(raised.exception))

    def test_correction_for_an_already_corrected_row_is_rejected(self):
        first = (
            "p1-001",
            "2026-11-14T17:00:00Z",
            "2026-11-14T18:00:00Z",
            "tester",
            "merged out of order",
        )
        base = self.write_base(
            {"p1-001": queue_row("p1-001")},
            {"p1-001": [ledger_row("2026-11-14T18:00:00Z", "p1-001")]},
            corrections=[first],
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T19:00:00Z", "p1-001")
        )
        self.write_corrections(
            first,
            (
                "p1-001",
                "2026-11-14T18:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "corrected again",
            ),
        )

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("multiple correction records", output)
        self.assertIn("corrected at most once", output)

    def test_correction_for_a_unit_without_a_queue_shard_is_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001")
        )
        self.write_corrections(
            (
                "p1-002",
                "2026-11-14T17:00:00Z",
                "2026-11-14T18:00:00Z",
                "tester",
                "dead record",
            )
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("no queue shard", str(raised.exception))
        self.assertIn("dead correction record", str(raised.exception))

    def test_correction_for_a_newly_retired_unit_is_rejected(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")},
            {
                "p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")],
                "p1-002": [ledger_row("2026-11-14T18:00:00Z", "p1-002")],
            },
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001")
        )
        (self.units / "removed.tsv").write_text(
            port_index.REMOVED_HEADER + "\np1-002\tretired\tcaptain\n"
        )
        self.write_corrections(
            (
                "p1-002",
                "2026-11-14T18:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "dead record",
            )
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("retired in this change", output)
        self.assertIn("correction only while the unit is live", output)
        self.assertIn("no ledger shard at head", output)

    def test_correction_for_a_unit_that_never_had_a_ledger_shard_is_rejected(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T18:00:00Z",
                "tester",
                "no ledger row to correct",
            )
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("no ledger shard to correct", str(raised.exception))

    def test_historical_correction_survives_a_later_retirement(self):
        correction = (
            "p1-002",
            "2026-11-14T17:00:00Z",
            "2026-11-14T18:00:00Z",
            "tester",
            "merged out of order",
        )
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")},
            {
                "p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")],
                "p1-002": [ledger_row("2026-11-14T18:00:00Z", "p1-002")],
            },
            corrections=[correction],
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T17:00:00Z", "p1-001")
        )
        (self.units / "removed.tsv").write_text(
            port_index.REMOVED_HEADER + "\np1-002\tretired\tcaptain\n"
        )
        self.write_corrections(correction)
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)
        self.assertIn("notice: ledger correction p1-002", output)

    def test_check_against_base_ignores_the_base_aggregate_order(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")},
            {
                "p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")],
                "p1-002": [ledger_row("2026-11-14T18:00:00Z", "p1-002")],
            },
        )
        reordered = port_index.BaseSnapshot(
            base.queue_shards,
            base.ledger_shards,
            base.queue_aggregate,
            tuple(reversed(base.ledger_aggregate)),
            base.removed,
            base.corrections,
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T19:00:00Z", "p1-001")
        )
        self.write_ledger_shard(
            "p1-002", ledger_row("2026-11-14T18:00:00Z", "p1-002")
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "merged out of order",
            )
        )
        self.build()

        status, output = self.check_against(reordered)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_correction_may_move_its_row_between_shard_positions(self):
        first = ledger_row("2026-11-14T17:00:00Z", "p1-001", to_status="done")
        second = ledger_row(
            "2026-11-14T18:00:00Z", "p1-001", from_status="done", to_status="released"
        )
        base = self.write_base(
            {"p1-001": queue_row("p1-001")}, {"p1-001": [first, second]}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row(
                "2026-11-14T18:00:00Z", "p1-001", from_status="done", to_status="released"
            ),
            ledger_row("2026-11-14T19:00:00Z", "p1-001", to_status="done"),
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "merged out of order",
            )
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_check_prints_a_notice_naming_the_correction_record(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001"), "p1-002": queue_row("p1-002")},
            {
                "p1-001": [ledger_row("2026-11-14T17:00:00Z", "p1-001")],
                "p1-002": [ledger_row("2026-11-14T18:00:00Z", "p1-002")],
            },
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T19:00:00Z", "p1-001")
        )
        self.write_ledger_shard(
            "p1-002", ledger_row("2026-11-14T18:00:00Z", "p1-002")
        )
        self.write_corrections(
            (
                "p1-001",
                "2026-11-14T17:00:00Z",
                "2026-11-14T19:00:00Z",
                "tester",
                "merged out of order",
            )
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn(
            "notice: ledger correction p1-001: 2026-11-14T17:00:00Z -> "
            "2026-11-14T19:00:00Z by tester (merged out of order)",
            output,
        )

    def test_existing_correction_records_are_append_only(self):
        base = self.write_base(
            {"p1-001": queue_row("p1-001")},
            {"p1-001": [ledger_row("2026-11-14T18:00:00Z", "p1-001")]},
            corrections=[
                (
                    "p1-001",
                    "2026-11-14T17:00:00Z",
                    "2026-11-14T18:00:00Z",
                    "tester",
                    "merged out of order",
                )
            ],
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001", ledger_row("2026-11-14T18:00:00Z", "p1-001")
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("append-only", output)
        self.assertIn("ledger-corrections.tsv", output)

    def _base_for_insertion(self):
        first = ledger_row("2026-11-14T17:00:00Z", "p1-001", to_status="done")
        second = ledger_row(
            "2026-11-14T18:00:00Z", "p1-001", from_status="done", to_status="released"
        )
        base = self.write_base(
            {"p1-001": queue_row("p1-001")}, {"p1-001": [first, second]}
        )
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        return base, first, second

    def test_check_against_base_allows_a_row_inserted_at_the_front(self):
        base, first, second = self._base_for_insertion()
        inserted = ledger_row("2026-11-14T16:00:00Z", "p1-001", actor="new")
        self.write_ledger_shard("p1-001", inserted, first, second)
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_check_against_base_allows_a_row_inserted_between_rows(self):
        base, first, second = self._base_for_insertion()
        inserted = ledger_row("2026-11-14T17:30:00Z", "p1-001", actor="inserted")
        self.write_ledger_shard("p1-001", first, inserted, second)
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_check_against_base_allows_an_earlier_sorting_appended_row(self):
        base, first, second = self._base_for_insertion()
        earlier = ledger_row("2026-11-14T16:00:00Z", "p1-001", actor="earlier")
        self.write_ledger_shard("p1-001", first, second, earlier)
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 0, output)
        self.assertIn("history matches BASE", output)

    def test_check_against_base_rejects_an_edited_retirement_reason(self):
        base = self.write_base(
            {"p1-002": queue_row("p1-002")},
            removed={"p1-001": ("retired", "captain")},
        )
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        (self.units / "removed.tsv").write_text(
            port_index.REMOVED_HEADER + "\np1-001\trewritten reason\tcaptain\n"
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("retirement record for 'p1-001' was edited", output)
        self.assertIn("permanent", output)

    def test_check_against_base_rejects_an_edited_retirement_approval(self):
        base = self.write_base(
            {"p1-002": queue_row("p1-002")},
            removed={"p1-001": ("retired", "captain")},
        )
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        (self.units / "removed.tsv").write_text(
            port_index.REMOVED_HEADER + "\np1-001\tretired\tsomeone-else\n"
        )
        self.build()

        status, output = self.check_against(base)

        self.assertEqual(status, 1)
        self.assertIn("retirement record for 'p1-001' was edited", output)

    def test_retirement_record_without_approved_by_column_is_rejected(self):
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        (self.units / "removed.tsv").write_text(
            "unit\treason\np1-001\tretired\n"
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("header mismatch", str(raised.exception))
        self.assertIn("approved-by", str(raised.exception))

    def test_retirement_record_with_empty_approved_by_is_rejected(self):
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        (self.units / "removed.tsv").write_text(
            port_index.REMOVED_HEADER + "\np1-001\tretired\t\n"
        )

        with self.assertRaises(port_index.PortIndexError) as raised:
            self.render()
        self.assertIn("needs a non-empty approved-by", str(raised.exception))

    def test_check_against_rejects_a_non_ancestor_ref(self):
        base = self.write_base({"p1-001": queue_row("p1-001")})
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()

        status, output = self.check_against(base, ancestor_ok=False)

        self.assertEqual(status, 1)
        self.assertIn("not a strict ancestor of HEAD", output)
        self.assertIn("not HEAD, a descendant", output)

    def test_strict_ancestor_guard_binds_history_to_an_ancestor(self):
        repo = self.root / "repo"
        repo.mkdir()

        def git(*args):
            return subprocess.run(
                ["git", "-C", str(repo), *args],
                check=True,
                capture_output=True,
                text=True,
            ).stdout.strip()

        git("init", "-q")
        git("config", "user.email", "port-index@example.invalid")
        git("config", "user.name", "Port Index Test")
        (repo / "a.txt").write_text("one\n")
        git("add", "a.txt")
        git("commit", "-q", "-m", "one")
        first = git("rev-parse", "HEAD")
        (repo / "a.txt").write_text("two\n")
        git("commit", "-qam", "two")
        second = git("rev-parse", "HEAD")

        self.assertTrue(port_index._is_strict_ancestor(repo, first))
        self.assertFalse(port_index._is_strict_ancestor(repo, second))
        self.assertFalse(port_index._is_strict_ancestor(repo, "HEAD"))

        git("checkout", "-q", "-b", "ahead")
        (repo / "a.txt").write_text("three\n")
        git("commit", "-qam", "three")
        descendant = git("rev-parse", "HEAD")
        git("checkout", "-q", "-")

        self.assertFalse(port_index._is_strict_ancestor(repo, descendant))
        self.assertFalse(port_index._is_strict_ancestor(repo, "does-not-exist"))

    def test_missing_units_base_requires_a_migration_acknowledgement(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()

        status, output = self.check_against(None)

        self.assertEqual(status, 1)
        self.assertIn("--allow-pre-migration-base", output)
        self.assertIn("history cannot be checked", output)
        self.assertNotIn("history matches", output)

    def test_migration_acknowledgement_prints_the_skip_notice(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()

        status, output = self.check_against(
            None, allow_pre_migration_base=True
        )

        self.assertEqual(status, 0, output)
        self.assertIn("notice:", output)
        self.assertIn("history check skipped for the migration PR", output)
        self.assertIn("port index is current", output)

    def test_shard_only_change_passes_without_regenerated_aggregates(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        self.write_queue_shard("p1-001", queue_row("p1-001", status="in-progress"))
        self.commit("shard only")

        stale_status, stale_output = self.check_with_git()

        self.assertEqual(stale_status, 1)
        self.assertIn("is not generated output", stale_output)

        status, output = self.check_with_git("--shards-only", "--against", base)

        self.assertEqual(status, 0, output)
        self.assertIn("shards are valid", output)
        self.assertIn(f"history matches {base}", output)

    def test_pull_request_with_only_a_shard_change_passes_the_ownership_gate(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        self.write_queue_shard("p1-001", queue_row("p1-001", status="in-progress"))
        self.commit("shard only")

        status, output = self.check_with_git(
            "--shards-only",
            "--against",
            base,
            "--forbid-aggregate-changes",
        )

        self.assertEqual(status, 0, output)
        self.assertIn("shards are valid", output)

    def test_pull_request_that_edits_an_aggregate_fails(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        self.queue.write_text(
            QUEUE_HEADER + "\n" + queue_row("p1-001", status="done") + "\n"
        )
        self.commit("aggregate only")

        status, output = self.check_with_git(
            "--shards-only",
            "--against",
            base,
            "--forbid-aggregate-changes",
        )

        self.assertEqual(status, 1)
        self.assertIn("must not modify generated or bot-owned files", output)
        self.assertIn("changed: docs/port/queue.tsv", output)

    def test_pull_request_that_regenerates_aggregates_still_fails_the_ownership_gate(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        self.write_queue_shard("p1-001", queue_row("p1-001", status="in-progress"))
        self.build()
        self.commit("shard plus regenerated aggregates")

        status, output = self.check_with_git(
            "--shards-only",
            "--against",
            base,
            "--forbid-aggregate-changes",
        )

        self.assertEqual(status, 1)
        self.assertIn("must not modify generated or bot-owned files", output)
        self.assertIn("changed: docs/port/queue.tsv", output)

    def test_shards_only_still_rejects_an_invalid_shard(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        self.write_queue_shard(
            "p1-001", "\t".join(queue_row("p1-001").split("\t")[:-1])
        )
        self.commit("broken shard")

        status, output = self.check_with_git("--shards-only", "--against", base)

        self.assertEqual(status, 1)
        self.assertIn("expected 10 columns", output)

    def test_post_merge_writer_refuses_an_invalid_shard_tree(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        queue_before = self.queue.read_bytes()
        ledger_before = self.ledger.read_bytes()
        self.write_queue_shard(
            "p1-001", "\t".join(queue_row("p1-001").split("\t")[:-1])
        )
        self.commit("broken shard")

        # This is the writer's validate-before-write step. It must fail and leave
        # the aggregates untouched rather than committing a laundered deletion.
        status, output = self.check_with_git("--shards-only", "--against", base)

        self.assertEqual(status, 1)
        self.assertIn("expected 10 columns", output)
        self.assertEqual(self.queue.read_bytes(), queue_before)
        self.assertEqual(self.ledger.read_bytes(), ledger_before)

    def test_aggregate_gate_uses_the_merge_base_when_main_moves_ahead(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        self.write_queue_shard("p1-001", queue_row("p1-001", status="in-progress"))
        self.build()
        main_tip = self.commit("main regenerates aggregates")

        self.git("checkout", "-q", "-b", "feature", base)
        self.write_queue_shard("p1-001", queue_row("p1-001", status="done"))
        self.commit("feature changes only its shard")

        two_dot = self.git(
            "diff", "--name-only", main_tip, "HEAD", "--", "docs/port/queue.tsv"
        )
        self.assertIn("docs/port/queue.tsv", two_dot)

        changed = port_index.changed_aggregates(
            self.root, main_tip, self.queue, self.ledger
        )

        self.assertEqual(changed, [])

    def test_port_index_workflow_is_main_only_and_validates_before_build(self):
        workflow = (
            port_index.ROOT / ".github" / "workflows" / "port-index.yml"
        ).read_text(encoding="utf-8")

        self.assertNotIn("workflow_dispatch", workflow)
        self.assertNotIn("[skip port-index]", workflow)
        self.assertNotIn("--force", workflow)
        self.assertIn("refs/heads/main", workflow)
        self.assertIn("GITHUB_EVENT_NAME", workflow)
        self.assertIn("workflow_run:", workflow)
        self.assertIn("actions/download-artifact@v4", workflow)
        self.assertIn("--capture-results", workflow)
        self.assertIn("tests/baseline.json", workflow)
        self.assertIn("docs/port/PORT-STATUS.md", workflow)
        self.assertIn("docs/port/units/ownership.paths", workflow)
        self.assertLess(
            workflow.index("check --shards-only --against \"$BASE_SHA\""),
            workflow.index("build_port_index.py build"),
        )

    def test_shards_only_still_rejects_a_deleted_queue_shard(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.build()
        base = self.start_git_history()
        self.git("rm", "-q", "docs/port/units/p1-002.queue.tsv")
        self.git("add", "docs")
        self.git("commit", "-q", "-m", "delete a shard")

        status, output = self.check_with_git("--shards-only", "--against", base)

        self.assertEqual(status, 1)
        self.assertIn("queue shard 'p1-002'", output)
        self.assertIn("missing at head", output)

    def test_forbid_aggregate_changes_requires_a_base_ref(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()

        status, output = self.check_with_git("--forbid-aggregate-changes")

        self.assertEqual(status, 1)
        self.assertIn("--forbid-aggregate-changes requires --against", output)

    def ownership_map(self):
        return self.units / "ownership.paths"

    def test_ownership_map_derives_from_ledger_recorded_pr_commits(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-15T00:00:00Z", "p1-001",
                       evidence="pull/50 delivered the path"),
        )
        self.build()
        self.start_git_history()
        product = self.root / "src" / "App" / "Thing.cs"
        product.parent.mkdir(parents=True)
        product.write_text("// thing\n")
        self.write_queue_shard("p1-001", queue_row("p1-001", status="done"))
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "feat: add thing (#50)")
        commit = self.git("rev-parse", "HEAD")

        self.build()

        rows = self.ownership_map().read_text().splitlines()
        self.assertEqual(port_index.OWNERSHIP_HEADER, rows[0])
        self.assertIn(f"src/App/Thing.cs\tp1-001\t{commit}\tpull/50", rows)

    def test_ownership_map_excludes_evidence_generated_and_build_files(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-15T00:00:00Z", "p1-001", evidence="pull/51"),
        )
        self.build()
        self.start_git_history()
        for relative in (
            "src/App/Thing.cs",
            "tests/App.Tests/ThingTests.cs",
            "tests/fixtures/v10-assets/assets.json",
            "tools/AssetCopy.CrashHost/Program.cs",
            "tests/fixtures/p1-001-source/README.md",
            "tests/golden/p1-001-golden/data.json",
            "tools/p1-001-tool/README.md",
            "tests/baseline.json",
            "tests/App.Tests/App.Tests.csproj",
        ):
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("{}\n")
        self.write_queue_shard("p1-001", queue_row("p1-001", status="done"))
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "feat: paths (#51)")

        self.build()

        body = self.ownership_map().read_text()
        for included in (
            "src/App/Thing.cs",
            "tests/App.Tests/ThingTests.cs",
            "tests/fixtures/v10-assets/assets.json",
            "tools/AssetCopy.CrashHost/Program.cs",
        ):
            self.assertIn(included + "\t", body)
        for excluded in (
            "tests/golden/p1-001-golden/data.json",
            "tests/fixtures/p1-001-source/README.md",
            "tools/p1-001-tool/README.md",
            "tests/baseline.json",
            "tests/App.Tests/App.Tests.csproj",
        ):
            self.assertNotIn(excluded + "\t", body)

    def test_ownership_map_ownership_follows_the_last_unit_mapped_commit(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_queue_shard("p1-002", queue_row("p1-002"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-15T00:00:00Z", "p1-001", evidence="pull/52"),
        )
        self.write_ledger_shard(
            "p1-002",
            ledger_row("2026-11-15T00:01:00Z", "p1-002", evidence="pull/53"),
        )
        self.build()
        self.start_git_history()
        product = self.root / "src" / "App" / "Thing.cs"
        product.parent.mkdir(parents=True)
        product.write_text("// one\n")
        self.write_queue_shard("p1-001", queue_row("p1-001", status="in-progress"))
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "feat: first (#52)")
        product.write_text("// two\n")
        self.write_queue_shard("p1-002", queue_row("p1-002", status="in-progress"))
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "feat: second (#53)")
        second = self.git("rev-parse", "HEAD")

        self.build()

        self.assertIn(
            f"src/App/Thing.cs\tp1-002\t{second}\tpull/53",
            self.ownership_map().read_text().splitlines(),
        )

    def test_ownership_map_ignores_a_commit_no_ledger_records(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        self.start_git_history()
        product = self.root / "src" / "App" / "Thing.cs"
        product.parent.mkdir(parents=True)
        product.write_text("// unrecorded\n")
        self.write_queue_shard("p1-001", queue_row("p1-001", status="done"))
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "chore: unrecorded (#55)")

        self.build()

        self.assertNotIn(
            "src/App/Thing.cs\t", self.ownership_map().read_text()
        )

    def test_ownership_map_is_byte_idempotent(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.write_ledger_shard(
            "p1-001",
            ledger_row("2026-11-15T00:00:00Z", "p1-001", evidence="pull/54"),
        )
        self.build()
        self.start_git_history()
        product = self.root / "src" / "App" / "Thing.cs"
        product.parent.mkdir(parents=True)
        product.write_text("// thing\n")
        self.write_queue_shard("p1-001", queue_row("p1-001", status="done"))
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "feat: thing (#54)")
        self.build()
        first = self.ownership_map().read_bytes()

        self.build()

        self.assertEqual(first, self.ownership_map().read_bytes())
        self.assertEqual(first, port_index.render_ownership_map(
            self.root, self.units, port_index.build_index(self.units)))

    def test_check_rejects_a_hand_edited_ownership_map(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        self.start_git_history()
        self.ownership_map().write_text(
            port_index.OWNERSHIP_HEADER
            + "\n"
            + "src/Fake.cs\tp1-001\t"
            + "a" * 40
            + "\tpull/1\n"
        )

        status, output = self.check()

        self.assertEqual(status, 1)
        self.assertIn("ownership.paths is not generated output", output)

    def test_forbid_aggregate_changes_rejects_the_ownership_map(self):
        self.write_queue_shard("p1-001", queue_row("p1-001"))
        self.build()
        base = self.start_git_history()
        self.ownership_map().write_text(
            port_index.OWNERSHIP_HEADER
            + "\n"
            + "src/Fake.cs\tp1-001\t"
            + "a" * 40
            + "\tpull/1\n"
        )
        self.git("add", "docs")
        self.git("commit", "-q", "-m", "edit ownership map")

        status, output = self.check_with_git(
            "--shards-only", "--against", base, "--forbid-aggregate-changes")

        self.assertEqual(status, 1)
        self.assertIn("changed: docs/port/units/ownership.paths", output)
        self.assertIn("bot-owned", output)


if __name__ == "__main__":
    unittest.main()
