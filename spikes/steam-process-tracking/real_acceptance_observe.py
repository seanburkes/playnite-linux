#!/usr/bin/env python3
"""Bounded real-Steam acceptance probe for the p1-007 process-tree spike.

This is acceptance tooling, not platform code. It:

* refuses to start if a process from the requested install directory is already
  running;
* launches one already-installed title through the running Steam client with
  ``steam -applaunch <appid>`` (the supported CLI handoff);
* discovers the game root by executable path, then records the real
  ``(pid, start_ticks)`` identities and parent links the tracker uses;
* asks the window manager to close the game (WM_DELETE_WINDOW / Hyprland close),
  waits for exit, and confirms every recorded identity disappeared;
* never signals Steam.

A direct SIGTERM to the attributed game root, verified by start ticks, exists
only as a last-resort cleanup so a failed close cannot leave a game running.
Any use is recorded in the report as a deviation.

Usage:
    python3 -B spikes/steam-process-tracking/real_acceptance_observe.py \
        --appid 105600 --install-dir "$HOME/.local/share/Steam/steamapps/common/Terraria" \
        --window-name Terraria --observe-seconds 15 --report /tmp/opencode/report.json
"""

import argparse
import importlib.util
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

STEAM = "/usr/bin/steam"

_spec = importlib.util.spec_from_file_location(
    "p1_007_tracker", Path(__file__).with_name("tracker.py"))
tracker = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(tracker)


def parse_stat(text):
    opening = text.index("(")
    closing = text.rindex(")")
    fields = text[closing + 1:].split()
    return {
        "pid": int(text[:opening]),
        "comm": text[opening + 1:closing],
        "state": fields[0],
        "ppid": int(fields[1]),
        "start_ticks": int(fields[19]),
    }


def read_row(pid):
    row = parse_stat(Path(f"/proc/{pid}/stat").read_text())
    try:
        row["exe"] = os.readlink(f"/proc/{pid}/exe")
    except OSError:
        row["exe"] = None
    try:
        row["pid_namespace"] = os.readlink(f"/proc/{pid}/ns/pid")
    except OSError:
        row["pid_namespace"] = None
    try:
        raw = Path(f"/proc/{pid}/cmdline").read_bytes()
        row["cmdline"] = raw.replace(b"\0", b" ").decode("utf-8", "replace").strip()
    except OSError:
        row["cmdline"] = None
    return row


def sample():
    """One /proc read pass. complete covers rows we can read, not all processes."""
    rows = {}
    unreadable = 0
    for name in os.listdir("/proc"):
        if not name.isdigit():
            continue
        pid = int(name)
        try:
            rows[pid] = read_row(pid)
        except (FileNotFoundError, ProcessLookupError):
            continue
        except (PermissionError, ValueError, IndexError, OSError):
            unreadable += 1
    return rows, unreadable


def is_under(path, root):
    if not path:
        return False
    try:
        real = os.path.realpath(path)
    except OSError:
        return False
    root = os.path.realpath(root)
    return real == root or real.startswith(root + os.sep)


def alive(rows):
    return {pid: row for pid, row in rows.items() if row["state"] not in ("Z", "X")}


def identity(row):
    return (row["pid"], row["start_ticks"])


def serialize(row):
    return {
        "pid": row["pid"],
        "start_ticks": row["start_ticks"],
        "ppid": row["ppid"],
        "state": row["state"],
        "comm": row["comm"],
        "exe": row["exe"],
        "pid_namespace": row["pid_namespace"],
        "cmdline": (row["cmdline"] or "")[:400],
    }


def ancestors(rows, pid, limit=30):
    chain = []
    seen = set()
    while pid > 1 and len(chain) < limit and pid not in seen:
        seen.add(pid)
        row = rows.get(pid)
        if row is None:
            break
        chain.append(serialize(row))
        pid = row["ppid"]
    return chain


def find_existing(install_dir):
    rows, _ = sample()
    return [serialize(row) for row in rows.values()
            if row["state"] not in ("Z", "X") and is_under(row["exe"], install_dir)]


def discover_root(install_dir, timeout):
    deadline = time.monotonic() + timeout
    latest_rows, latest_unreadable = {}, 0
    while time.monotonic() < deadline:
        latest_rows, latest_unreadable = sample()
        for row in latest_rows.values():
            if row["state"] in ("Z", "X"):
                continue
            if is_under(row["exe"], install_dir):
                return row, latest_rows, latest_unreadable
        time.sleep(0.2)
    return None, latest_rows, latest_unreadable


def xdotool(*args):
    try:
        done = subprocess.run(["xdotool", *args], capture_output=True, text=True,
                              timeout=10, check=False)
    except (FileNotFoundError, subprocess.TimeoutExpired) as exc:
        return {"args": args, "error": repr(exc), "ids": []}
    ids = [line for line in done.stdout.splitlines() if line.strip().isdigit()]
    return {"args": args, "returncode": done.returncode,
            "stdout": done.stdout.strip(), "stderr": done.stderr.strip(), "ids": ids}


