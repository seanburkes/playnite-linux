"""Bounded owned tree: Python processes, NOT Steam, Wine, Proton, or a game."""

import json
import os
from pathlib import Path
import select
import subprocess
import sys


def main():
    depth = int(sys.argv[1])
    if depth not in range(4):
        raise ValueError("depth must be 0..3")
    child = None
    try:
        pids = [os.getpid()]
        if depth:
            child = subprocess.Popen(
                [sys.executable, str(Path(__file__).resolve()), str(depth - 1)],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
            # Each descendant has an independent four-second lifetime bound.
            if not select.select([child.stdout], [], [], 2)[0]:
                raise TimeoutError("child handshake")
            pids.extend(json.loads(child.stdout.readline()))
        print(json.dumps(pids), flush=True)
        if select.select([sys.stdin], [], [], 4)[0]:
            sys.stdin.readline()
    finally:
        if child is not None:
            try:
                child.communicate("stop\n", timeout=7)
            except subprocess.TimeoutExpired:
                child.kill()  # Only this exact owned child; never a discovered PID.
                child.communicate(timeout=2)
            if child.returncode != 0:
                raise RuntimeError(f"controlled child failed: {child.returncode}")


if __name__ == "__main__":
    main()
