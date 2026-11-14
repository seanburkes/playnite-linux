import json
from pathlib import Path
import select
import subprocess
import sys
import tempfile
from threading import Event
import unittest

from tracker import Identity, Process, Session, Snapshot, parse_stat, snapshot


def process(pid, parent=1, ticks=10, state="S"):
    return Process(Identity(pid, ticks), parent, state)


class TrackingTests(unittest.TestCase):
    def test_stat_name_with_spaces_and_parentheses(self):
        fields = ["S", "1"] + ["0"] * 17 + ["987"]
        row = parse_stat("42 (game ) name (x)) " + " ".join(fields))
        self.assertEqual(row, process(42, ticks=987))

    def test_missing_stat_is_absent_but_malformed_stat_is_unknown(self):
        with tempfile.TemporaryDirectory(prefix="p1-007-") as directory:
            root = Path(directory)
            self.assertEqual(snapshot((42,), root), Snapshot(()))
            (root / "42").mkdir()
            (root / "42" / "stat").write_text("broken")
            self.assertFalse(snapshot((42,), root).complete)

    def test_reverse_order_proton_shaped_tree_reaches_fixed_point(self):
        session = Session(Identity(10, 10))
        rows = (process(13, 12), process(12, 11), process(11, 10), process(10),
                process(99))
        self.assertEqual(session.update(Snapshot(rows)), "running")
        self.assertEqual(session.known, {Identity(pid, 10) for pid in range(10, 14)})

    def test_observed_child_survives_parent_exit_and_reparenting(self):
        session = Session(Identity(10, 10))
        session.update(Snapshot((process(10), process(11, 10))))
        self.assertEqual(session.update(Snapshot((process(11, 1),))), "running")
        self.assertEqual(session.update(Snapshot(())), "stopped")

    def test_reused_root_pid_does_not_attach_new_descendants(self):
        session = Session(Identity(10, 10))
        session.update(Snapshot((process(10),)))
        self.assertEqual(session.update(Snapshot((process(10, ticks=20),
                                                 process(11, 10)))), "stopped")

    def test_reused_descendant_pid_does_not_extend_session(self):
        session = Session(Identity(10, 10))
        session.update(Snapshot((process(10), process(11, 10))))
        self.assertEqual(session.update(Snapshot((process(11, ticks=20),))), "stopped")

    def test_zombie_is_not_a_running_game(self):
        session = Session(Identity(10, 10))
        session.update(Snapshot((process(10),)))
        self.assertEqual(session.update(Snapshot((process(10, state="Z"),))), "stopped")

    def test_incomplete_snapshot_does_not_report_exit_or_lose_identity(self):
        session = Session(Identity(10, 10))
        session.update(Snapshot((process(10),)))
        self.assertEqual(session.update(Snapshot((), complete=False)), "unknown")
        self.assertEqual(session.update(Snapshot((process(10),))), "running")

    def test_broker_launch_exit_is_not_game_session_evidence(self):
        session = Session(Identity(10, 10))
        # A game started by an already-running Steam broker is not our child.
        self.assertEqual(session.update(Snapshot((process(20, 19),))), "unobserved")
        self.assertEqual(session.wait(lambda: Snapshot(()), Event(), 0.03), "timeout")

    def test_attributed_root_may_appear_after_initial_empty_sample(self):
        session = Session(Identity(10, 10))
        self.assertEqual(session.update(Snapshot(())), "unobserved")
        self.assertEqual(session.update(Snapshot((process(10),))), "running")

    def test_stopped_session_does_not_reattach_to_stale_identity(self):
        session = Session(Identity(10, 10))
        session.update(Snapshot((process(10),)))
        session.update(Snapshot(()))
        self.assertEqual(session.update(Snapshot((process(10),))), "stopped")

    def test_missed_short_lived_parent_cannot_be_reconstructed(self):
        session = Session(Identity(10, 10))
        session.update(Snapshot((process(10),)))
        self.assertEqual(session.update(Snapshot((process(12, 1),))), "stopped")
        # This is a documented false-stop limitation, not proof the game exited.

    def test_cancel_before_read_and_during_wait(self):
        session = Session(Identity(10, 10))
        cancel = Event()
        cancel.set()
        def unexpected_read():
            self.fail("read after pre-cancellation")
        self.assertEqual(session.wait(unexpected_read, cancel, 1), "cancelled")
        cancel.clear()
        def read_and_cancel():
            cancel.set()
            return Snapshot((process(10),))
        self.assertEqual(session.wait(read_and_cancel, cancel, 1), "cancelled")
        self.assertEqual(session.known, {Identity(10, 10)})
        cancel.clear()
        session.update(Snapshot((process(10),)))
        def exit_and_cancel():
            cancel.set()
            return Snapshot(())
        self.assertEqual(session.wait(exit_and_cancel, cancel, 1), "cancelled")
        self.assertEqual(session.known, {Identity(10, 10)})

    def test_disposed_session_rejects_observation(self):
        session = Session(Identity(10, 10))
        session.close()
        session.close()
        with self.assertRaises(RuntimeError):
            session.update(Snapshot(()))
        with self.assertRaises(RuntimeError):
            session.wait(lambda: Snapshot(()), Event(), 1)

    def exercise_live_tree(self, depth):
        child = subprocess.Popen(
            [sys.executable, str(Path(__file__).with_name("controlled_child.py")), str(depth)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        identities = set()
        try:
            self.assertTrue(select.select([child.stdout], [], [], 3)[0], "handshake timeout")
            pids = tuple(json.loads(child.stdout.readline()))
            self.assertEqual(len(pids), depth + 1)
            sample = snapshot(pids)
            self.assertTrue(sample.complete)
            self.assertEqual(len(sample.processes), depth + 1)
            root = next(p.identity for p in sample.processes if p.identity.pid == child.pid)
            identities = {p.identity for p in sample.processes}
            session = Session(root)
            self.assertEqual(session.update(sample), "running")
            self.assertEqual(session.known, identities)
            exit_observer = Session(root)
            exit_observer.update(sample)
            self.assertEqual(session.wait(lambda: snapshot(pids), Event(), 0.03), "timeout")
            cancel = Event()
            cancel.set()
            self.assertEqual(session.wait(lambda: snapshot(pids), cancel, 1), "cancelled")
            session.close()
            self.assertIsNone(child.poll(), "cancel/dispose must not kill owned game")
        finally:
            try:
                child.communicate("stop\n", timeout=10)
            except subprocess.TimeoutExpired:
                child.kill()
                child.communicate(timeout=2)
        self.assertEqual(child.returncode, 0)
        remaining = snapshot(tuple(i.pid for i in identities))
        self.assertTrue(remaining.complete)
        self.assertFalse(identities.intersection(p.identity for p in remaining.processes),
                         "owned process still present, including zombie")
        self.assertEqual(exit_observer.update(remaining), "stopped")

    def test_live_native_shaped_parent_and_game_cleanup(self):
        self.exercise_live_tree(1)

    def test_live_proton_shaped_wrapper_chain_cleanup(self):
        self.exercise_live_tree(3)


if __name__ == "__main__":
    if not sys.platform.startswith("linux") or not Path("/proc/self/stat").is_file():
        raise SystemExit("Linux /proc required; acceptance cannot be skipped")
    unittest.main(verbosity=2)
