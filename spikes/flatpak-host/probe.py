#!/usr/bin/env python3
"""Bounded ADR-020 transport probe; never starts Steam or enumerates user processes."""

import argparse
import json
import os
from pathlib import Path
import select
import shutil
import subprocess
import sys
import time


MAX_RECORD_BYTES = 4096  # Includes the terminating newline.
RECORD_TIMEOUT = 5


# The controlled child self-expires even if its launcher dies. No host file writes.
CHILD = """
import json, os, pathlib, select, sys
print(json.dumps({
    'pid': os.getpid(),
    'stat': pathlib.Path('/proc/self/stat').read_text(),
    'namespace': os.readlink('/proc/self/ns/pid'),
}), flush=True)
select.select([sys.stdin], [], [], 3)
sys.exit(23)
"""


def identity(stat, namespace):
    """PID plus start ticks and PID namespace, not a PID-only visibility guess."""
    pid, rest = stat.split(" (", 1)
    _, fields = rest.rsplit(") ", 1)
    fields = fields.split()
    return {"pid": int(pid), "start_ticks": int(fields[19]), "namespace": namespace}


def read_identity(pid):
    return identity(
        Path(f"/proc/{pid}/stat").read_text(),
        os.readlink(f"/proc/{pid}/ns/pid"),
    )


def compare_visibility(expected, reader=read_identity):
    try:
        observed = reader(expected["pid"])
    except FileNotFoundError:
        return "not-visible-or-exited"
    except PermissionError:
        return "permission-denied"
    return "same-identity" if observed == expected else "different-identity"


def child_command(host=False):
    if host:
        return ["flatpak-spawn", "--host", "--watch-bus", "/usr/bin/python3", "-u", "-c", CHILD]
    return [sys.executable, "-u", "-c", CHILD]


def read_record(stream):
    """Read one newline-framed record within a total byte/time budget."""
    deadline = time.monotonic() + RECORD_TIMEOUT
    record = bytearray()
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError(f"controlled child report timed out ({len(record)} bytes received)")
        ready, _, _ = select.select([stream], [], [], remaining)
        if not ready:
            raise TimeoutError(f"controlled child report timed out ({len(record)} bytes received)")
        chunk = os.read(stream.fileno(), MAX_RECORD_BYTES + 1 - len(record))
        if time.monotonic() >= deadline:
            raise TimeoutError(f"controlled child report timed out ({len(record) + len(chunk)} bytes received)")
        if not chunk:
            if record:
                raise EOFError("controlled child report ended before newline")
            return b""
        record.extend(chunk)
        if len(record) > MAX_RECORD_BYTES:
            raise ValueError("controlled child report exceeds 4096-byte limit")
        if b"\n" in record:
            return bytes(record[:record.index(b"\n") + 1])


def run_child(command):
    """Only terminate/reap this probe's own launcher; host child has a 3s deadline."""
    with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE) as process:
        try:
            line = read_record(process.stdout)
            if not line:
                _, error = process.communicate(timeout=5)
                return {"result": "launch-failed", "exit_code": process.returncode,
                        "stderr": error.decode(errors="replace")[:1024]}
            sample = json.loads(line)
            expected = identity(sample["stat"], sample["namespace"])
            if expected["pid"] != sample["pid"]:
                raise ValueError("child PID does not match its proc stat")
            visibility = compare_visibility(expected)
            _, error = process.communicate(b"release\n", timeout=5)
            return {"result": "observed", "child_identity": expected,
                    "caller_proc_visibility": visibility,
                    "exit_code": process.returncode,
                    "exit_status_forwarded": process.returncode == 23,
                    "stderr": error.decode(errors="replace")[:1024]}
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate(timeout=5)


def host_gate(in_sandbox, spawn_available):
    if not in_sandbox:
        return "not-run: caller is not inside an existing Flatpak sandbox"
    if not spawn_available:
        return "not-run: flatpak-spawn unavailable"
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host-child", action="store_true",
                        help="explicitly probe a controlled host Python child from an existing sandbox")
    args = parser.parse_args()
    capabilities = {
        "in_flatpak": Path("/.flatpak-info").is_file(),
        "flatpak_on_path": shutil.which("flatpak") is not None,
        "flatpak_spawn_on_path": shutil.which("flatpak-spawn") is not None,
        "steam_on_path": shutil.which("steam") is not None,
    }
    report = {"capabilities": capabilities, "local_child": run_child(child_command()),
              "host_child": {"result": "not-run: --host-child not requested"},
              "real_steam_native": "not-run: user games are outside this probe",
              "real_steam_proton": "not-run: user games are outside this probe"}
    if args.host_child:
        reason = host_gate(capabilities["in_flatpak"], capabilities["flatpak_spawn_on_path"])
        report["host_child"] = {"result": reason} if reason else run_child(child_command(True))
    print(json.dumps(report, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
