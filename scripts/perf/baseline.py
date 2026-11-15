#!/usr/bin/env python3
"""Linux shell baselines, not performance gates. See docs/port/PERFORMANCE.md."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import selectors
import shutil
import statistics
import subprocess
import time
from datetime import datetime, timezone


ROOT = Path(__file__).resolve().parents[2]
REPETITIONS = 10


def capture(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()


def digest(path):
    # Streaming avoids inflating the parent's pre-exec RSS high-water mark.
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def measure(command, env, desktop):
    """Timestamp observed pipe lines; wait4 supplies this child's lifetime peak RSS."""
    start = time.perf_counter_ns()
    process = subprocess.Popen(command, cwd=ROOT, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT)
    lines = []
    events = {}
    pending = b""
    status = None
    usage = None
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while status is None or selector.get_map():
                if time.perf_counter_ns() - start > 30_000_000_000:
                    raise TimeoutError(f"Host exceeded 30 seconds: {command}")
                for key, _ in selector.select(timeout=0.001):
                    chunk = os.read(key.fileobj.fileno(), 65536)
                    observed = time.perf_counter_ns()
                    if not chunk:
                        selector.unregister(key.fileobj)
                    pending += chunk
                    while b"\n" in pending:
                        line, pending = pending.split(b"\n", 1)
                        text = line.decode("utf-8").rstrip("\r")
                        lines.append(text)
                        events[text] = (observed - start) / 1_000_000
                if status is None:
                    pid, child_status, child_usage = os.wait4(process.pid, os.WNOHANG)
                    if pid:
                        status, usage = child_status, child_usage
                        exited = (time.perf_counter_ns() - start) / 1_000_000
                        process.returncode = os.waitstatus_to_exitcode(status)
        if process.returncode != 0 or pending:
            raise RuntimeError(f"Host failed ({process.returncode}): {lines!r} {pending!r}")
        if desktop:
            if lines != ["Window opened: Playnite", "Window closed"]:
                raise RuntimeError(f"Unexpected desktop output: {lines!r}")
            result = {"opened_to_closed_ms": events["Window closed"] - events[lines[0]],
                      "launch_to_opened_ms": events[lines[0]]}
        else:
            if len(lines) != 1 or len(lines[0].split(".")) != 4 or not all(
                    part.isdigit() for part in lines[0].split(".")):
                raise RuntimeError(f"Unexpected CLI output: {lines!r}")
            result = {"launch_to_version_ms": events[lines[0]]}
        return {**result, "launch_to_exit_ms": exited, "peak_rss_kib": usage.ru_maxrss}
    finally:
        if process.returncode is None:
            process.kill()
            process.wait()
        process.stdout.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if platform.system() != "Linux":
        parser.error("Linux wait4 RSS units and /proc metadata are required")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        parser.error("dotnet must be on PATH")
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    env.pop("DISPLAY", None)
    env.pop("WAYLAND_DISPLAY", None)
    commands = {}
    manifests = {}
    # Build each host once; restore/build/SDK first-use never enter sample timing.
    for host in ("Playnite.Cli", "Playnite.Desktop"):
        project = ROOT / "src" / host / (host + ".csproj")
        subprocess.run([dotnet, "restore", str(project), "--locked-mode"],
                       cwd=ROOT, env=env, check=True)
        subprocess.run([dotnet, "build", str(project), "-c", "Release", "--no-restore"],
                       cwd=ROOT, env=env, check=True)
        binary_dir = project.parent / "bin" / "Release" / "net11.0"
        manifests[host] = {str(p.relative_to(binary_dir)): digest(p)
                           for p in sorted(binary_dir.rglob("*")) if p.is_file()}
        commands[host] = [dotnet, str(binary_dir / (host + ".dll"))]
    commands["Playnite.Cli"].append("--version")
    commands["Playnite.Desktop"].extend(["--headless", "--smoke"])
    report = {
        "captured_at": datetime.now(timezone.utc).isoformat(),
        "source_commit": capture("git", "rev-parse", "HEAD"),
        "source_status": capture("git", "status", "--short"),
        "kernel": platform.platform(), "os_release": Path("/etc/os-release").read_text(),
        "cpu": capture("lscpu"), "memory": Path("/proc/meminfo").read_text(),
        "loadavg_before": Path("/proc/loadavg").read_text().strip(),
        "dotnet_info": capture(dotnet, "--info"),
        "central_versions": (ROOT / "Directory.Packages.props").read_text(),
        "repetitions": REPETITIONS, "binary_sha256": manifests, "hosts": {},
        "empty_library_open": {"status": "NOT MEASURED", "deferred_to": "Phase 4",
                               "reason": "No library, database or open API exists yet."},
    }
    for host, command in commands.items():
        desktop = host == "Playnite.Desktop"
        warmup = measure(command, env, desktop)
        samples = [measure(command, env, desktop) for _ in range(REPETITIONS)]
        summary = {metric: {"median": statistics.median(s[metric] for s in samples),
                            "min": min(s[metric] for s in samples),
                            "max": max(s[metric] for s in samples)} for metric in samples[0]}
        report["hosts"][host] = {"command": command, "warmup_excluded": warmup,
                                  "samples": samples, "summary": summary}
        print(host, json.dumps(summary, sort_keys=True), flush=True)
    report["loadavg_after"] = Path("/proc/loadavg").read_text().strip()
    args.output.write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
