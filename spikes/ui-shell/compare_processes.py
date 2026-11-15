#!/usr/bin/env python3
"""Compare PNG captures and rendered semantic trees across two separate processes."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path, help="mandatory new or empty external directory")
    args = parser.parse_args()
    evidence = args.evidence.resolve()
    if evidence.exists() and any(evidence.iterdir()):
        parser.error("evidence directory must be new or empty")
    evidence.mkdir(parents=True, exist_ok=True)
    project = Path(__file__).resolve().with_name("UiShellSpike.csproj")
    env = os.environ.copy()
    env.pop("DISPLAY", None)
    env.pop("WAYLAND_DISPLAY", None)
    results = []
    for name in ("process-1", "process-2"):
        run = evidence / name
        env["SPIKE_EVIDENCE_DIR"] = str(run)
        command = ["dotnet", "run", "--project", str(project), "-c", "Release", "--no-restore"]
        # Each call starts a separate test executable, and a nonzero exit aborts comparison.
        result = subprocess.run(command, env=env, cwd=project.parent, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
        (evidence / f"{name}.log").write_text(result.stdout)
        print(result.stdout, end="", flush=True)
        result.check_returncode()
        summary = re.search(r"Total:\s*(\d+), Errors:\s*(\d+), Failed:\s*(\d+), Skipped:\s*(\d+), Not Run:\s*(\d+)", result.stdout)
        if summary is None or int(summary[1]) < 9 or any(int(value) for value in summary.groups()[1:]):
            raise RuntimeError(f"{name}: expected at least nine tests with zero errors/failures/skips/not-run")
        metadata = json.loads((run / "render.json").read_text())
        captures = [(run / f"render-{index}.png").read_bytes() for index in (1, 2, 3)]
        digest = hashlib.sha256(captures[0]).hexdigest()
        if not all(capture == captures[0] for capture in captures):
            raise RuntimeError(f"{name}: within-process PNG bytes differ")
        if metadata["sha256"].lower() != digest:
            raise RuntimeError(f"{name}: metadata SHA-256 does not match PNG bytes")
        tree = (run / "library-1200.semantic.json").read_bytes()
        results.append({"directory": name, "sha256": digest, "pngBytes": len(captures[0]),
                        "treeSha256": hashlib.sha256(tree).hexdigest(),
                        "passed": int(summary[1]), "skipped": 0})
    first = evidence / "process-1/render-1.png"
    second = evidence / "process-2/render-1.png"
    # Equality within each triple plus this cross-process pair proves all six equal.
    if first.read_bytes() != second.read_bytes():
        raise RuntimeError(f"Cross-process PNG bytes differ: {results}")
    if (evidence / "process-1/library-1200.semantic.json").read_bytes() != (evidence / "process-2/library-1200.semantic.json").read_bytes():
        raise RuntimeError(f"Cross-process semantic trees differ: {results}")
    report = {"separateProcesses": 2, "byteIdentical": True, "treeByteIdentical": True, "runs": results}
    (evidence / "comparison.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
