import subprocess
import sys
import unittest
from unittest.mock import patch

import probe


def stat(pid=42, start=123, name="synthetic (game) name"):
    # Linux stat fields 3 through 22; starttime is field 22.
    return f"{pid} ({name}) S " + "0 " * 18 + str(start) + " 0 0"


class IdentityTests(unittest.TestCase):
    def test_stat_with_spaces_and_parentheses(self):
        self.assertEqual(probe.identity(stat(), "pid:[1]"),
                         {"pid": 42, "start_ticks": 123, "namespace": "pid:[1]"})

    def test_pid_reuse_is_not_visibility(self):
        expected = probe.identity(stat(), "pid:[1]")
        reused = probe.identity(stat(start=124), "pid:[1]")
        self.assertEqual(probe.compare_visibility(expected, lambda _: reused), "different-identity")

    def test_same_number_in_different_namespace_is_not_visibility(self):
        expected = probe.identity(stat(), "pid:[1]")
        other = probe.identity(stat(), "pid:[2]")
        self.assertEqual(probe.compare_visibility(expected, lambda _: other), "different-identity")

    def test_matching_identity(self):
        expected = probe.identity(stat(), "pid:[1]")
        self.assertEqual(probe.compare_visibility(expected, lambda _: expected), "same-identity")

    def test_missing_process_is_inconclusive(self):
        with patch("probe.read_identity", side_effect=FileNotFoundError) as reader:
            self.assertEqual(probe.compare_visibility({"pid": 42}, reader), "not-visible-or-exited")

    def test_denied_process_is_not_success(self):
        with patch("probe.read_identity", side_effect=PermissionError) as reader:
            self.assertEqual(probe.compare_visibility({"pid": 42}, reader), "permission-denied")


class TransportTests(unittest.TestCase):
    def test_host_gate_requires_existing_sandbox(self):
        self.assertIn("not inside", probe.host_gate(False, True))

    def test_host_gate_requires_spawn(self):
        self.assertIn("unavailable", probe.host_gate(True, False))

    def test_host_gate_allows_available_transport_only(self):
        self.assertIsNone(probe.host_gate(True, True))

    def test_host_command_is_fixed_argument_list(self):
        self.assertEqual(probe.child_command(True)[:-1],
                         ["flatpak-spawn", "--host", "--watch-bus", "/usr/bin/python3", "-u", "-c"])

    def test_controlled_local_child_exit_and_visibility(self):
        result = probe.run_child(probe.child_command())
        self.assertEqual(result["result"], "observed")
        self.assertEqual(result["caller_proc_visibility"], "same-identity")
        self.assertTrue(result["exit_status_forwarded"])
        self.assertEqual(result["stderr"], "")

    def test_failed_launch_is_evidence_not_success(self):
        result = probe.run_child([sys.executable, "-c", "import sys; sys.exit(7)"])
        self.assertEqual(result["result"], "launch-failed")
        self.assertEqual(result["exit_code"], 7)

    def test_unresponsive_owned_child_is_reaped(self):
        real_popen = subprocess.Popen
        children = []

        def capture(*args, **kwargs):
            child = real_popen(*args, **kwargs)
            children.append(child)
            return child

        with patch("probe.subprocess.Popen", side_effect=capture), \
                patch("probe.select.select", return_value=([], [], [])):
            with self.assertRaises(TimeoutError):
                probe.run_child(probe.child_command())
        self.assertIsNotNone(children[0].returncode)


if __name__ == "__main__":
    unittest.main()
