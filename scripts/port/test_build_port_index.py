import io
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

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

    def build(self):
        with redirect_stdout(io.StringIO()):
            status = port_index.main(
                ["build"],
                units=self.units,
                queue=self.queue,
                ledger=self.ledger,
            )
        self.assertEqual(status, 0)

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


if __name__ == "__main__":
    unittest.main()
