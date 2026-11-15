import io
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
    def test_local_path_categories_name_the_offending_document_row(self):
        paths = ("$HOME/config", "${HOME}/config", "~/.local", r"C:\Users\alice\proof.log",
                 r"C:\Home\alice\proof.log", "C:/Home/alice/proof.log",
                 "/srv/firstmate/data/task/log", "firstmate/state/task.status",
                 "/pool/.treehouse/repo/docs")
        paths += tuple(f"/{root}/alice/proof.log" for root in port_index.LOCAL_ABSOLUTE_ROOTS)
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

    def test_remote_urls_do_not_hide_a_local_path_on_the_same_line(self):
        url = "https://api.example.org/users/alice/home/root/firstmate/data/.treehouse/task"
        for line in (url, f"`{url}`", f"[review]({url})", f"<{url}>"):
            with self.subTest(line=line):
                self.assertEqual([], port_index.local_path_errors(line, "docs/proof.md"))
                errors = port_index.local_path_errors(line + " proof: /opt/private/log",
                                                      "docs/proof.md")
                self.assertEqual(1, len(errors))
                self.assertIn("docs/proof.md:1:", errors[0])

    def test_missing_fixed_baseline_fails_closed(self):
        with patch.object(port_index, "PRE_GUARD_COMMIT", "f" * 40):
            port_index.local_path_allowance.cache_clear()
            with self.assertRaises(port_index.PortIndexError):
                port_index.local_path_errors("/opt/private/proof", "docs/proof.md")
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
                                             root=self.root, units=self.units,
                                             queue=self.queue, ledger=self.ledger)
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
                queue=self.queue,
                ledger=self.ledger,
            )
        return status, stdout.getvalue() + stderr.getvalue()

    def run_build(self):
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            status = port_index.main(
                ["build"],
                units=self.units,
                queue=self.queue,
                ledger=self.ledger,
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
                queue=self.queue,
                ledger=self.ledger,
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
                queue=self.queue,
                ledger=self.ledger,
                root=self.root,
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
        self.assertIn("must not modify the generated aggregates", output)
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
        self.assertIn("must not modify the generated aggregates", output)
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

    def test_port_index_workflow_is_push_main_only_and_validates_before_build(self):
        workflow = (
            port_index.ROOT / ".github" / "workflows" / "port-index.yml"
        ).read_text(encoding="utf-8")

        self.assertNotIn("workflow_dispatch", workflow)
        self.assertNotIn("[skip port-index]", workflow)
        self.assertNotIn("--force", workflow)
        self.assertIn("refs/heads/main", workflow)
        self.assertIn("GITHUB_EVENT_NAME", workflow)
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


if __name__ == "__main__":
    unittest.main()
