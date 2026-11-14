import json
import subprocess
import sys
import unittest
from unittest.mock import Mock, patch

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


class FramingTests(unittest.TestCase):
    def read_chunks(self, chunks):
        stream = Mock()
        with patch("probe.select.select", return_value=([stream], [], [])), \
                patch("probe.os.read", side_effect=chunks):
            return probe.read_record(stream)

    def test_complete_and_fragmented_records_are_equal(self):
        record = b'{"pid":42,"name":"\xc3\xa9"}\n'
        complete = self.read_chunks([record])
        fragmented = self.read_chunks([bytes([byte]) for byte in record])
        self.assertEqual(complete, record)
        self.assertEqual(json.loads(fragmented), json.loads(complete))

    def test_empty_eof_is_not_a_partial_record(self):
        self.assertEqual(self.read_chunks([b""]), b"")

    def test_partial_eof_requires_newline_even_for_valid_json(self):
        with self.assertRaisesRegex(EOFError, "before newline"):
            self.read_chunks([b'{"pid":42}', b""])

    def test_partial_timeout_is_not_malformed_json(self):
        stream = Mock()
        with patch("probe.select.select", side_effect=[([stream], [], []), ([], [], [])]), \
                patch("probe.os.read", return_value=b'{"pid":') as reader:
            with self.assertRaisesRegex(TimeoutError, "7 bytes received"):
                probe.read_record(stream)
        self.assertEqual(reader.call_count, 1)

    def test_fragment_reads_share_one_deadline(self):
        stream = Mock()
        with patch("probe.time.monotonic", side_effect=[10, 10, 11, 11, 12]), \
                patch("probe.select.select", return_value=([stream], [], [])) as select, \
                patch("probe.os.read", side_effect=[b'{"pid":', b'42}\n']):
            self.assertEqual(probe.read_record(stream), b'{"pid":42}\n')
        self.assertEqual([call.args[3] for call in select.call_args_list], [5, 4])

    def test_no_read_after_total_deadline(self):
        stream = Mock()
        with patch("probe.time.monotonic", side_effect=[10, 10, 14, 15]), \
                patch("probe.select.select", return_value=([stream], [], [])) as select, \
                patch("probe.os.read", return_value=b'{"pid":') as reader:
            with self.assertRaises(TimeoutError):
                probe.read_record(stream)
        self.assertEqual(select.call_count, 1)
        self.assertEqual(reader.call_count, 1)

    def test_record_completing_at_deadline_is_timeout(self):
        with patch("probe.time.monotonic", side_effect=[10, 10, 15]):
            with self.assertRaises(TimeoutError):
                self.read_chunks([b'{}\n'])

    def test_record_at_byte_limit_including_newline_is_accepted(self):
        record = b'"' + b'x' * (probe.MAX_RECORD_BYTES - 3) + b'"\n'
        self.assertEqual(self.read_chunks([record[:-1], record[-1:]]), record)

    def test_oversized_framed_record_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "4096-byte limit"):
            self.read_chunks([b'x' * probe.MAX_RECORD_BYTES + b'\n'])

    def test_unframed_output_is_bounded_across_reads(self):
        stream = Mock()
        with patch("probe.select.select", return_value=([stream], [], [])), \
                patch("probe.os.read", side_effect=[b'x' * probe.MAX_RECORD_BYTES, b'x']) as reader:
            with self.assertRaisesRegex(ValueError, "4096-byte limit"):
                probe.read_record(stream)
        self.assertEqual([call.args[1] for call in reader.call_args_list],
                         [probe.MAX_RECORD_BYTES + 1, 1])


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

    def test_fragmented_local_child_still_reports_visibility_and_exit(self):
        real_popen = subprocess.Popen
        real_read = probe.os.read
        children = []

        def capture(*args, **kwargs):
            child = real_popen(*args, **kwargs)
            children.append(child)
            return child

        def short_read(fd, size):
            if children and not children[0].stdout.closed and fd == children[0].stdout.fileno():
                size = min(size, 7)
            return real_read(fd, size)

        with patch("probe.subprocess.Popen", side_effect=capture), \
                patch("probe.os.read", side_effect=short_read):
            result = probe.run_child(probe.child_command())
        self.assertEqual(result["result"], "observed")
        self.assertEqual(result["caller_proc_visibility"], "same-identity")
        self.assertTrue(result["exit_status_forwarded"])
        self.assertEqual(children[0].returncode, 23)

    def test_invalid_reports_reap_owned_children(self):
        cases = [
            (b'{"pid":', EOFError),
            (b'{broken}\n', json.JSONDecodeError),
            (b'\n', json.JSONDecodeError),
            (b'\xff\n', UnicodeDecodeError),
            (b'x' * (probe.MAX_RECORD_BYTES + 1), ValueError),
        ]
        real_popen = subprocess.Popen
        for output, error in cases:
            with self.subTest(output_prefix=output[:10], error=error):
                children = []

                def capture(*args, **kwargs):
                    child = real_popen(*args, **kwargs)
                    children.append(child)
                    return child

                command = [sys.executable, "-c",
                           f"import os, time; os.write(1, {output!r}); os.close(1); time.sleep(3)"]
                with patch("probe.subprocess.Popen", side_effect=capture):
                    with self.assertRaises(error):
                        probe.run_child(command)
                self.assertIsNotNone(children[0].returncode)

    def test_partial_timeout_reaps_owned_child(self):
        real_popen = subprocess.Popen
        children = []

        def capture(*args, **kwargs):
            child = real_popen(*args, **kwargs)
            children.append(child)
            return child

        command = [sys.executable, "-c", "import os, time; os.write(1, b'{'); time.sleep(3)"]
        real_select = probe.select.select
        calls = 0

        def first_read_only(*args):
            nonlocal calls
            calls += 1
            return real_select(*args) if calls == 1 else ([], [], [])

        with patch("probe.subprocess.Popen", side_effect=capture), \
                patch("probe.select.select", side_effect=first_read_only):
            with self.assertRaisesRegex(TimeoutError, "1 bytes received"):
                probe.run_child(command)
        self.assertIsNotNone(children[0].returncode)

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
