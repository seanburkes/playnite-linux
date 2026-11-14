"""Isolated polling experiment; not a Steam launch/session attribution implementation."""

from dataclasses import dataclass
from pathlib import Path
from threading import Event
import time


@dataclass(frozen=True)
class Identity:
    pid: int
    start_ticks: int


@dataclass(frozen=True)
class Process:
    identity: Identity
    parent_pid: int
    state: str


@dataclass(frozen=True)
class Snapshot:
    processes: tuple[Process, ...]
    complete: bool = True


def parse_stat(text: str) -> Process:
    # comm may contain whitespace and ')'; fields after its final ')' are fixed.
    opening = text.index("(")
    closing = text.rindex(")")
    fields = text[closing + 1:].split()
    return Process(Identity(int(text[:opening]), int(fields[19])),
                   int(fields[1]), fields[0])


def snapshot(pids: tuple[int, ...], proc: Path = Path("/proc")) -> Snapshot:
    """Read only explicitly supplied PIDs; no command lines or environments collected."""
    rows = []
    complete = True
    for pid in pids:
        try:
            row = parse_stat((proc / str(pid) / "stat").read_text())
            if row.identity.pid != pid:
                raise ValueError("PID does not match requested stat path")
            rows.append(row)
        except FileNotFoundError:
            # A process that exited between observations is absent.
            continue
        except (PermissionError, ProcessLookupError, ValueError, IndexError):
            complete = False
    return Snapshot(tuple(rows), complete)


class Session:
    """Track an explicitly attributed root within one boot and PID namespace."""

    def __init__(self, root: Identity):
        self.known = {root}
        self.observed = False
        self.closed = False

    def update(self, sample: Snapshot) -> str:
        if self.closed:
            raise RuntimeError("session disposed")
        if not sample.complete:
            return "unknown"
        alive = {p.identity: p for p in sample.processes if p.state not in ("Z", "X")}
        by_pid = {identity.pid: identity for identity in alive}
        related = self.known.intersection(alive)
        # Fixed point: /proc enumeration order must not determine descendant discovery.
        while True:
            descendants = {identity for identity, p in alive.items()
                           if by_pid.get(p.parent_pid) in related}
            expanded = related | descendants
            if expanded == related:
                break
            related = expanded
        if related:
            self.known = related
            self.observed = True
            return "running"
        if self.observed:
            self.known.clear()
        return "stopped" if self.observed else "unobserved"

    def wait(self, read, cancel: Event, timeout: float, interval: float = 0.02) -> str:
        if timeout <= 0 or interval <= 0:
            raise ValueError("positive timeout and interval required")
        deadline = time.monotonic() + timeout
        while True:
            if self.closed:
                raise RuntimeError("session disposed")
            if cancel.is_set():
                return "cancelled"
            sample = read()
            if cancel.is_set():
                return "cancelled"
            state = self.update(sample)
            if state == "stopped":
                return state
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return "timeout"
            cancel.wait(min(interval, remaining))

    def close(self):
        # Observation owns no processes: disposal must not terminate a game.
        self.closed = True
        self.known.clear()