def hyprland_window_for(pid):
    try:
        done = subprocess.run(["hyprctl", "clients", "-j"], capture_output=True,
                              text=True, timeout=10, check=False)
        clients = json.loads(done.stdout or "[]")
    except (FileNotFoundError, subprocess.TimeoutExpired, ValueError):
        return None
    for client in clients:
        if client.get("pid") == pid:
            return client
    return None


def hyprland_close_window(pid, record):
    """Ask Hyprland to close windows owned by pid using a window object.

    Hyprland 0.56 replaced legacy ``dispatch closewindow address:...`` strings
    with a Lua dispatcher API; the string form is a parse error on this host.
    """
    client = hyprland_window_for(pid)
    if not client:
        return None, []
    # Filter inside Lua so the selector cannot accidentally match another
    # window; dispatch only the object whose pid is the attributed root.
    code = (
        "local n = 0\n"
        "for _, w in ipairs(hl.get_windows()) do\n"
        f"  if w.pid == {pid} then\n"
        "    hl.dispatch(hl.dsp.window.close({ window = w }))\n"
        "    n = n + 1\n"
        "  end\n"
        "end\n"
        "return n\n"
    )
    try:
        done = subprocess.run(["hyprctl", "eval", code], capture_output=True,
                              text=True, timeout=10, check=False)
    except (FileNotFoundError, subprocess.TimeoutExpired) as exc:
        record.append({"hyprctl_eval": repr(exc)})
        return None, []
    record.append({"hyprctl_eval": done.stdout.strip(),
                   "returncode": done.returncode,
                   "stderr": done.stderr.strip()})
    return client.get("address"), record


def request_close(root_pid, window_name):
    attempts = []
    search = [xdotool("search", "--pid", str(root_pid))]
    attempts.extend(search)
    ids = [wid for entry in search for wid in entry["ids"]]
    if not ids and window_name:
        search = [xdotool("search", "--name", window_name)]
        attempts.extend(search)
        ids = [wid for entry in search for wid in entry["ids"]]
    for wid in ids:
        attempts.append(xdotool("windowclose", wid))
    if ids:
        return "xdotool-windowclose", ids, attempts

    address, attempts = hyprland_close_window(root_pid, attempts)
    if address:
        return "hyprland-lua-closewindow", [address], attempts
    return None, [], attempts


def session_snapshot(rows, sessions):
    """Feed the spike's Session the same real rows the probe observed.

    ``complete`` describes the identities the sessions already know about: a
    known pid that is absent because it exited is fine, but a pid that exists
    and cannot be read makes the sample unknown rather than stopped.
    """
    complete = True
    for session in sessions:
        for ident in session.known:
            if ident.pid in rows:
                continue
            try:
                Path(f"/proc/{ident.pid}/stat").read_text()
            except FileNotFoundError:
                continue
            except OSError:
                complete = False
    processes = tuple(
        tracker.Process(
            tracker.Identity(row["pid"], row["start_ticks"]),
            row["ppid"],
            row["state"],
        )
        for row in rows.values()
    )
    return tracker.Snapshot(processes, complete)


