#!/usr/bin/env python3
"""Check the real LibraryImport model, then require a counterexample per invariant.

Mutants run in disposable directories; the repository/oracle is never rewritten.
Edits target named TLA+ actions and primed assignments, not line numbers or whole
source snippets. Each mutant checks TypeOK plus its target (TypeOK only for the
domain mutant), so an unrelated safety invariant cannot mask the intended failure.
"""

import os
from pathlib import Path
import re
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
FORMAL = ROOT / "docs/formal"
JAR = Path(os.environ.get("TLA2TOOLS_JAR", "/tmp/opencode/tla/tla2tools.jar"))


def assignment(source, action, variable, value, old_value=None):
    """Change one action's assignment, or replace its UNCHANGED variable."""
    start = re.search(rf"(?m)^{re.escape(action)}\s*==\s*\n", source)
    if start is None:
        raise ValueError(f"Missing action {action}")
    following = re.search(r"(?m)^\w+\s*==", source[start.end():])
    end = start.end() + following.start() if following else len(source)
    body = source[start.end():end]
    rhs = re.escape(old_value) if old_value is not None else r"[^\n]+"
    pattern = rf"(/\\\s+{re.escape(variable)}'\s*=\s*){rhs}(?=\n)"
    body, count = re.subn(pattern, lambda m: m[1] + value, body)
    if count == 0 and old_value is None:
        def replace_unchanged(match):
            nonlocal count
            names = [name.strip() for name in match[1].split(",")]
            if variable not in names:
                return match[0]
            names.remove(variable)
            count += 1
            return f"{variable}' = {value}\n    /\\ UNCHANGED <<{', '.join(names)}>>"
        body = re.sub(r"UNCHANGED\s*<<([^>]+)>>", replace_unchanged, body)
    if count != 1:
        raise ValueError(f"Expected one assignment to {action}.{variable}, found {count}")
    return source[:start.end()] + body + source[end:]


def run(directory, source, config, name):
    directory.mkdir()
    model = directory / "LibraryImport.tla"
    cfg = directory / "LibraryImport.cfg"
    model.write_text(source)
    cfg.write_text(config)
    result = subprocess.run(
        ["java", "-Xmx512m", "-cp", str(JAR), "tlc2.TLC", "-deadlock",
         "-config", str(cfg), "-metadir", str(directory / "states"), str(model)],
        capture_output=True, text=True, timeout=60,
    )
    output = result.stdout + result.stderr
    # Full logs are retained when FORMAL_MUTATION_OUTPUT names an output directory.
    output_directory = os.environ.get("FORMAL_MUTATION_OUTPUT")
    if output_directory:
        log_directory = Path(output_directory)
        log_directory.mkdir(parents=True, exist_ok=True)
        (log_directory / f"{name}.log").write_text(output)
    return result.returncode, output


def main():
    source = (FORMAL / "LibraryImport.tla").read_text()
    config = (FORMAL / "LibraryImport.cfg").read_text()
    prefix, invariant_text = config.split("INVARIANTS", 1)
    invariants = invariant_text.split()
    # (invariant, action, variable, replacement, optional old expression).
    # None action means exercise the explicit unsafe publication implementation.
    cases = [
        ("TypeOK", "Start", "runs", "MaxRuns + 1", None),
        ("SourceUnchanged", "Read", "source", "{}", None),
        ("ReadSnapshotImmutable", "Read", "snapshot", "{}", None),
        ("NoCommitWithoutVerify", "Publish", "verified", "FALSE", None),
        ("ReaderNeverPartial", None, None, None, None),
        ("AtomicLive", None, None, None, None),
        ("BackupBeforeWrite", "Backup", "backupExists", "FALSE", None),
        ("DurableReceipt", "Publish", "receipt", "FALSE", None),
        ("CrashSafe", None, None, None, None),
        ("CleanResumable", "Recover", "tempDb", "tempDb", None),
        ("RepeatRunSafe", "Start", "outcome", '"Imported"', "state'"),
    ]
    if set(invariants) != {case[0] for case in cases} or len(invariants) != len(cases):
        raise ValueError("Mutation coverage must match every configured invariant exactly")
    with tempfile.TemporaryDirectory(prefix="library-import-mutants-") as scratch:
        scratch = Path(scratch)
        code, output = run(scratch / "baseline", source, config, "baseline")
        if code != 0 or "Model checking completed. No error has been found." not in output:
            raise RuntimeError(f"Baseline failed (exit {code}):\n{output}")
        print("Baseline: all 11 configured invariants PASS", flush=True)
        for invariant, action, variable, value, old_value in cases:
            mutant_source = source
            mutant_prefix = prefix
            if action is None:
                mutant_prefix, count = re.subn(
                    r"\bInPlacePublish\s*=\s*FALSE\b", "InPlacePublish = TRUE", prefix,
                )
                if count != 1:
                    raise ValueError("Missing safe publication configuration")
                description = "InPlacePublish = TRUE"
            else:
                mutant_source = assignment(source, action, variable, value, old_value)
                description = f"{action}.{variable}' = {value}"
            targets = ["TypeOK", invariant] if invariant != "TypeOK" else [invariant]
            mutant_config = mutant_prefix + "INVARIANTS\n    " + "\n    ".join(targets) + "\n"
            code, output = run(scratch / invariant, mutant_source, mutant_config, invariant)
            if code != 12 or f"Invariant {invariant} is violated." not in output:
                raise RuntimeError(f"{invariant}: wrong result (exit {code}):\n{output}")
            # A reader/crash counterexample must actually traverse the unsafe
            # publication boundary and then the observing/crashing action.
            required_actions = {
                "AtomicLive": ["PublishPartial"],
                "ReaderNeverPartial": ["PublishPartial", "ReadVisible"],
                "CrashSafe": ["PublishPartial", "Crash"],
            }.get(invariant, [])
            for required in required_actions:
                if re.search(rf"<{required}\s+line\b", output) is None:
                    raise RuntimeError(f"Missing {required} in {invariant} trace:\n{output}")
            print(f"{invariant}: REJECTED (exit 12; {description})", flush=True)
    print("Mutation coverage: 11/11 invariants rejected their intended mutant", flush=True)


if __name__ == "__main__":
    main()
