#!/usr/bin/env python3
"""Classify MTP results; empty scaffolds are allowed only before a new-suite baseline."""

import argparse
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def load_baseline(path):
    if not path.exists():
        return None
    document = json.loads(path.read_text())
    if not isinstance(document, dict) or "new" not in document:
        raise ValueError("baseline must contain the new-suite state in 'new'")
    baseline = document["new"]
    if baseline is None:
        return None
    if not isinstance(baseline, dict) or not baseline:
        raise ValueError("recorded new-suite baseline must contain per-project counts")
    for project, row in baseline.items():
        if not isinstance(row, dict) or type(row.get("executed")) is not int or row["executed"] < 0:
            raise ValueError(f"invalid executed baseline for {project}")
    return baseline


def read_counts(path):
    root = ET.parse(path).getroot()
    if root.tag not in ("testsuites", "testsuite"):
        raise ValueError("expected a JUnit testsuite or testsuites result")
    cases = list(root.iter("testcase"))
    skipped = sum(case.find("skipped") is not None for case in cases)
    failed = sum(case.find("failure") is not None or case.find("error") is not None
                 for case in cases)
    for suite in root.iter():
        if suite.tag not in ("testsuites", "testsuite"):
            continue
        descendants = list(suite.iter("testcase"))
        expected = {
            "tests": len(descendants),
            "failures": sum(case.find("failure") is not None for case in descendants),
            "errors": sum(case.find("error") is not None for case in descendants),
            "skipped": sum(case.find("skipped") is not None for case in descendants),
            "disabled": 0,
        }
        if "tests" not in suite.attrib:
            raise ValueError("JUnit suite is missing its test count")
        for field, count in expected.items():
            if field in suite.attrib and int(suite.attrib[field]) != count:
                raise ValueError(f"JUnit {field} count disagrees with case results")
    return {"total": len(cases), "executed": len(cases) - skipped,
            "passed": len(cases) - skipped - failed, "failed": failed, "skipped": skipped}


def classify(project, exit_code, counts, baseline):
    if counts["failed"] or counts["skipped"]:
        return "fail", "failed or skipped cases are never accepted"
    if exit_code == 8 and counts["total"] == 0 and baseline is None:
        return "bootstrap-empty", "NO TESTS EXIST YET; no new-suite baseline is recorded"
    if exit_code != 0:
        return "fail", f"unaccepted MTP exit code {exit_code}"
    if counts["executed"] == 0:
        return "fail", "zero executed tests; only classified pre-baseline MTP exit 8 is allowed"
    if baseline is not None and project in baseline:
        minimum = baseline[project]["executed"]
        if counts["executed"] < minimum:
            return "fail", f"executed count {counts['executed']} is below baseline {minimum}"
    return "pass", "all executed cases passed"


def check_results(projects, results, baseline_path, summary_path):
    try:
        baseline = load_baseline(baseline_path)
    except (OSError, ValueError) as error:
        message = f"Invalid new-suite baseline: {error}"
        print(f"::error::{message}")
        with summary_path.open("a") as summary:
            summary.write(f"\n**FAIL: {message}**\n")
        return 1

    rows = {}
    failures = []
    notices = []
    summary = ["\n### Linux test counts\n",
               "| Project | Executed | Passed | Failed | Skipped | MTP exit | Outcome |",
               "| --- | ---: | ---: | ---: | ---: | ---: | --- |"]
    for project in projects:
        name = Path(project).stem
        if name in rows:
            failures.append(f"duplicate test project name: {name}")
            continue
        row = {"exit_code": None, "counts": None}
        try:
            directory = results / name
            exit_code = int((directory / "exit-code.txt").read_text().strip())
            if exit_code < 0:
                raise ValueError("exit code must be nonnegative")
            row["exit_code"] = exit_code
            counts = read_counts(directory / f"{name}.xml")
            row["counts"] = counts
            outcome, message = classify(name, exit_code, counts, baseline)
        except (OSError, ValueError, ET.ParseError) as error:
            outcome, message = "fail", f"cannot validate MTP result: {error}"
        row.update(outcome=outcome, message=message)
        rows[name] = row
        numbers = row["counts"] or {field: "?" for field in ("executed", "passed", "failed", "skipped")}
        summary.append(f"| {name} | {numbers['executed']} | {numbers['passed']} | "
                       f"{numbers['failed']} | {numbers['skipped']} | {row['exit_code']} | {outcome} |")
        print(f"{name}: {numbers}; MTP exit {row['exit_code']}; {outcome}")
        if outcome == "bootstrap-empty":
            notice = f"{name}: {message}; 0 executed cases. This is not behavior evidence."
            print(f"::notice::{notice}")
            notices.append(f"\n**BOOTSTRAP NOTICE — {notice}**\n")
        elif outcome == "fail":
            failures.append(f"{name}: {message}")

    if not projects:
        failures.append("no test projects were supplied")
    if baseline is not None:
        failures.extend(f"baseline test project was not run: {name}"
                        for name in sorted(set(baseline) - set(rows)))
    summary.extend(notices)
    for failure in failures:
        print(f"::error::{failure}")
        summary.append(f"\n**FAIL: {failure}**\n")
    results.mkdir(parents=True, exist_ok=True)
    (results / "test-counts.json").write_text(json.dumps(
        {"baseline_recorded": baseline is not None, "projects": rows, "failures": failures},
        indent=2) + "\n")
    with summary_path.open("a") as output:
        output.write("\n".join(summary) + "\n")
    return int(bool(failures))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--projects", nargs="+", required=True)
    parser.add_argument("--results-directory", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--summary", type=Path, required=True)
    args = parser.parse_args()
    return check_results(args.projects, args.results_directory, args.baseline, args.summary)


if __name__ == "__main__":
    sys.exit(main())