def wait_for_exit(known, seconds, check_install_dir=None):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        rows, unreadable = sample()
        live = alive(rows)
        remaining = [ident for ident in known if ident[0] in live
                     and (ident[0], live[ident[0]]["start_ticks"]) == ident]
        if not remaining:
            leftovers = []
            if check_install_dir:
                leftovers = [serialize(row) for row in live.values()
                             if is_under(row["exe"], check_install_dir)]
            return True, rows, unreadable, leftovers
        time.sleep(0.2)
    rows, unreadable = sample()
    leftovers = []
    if check_install_dir:
        leftovers = [serialize(row) for row in alive(rows).values()
                     if is_under(row["exe"], check_install_dir)]
    return False, rows, unreadable, leftovers


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--appid", required=True)
    parser.add_argument("--install-dir", required=True)
    parser.add_argument("--window-name", default="")
    parser.add_argument("--discover-seconds", type=float, default=45)
    parser.add_argument("--observe-seconds", type=float, default=15)
    parser.add_argument("--close-seconds", type=float, default=15)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()

    report = {
        "started_at_unix": time.time(),
        "started_at_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "host": {
            "kernel": os.uname().release,
            "boot_id": Path("/proc/sys/kernel/random/boot_id").read_text().strip(),
            "pid_namespace": os.readlink("/proc/self/ns/pid"),
            "uid": os.getuid(),
            "steam_cli": STEAM,
            "display": os.environ.get("DISPLAY"),
            "wayland_display": os.environ.get("WAYLAND_DISPLAY"),
            "desktop": os.environ.get("XDG_CURRENT_DESKTOP"),
        },
        "request": {
            "appid": args.appid,
            "install_dir": os.path.realpath(args.install_dir),
            "window_name": args.window_name,
        },
        "deviations": [],
        "close": {},
    }

    existing = find_existing(args.install_dir)
    if existing:
        report["result"] = "aborted-existing-process"
        report["existing"] = existing
        Path(args.report).write_text(json.dumps(report, indent=2) + "\n")
        print("ABORT: install-dir process already running", file=sys.stderr)
        return 2

    launch_started = time.monotonic()
    report["launch_started_unix"] = time.time()
    try:
        cli = subprocess.Popen([STEAM, "-applaunch", args.appid],
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True)
    except OSError as exc:
        report["result"] = "launch-failed"
        report["launch_error"] = repr(exc)
        Path(args.report).write_text(json.dumps(report, indent=2) + "\n")
        print(f"ABORT: cannot run {STEAM}: {exc}", file=sys.stderr)
        return 2

    root, _, discovery_unreadable = discover_root(args.install_dir,
                                                  args.discover_seconds)
    report["discovery"] = {
        "seconds": round(time.monotonic() - launch_started, 3),
        "unreadable_rows": discovery_unreadable,
    }
    if root is None:
        cli.poll()
        report["result"] = "blocked-no-game-process"
        report["launch_cli"] = {
            "returncode": cli.returncode,
            "output": (cli.stdout.read() if cli.poll() is not None else
                       "(still running; left alone)")[:2000],
        }
        Path(args.report).write_text(json.dumps(report, indent=2) + "\n")
        print("BLOCKED: no game process appeared", file=sys.stderr)
        return 3

    root_id = identity(root)
    report["root"] = serialize(root)

    rows, unreadable = sample()
    report["tree_at_first_observation"] = ancestors(rows, root["ppid"])
    report["tree_at_first_observation"].append(serialize(rows.get(root["pid"], root)))
    report["sample_unreadable_rows"] = unreadable

    # Run the spike's own Session against real /proc rows, for two plausible
    # caller attributions: the game binary itself, and Steam's AppId-scoped
    # reaper that is the stable parent of the whole runtime chain.
    reaper = next((row for row in report["tree_at_first_observation"]
                   if row["comm"] == "reaper"
                   and "SteamLaunch" in (row["cmdline"] or "")
                   and f"AppId={args.appid}" in (row["cmdline"] or "")), None)
    tracker_roots = {"game": tracker.Identity(*root_id)}
    if reaper is not None:
        tracker_roots["reaper"] = tracker.Identity(reaper["pid"],
                                                   reaper["start_ticks"])
    tracker_sessions = {name: tracker.Session(ident)
                        for name, ident in tracker_roots.items()}
    tracker_log = []
    tracker_seen = {name: set(session.known)
                    for name, session in tracker_sessions.items()}
    tracker_last_state = {}
    tracker_last_known = {}

    def record_tracker_state(at_s):
        snapshot = session_snapshot(rows, tracker_sessions.values())
        for name, session in tracker_sessions.items():
            state = session.update(snapshot)
            tracker_seen[name].update(session.known)
            changed = (state != tracker_last_state.get(name)
                       or session.known != tracker_last_known.get(name))
            if changed:
                tracker_log.append({
                    "at_s": round(at_s, 3),
                    "session": name,
                    "state": state,
                    "known": [[ident.pid, ident.start_ticks]
                              for ident in sorted(session.known,
                                                  key=lambda i: (i.pid, i.start_ticks))],
                })
                tracker_last_state[name] = state
                tracker_last_known[name] = set(session.known)

    record_tracker_state(0.0)

    # Track the descendant closure exactly as the spike does: identities are
    # (pid, start_ticks), and only live rows are considered.
    known = {root_id}
    seen = {
        root_id: {"first_seen_s": 0.0, "last_seen_s": 0.0,
                  "comm": root["comm"], "exe": root["exe"],
                  "ppid_at_first_seen": root["ppid"]},
    }
    observed_at = time.monotonic()
    transitions = []
    root_lost_while_descendants_alive = False
    samples = 0
    while True:
        elapsed = time.monotonic() - observed_at
        if elapsed >= args.observe_seconds:
            break
        rows, unreadable = sample()
        samples += 1
        record_tracker_state(elapsed)
        live = alive(rows)
        by_pid = {pid: (pid, row["start_ticks"]) for pid, row in live.items()}
        related = known.intersection(by_pid.values())
        if root_id not in related and related and not root_lost_while_descendants_alive:
            root_lost_while_descendants_alive = True
            transitions.append({"at_s": round(elapsed, 3),
                                "event": "root-gone-descendants-alive",
                                "identities": sorted(related)})
        if not related:
            if not transitions or transitions[-1]["event"] != "all-identities-gone":
                transitions.append({"at_s": round(elapsed, 3),
                                    "event": "all-identities-gone"})
            break
        while True:
            descendants = {by_pid[row["ppid"]] for row in live.values()
                           if row["ppid"] in by_pid
                           and by_pid[row["ppid"]] in related}
            expanded = related | descendants
            if expanded == related:
                break
            related = expanded
        new = related - known
        for ident in new:
            row = live[ident[0]]
            seen[ident] = {"first_seen_s": round(elapsed, 3),
                           "last_seen_s": round(elapsed, 3),
                           "comm": row["comm"], "exe": row["exe"],
                           "ppid_at_first_seen": row["ppid"]}
            transitions.append({"at_s": round(elapsed, 3),
                                "event": "identity-appeared",
                                "identity": list(ident),
                                "comm": row["comm"]})
        for ident in related:
            if ident in seen:
                seen[ident]["last_seen_s"] = round(elapsed, 3)
        known = related
        time.sleep(0.5)

    report["observation"] = {
        "requested_seconds": args.observe_seconds,
        "samples": samples,
        "identities": [
            {"pid": pid, "start_ticks": ticks, **fields}
            for (pid, ticks), fields in sorted(seen.items())
        ],
        "transitions": transitions,
        "root_lost_while_descendants_alive": root_lost_while_descendants_alive,
    }

    close_started = time.monotonic()
    method, window_ids, attempts = request_close(root["pid"], args.window_name)
    report["close"] = {"requested_method": method, "window_ids": window_ids,
                       "attempts": attempts}
    exited, rows, unreadable, leftovers = wait_for_exit(known,
                                                        args.close_seconds,
                                                        args.install_dir)
    if not exited:
        live = alive(rows)
        if root_id[0] in live and (root_id[0], live[root_id[0]]["start_ticks"]) == root_id:
            report["deviations"].append(
                "window-manager close did not end the game; sent SIGTERM to the "
                "attributed root only, start_ticks re-verified")
            os.kill(root_id[0], signal.SIGTERM)
            exited, rows, unreadable, leftovers = wait_for_exit(
                known, 10, args.install_dir)
        if not exited:
            live = alive(rows)
            if root_id[0] in live and (root_id[0], live[root_id[0]]["start_ticks"]) == root_id:
                report["deviations"].append(
                    "SIGTERM did not end the game; sent SIGKILL to the attributed "
                    "root only, start_ticks re-verified")
                os.kill(root_id[0], signal.SIGKILL)
                exited, rows, unreadable, leftovers = wait_for_exit(
                    known, 10, args.install_dir)

    report["close"]["exit_detected"] = exited
    report["close"]["seconds"] = round(time.monotonic() - close_started, 3)
    report["close"]["remaining_install_dir_processes"] = leftovers
    report["close"]["unreadable_rows_at_exit"] = unreadable

    rows, unreadable = sample()
    record_tracker_state(time.monotonic() - observed_at)
    report["tracker"] = {
        "module": str(Path(__file__).with_name("tracker.py")),
        "roots": {name: [ident.pid, ident.start_ticks]
                  for name, ident in tracker_roots.items()},
        "states": tracker_log,
        "identities_seen": {
            name: [[ident.pid, ident.start_ticks]
                   for ident in sorted(idents,
                                       key=lambda i: (i.pid, i.start_ticks))]
            for name, idents in tracker_seen.items()
        },
        "final_known": {
            name: [[ident.pid, ident.start_ticks]
                   for ident in sorted(session.known,
                                       key=lambda i: (i.pid, i.start_ticks))]
            for name, session in tracker_sessions.items()
        },
    }

    if cli.poll() is None:
        try:
            cli.wait(timeout=5)
        except subprocess.TimeoutExpired:
            report["deviations"].append(
                "the steam -applaunch CLI child was still running 5s after game "
                "exit; terminated that owned child only")
            cli.terminate()
            try:
                cli.wait(timeout=5)
            except subprocess.TimeoutExpired:
                cli.kill()
    report["launch_cli"] = {"returncode": cli.returncode,
                            "output": (cli.stdout.read() or "")[:2000]}

    report["finished_at_unix"] = time.time()
    report["finished_at_utc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ",
                                              time.gmtime())
    report["total_seconds"] = round(report["finished_at_unix"]
                                    - report["started_at_unix"], 3)
    report["result"] = "passed" if exited and not leftovers else "failed"
    Path(args.report).write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"result": report["result"],
                      "root": report["root"],
                      "identities": report["observation"]["identities"],
                      "close": report["close"],
                      "deviations": report["deviations"]}, indent=2))
    return 0 if report["result"] == "passed" else 4


if __name__ == "__main__":
    sys.exit(main())
