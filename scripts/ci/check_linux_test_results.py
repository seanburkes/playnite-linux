#!/usr/bin/env python3
"""Classify MTP results; empty scaffolds are allowed only before a new-suite baseline.

The committed baseline is a ratchet. When a previous committed baseline is
supplied, a change may add the first non-null ``new`` object, but it may never
delete the baseline file, reset ``new`` to null, lower a project's executed or
passed count, or silently drop/rename a project. A project may be retired only
by a ``removed_projects`` record in the protected baseline that names a reason
and an approver; the protected-path ``oracle-change`` gate makes that record a
second-reviewer decision.

A project may also be absent from the counted baseline only when it is declared
in ``empty_projects`` with a non-empty reason and still executes zero cases.
Declaring a project empty is explicit: if it gains a case, the run fails until
that project is moved into ``new`` as a counted entry. Undeclared zero-case
projects fail once any new-suite baseline is recorded.

``--previous-projects`` lists the project names present at the base commit, so
deleting a project before the first baseline is recorded is still a failure
rather than zero tests.
"""

import argparse
import json
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def load_document(path):
    """Load a baseline document, or return None when it does not exist."""
    if path is None or not path.exists():
        return None
    document = json.loads(path.read_text())
    if not isinstance(document, dict):
        raise ValueError("baseline must be a JSON object")
    return document


def read_new_baseline(document):
    """Return the validated ``new`` object, or None while bootstrap is open."""
    if document is None:
        return None
    if "new" not in document:
        raise ValueError("baseline must contain the new-suite state in 'new'")
    baseline = document["new"]
    if baseline is None:
        return None
    if not isinstance(baseline, dict) or not baseline:
        raise ValueError("recorded new-suite baseline must contain per-project counts")
    for project, row in baseline.items():
        if (not isinstance(row, dict) or type(row.get("executed")) is not int
                or row["executed"] < 0):
            raise ValueError(f"invalid executed baseline for {project}")
        if "passed" in row and (type(row["passed"]) is not int or row["passed"] < 0):
            raise ValueError(f"invalid passed baseline for {project}")
        if "passed" in row and row["passed"] > row["executed"]:
            raise ValueError(f"passed baseline exceeds executed for {project}")
    return baseline


def read_empty_projects(document):
    """Return declared zero-case scaffolds as project name -> non-empty reason."""
    if document is None:
        return {}
    empty = document.get("empty_projects", {})
    if not isinstance(empty, dict):
        raise ValueError("empty_projects must be an object")
    for project, reason in empty.items():
        if not isinstance(project, str) or not project.strip():
            raise ValueError("empty_projects contains an invalid project name")
        if not isinstance(reason, str) or not reason.strip():
            raise ValueError(
                f"declared empty project {project} needs a non-empty reason")
    return empty


def read_removed_projects(document):
    """Return reviewed project-retirement records from the baseline."""
    if document is None:
        return {}
    removed = document.get("removed_projects", {})
    if not isinstance(removed, dict):
        raise ValueError("removed_projects must be an object")
    for project, record in removed.items():
        if not isinstance(record, dict):
            raise ValueError(f"invalid removal record for {project}")
        for field in ("reason", "approved_by"):
            value = record.get(field)
            if not isinstance(value, str) or not value.strip():
                raise ValueError(f"removal record for {project} needs a non-empty {field}")
        if "replaced_by" in record and (not isinstance(record["replaced_by"], str)
                                        or not record["replaced_by"].strip()):
            raise ValueError(f"removal record for {project} has an invalid replaced_by")
        if "replacement_executed" in record and (
                type(record["replacement_executed"]) is not int
                or record["replacement_executed"] < 0):
            raise ValueError(
                f"removal record for {project} has an invalid replacement_executed")
    return removed


def project_name(value):
    """Return a project name without a path or .csproj suffix.

    The project name itself may contain dots (Playnite.Architecture.Tests), so
    only the final .csproj suffix is removed; Path.stem would truncate at the
    last dot and turn it into Playnite.Architecture.
    """
    name = Path(value).name
    return name[:-len(".csproj")] if name.endswith(".csproj") else name


def read_previous_projects(path):
    if path is None or not path.exists():
        return set()
    return {project_name(line) for line in path.read_text().splitlines() if line.strip()}


def baseline_count(row, field):
    if field in row:
        return row[field]
    # Older baselines recorded only executed; a passing suite has passed == executed.
    return row["executed"] if field == "passed" else None


def validate_baseline_policy(current_path, current_document, current, removed,
                             previous_document, previous, previous_removed,
                             previous_projects, current_projects, empty):
    """Return failures for a baseline that lowers, deletes or loses history."""
    failures = []
    for project in sorted(empty):
        if current is not None and project in current:
            failures.append(f"{project}: declared empty but also recorded in the "
                            "counted baseline")
        if project in removed:
            failures.append(f"{project}: declared empty but also recorded as removed")

    if previous_document is not None and current_document is None:
        failures.append(f"baseline file {current_path} was deleted; the committed "
                        "baseline cannot be removed")
        return failures

    if previous_document is not None and previous is not None and current is None:
        failures.append("new-suite baseline was reset to null; the committed baseline "
                        "cannot be cleared")

    for project, previous_row in (previous or {}).items():
        if current is not None and project in current:
            current_row = current[project]
            for field in ("executed", "passed"):
                previous_count = baseline_count(previous_row, field)
                current_count = baseline_count(current_row, field)
                if previous_count is not None and (current_count is None
                                                   or current_count < previous_count):
                    failures.append(
                        f"{project}: baseline {field} count {current_count} is below the "
                        f"previous committed baseline {previous_count}")
        elif project in removed:
            record = removed[project]
            replacement = record.get("replaced_by")
            if replacement is not None:
                replacement_row = (current or {}).get(replacement)
                if replacement_row is None:
                    failures.append(
                        f"{project}: removal record names replacement {replacement}, "
                        "which is not in the current baseline")
                else:
                    previous_count = baseline_count(previous_row, "executed")
                    replacement_count = baseline_count(replacement_row, "executed")
                    if replacement_count is None or replacement_count < previous_count:
                        failures.append(
                            f"{project}: replacement {replacement} executed count "
                            f"{replacement_count} is below the retired project count "
                            f"{previous_count}")
                    stated = record.get("replacement_executed")
                    if stated is not None and stated != replacement_count:
                        failures.append(
                            f"{project}: removal record states replacement count {stated} "
                            f"but {replacement} records {replacement_count}")
        else:
            failures.append(
                f"{project}: test project from the previous committed baseline is "
                "missing; restore it or record a reviewed removal in removed_projects")

    for project, previous_record in previous_removed.items():
        if project not in removed:
            failures.append(
                f"{project}: prior reviewed removal record was deleted; retirement "
                "records are immutable")
        elif removed[project] != previous_record:
            failures.append(
                f"{project}: prior reviewed removal record was edited; retirement "
                "records are immutable")

    for project in sorted(removed):
        record = removed[project]
        if "replaced_by" in record and "replacement_executed" not in record:
            failures.append(f"{project}: removal record must state the replacement count")
        if "replacement_executed" in record and "replaced_by" not in record:
            failures.append(f"{project}: removal record states a replacement count "
                            "without a replaced_by project")
        if previous is not None and project not in previous and project not in previous_projects:
            failures.append(f"{project}: removal record names a project with no previous "
                            "baseline entry or base-commit project")
        if current is not None and project in current:
            failures.append(f"{project}: removal record conflicts with a current "
                            "baseline entry")
        if project in current_projects:
            failures.append(f"{project}: removal record conflicts with a project that "
                            "still ran")

    for project in sorted(previous_projects - current_projects):
        if project not in removed:
            failures.append(
                f"{project}: test project existed at the base commit but was not run; "
                "restore it or record a reviewed removal in removed_projects")
    return failures


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


def classify(project, exit_code, counts, baseline, empty):
    if counts["failed"] or counts["skipped"]:
        return "fail", "failed or skipped cases are never accepted"
    if project in empty:
        if counts["total"] != 0:
            return "fail", (f"declared empty but executed case count "
                            f"{counts['executed']}; record it in the counted baseline")
        if exit_code in (0, 8):
            return "declared-empty", "declared zero-case scaffold; no tests yet"
        return "fail", f"unaccepted MTP exit code {exit_code}"
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


def check_results(projects, results, baseline_path, summary_path,
                  previous_baseline_path=None, previous_projects_path=None,
                  previous_baseline_state=None, previous_projects_state=None):
    failures = []
    notices = []
    try:
        current_document = load_document(baseline_path)
        baseline = read_new_baseline(current_document)
        removed = read_removed_projects(current_document)
        empty = read_empty_projects(current_document)
    except (OSError, ValueError) as error:
        message = f"Invalid new-suite baseline: {error}"
        print(f"::error::{message}")
        with summary_path.open("a") as summary:
            summary.write(f"\n**FAIL: {message}**\n")
        return 1

    if previous_baseline_state is None:
        previous_baseline_state = "present" if previous_baseline_path is not None else "absent"
    if previous_projects_state is None:
        previous_projects_state = "present" if previous_projects_path is not None else "absent"

    previous_document = None
    previous = None
    previous_removed = {}
    if previous_baseline_state not in ("present", "absent"):
        failures.append(f"invalid previous baseline state: {previous_baseline_state}")
    elif previous_baseline_state == "present":
        if previous_baseline_path is None or not previous_baseline_path.exists():
            failures.append(f"previous committed baseline was expected at "
                            f"{previous_baseline_path} but is missing")
        else:
            try:
                previous_document = load_document(previous_baseline_path)
                previous = read_new_baseline(previous_document)
                previous_removed = read_removed_projects(previous_document)
            except (OSError, ValueError) as error:
                previous_document = previous = None
                failures.append(f"Invalid previous committed baseline: {error}")
    elif previous_baseline_path is not None and previous_baseline_path.exists():
        failures.append(f"previous committed baseline state is absent but "
                        f"{previous_baseline_path} exists")

    previous_projects = set()
    if previous_projects_state not in ("present", "absent"):
        failures.append(f"invalid previous project list state: {previous_projects_state}")
    elif previous_projects_state == "present":
        if previous_projects_path is None or not previous_projects_path.exists():
            failures.append(f"previous test project list was expected at "
                            f"{previous_projects_path} but is missing")
        else:
            try:
                previous_projects = read_previous_projects(previous_projects_path)
            except OSError as error:
                failures.append(f"Invalid previous project list: {error}")
    elif previous_projects_path is not None and previous_projects_path.exists():
        failures.append(f"previous project list state is absent but "
                        f"{previous_projects_path} exists")

    project_names = {project_name(project) for project in projects}
    failures.extend(validate_baseline_policy(
        baseline_path, current_document, baseline, removed,
        previous_document, previous, previous_removed,
        previous_projects, project_names, empty))

    rows = {}
    summary = ["\n### Linux test counts\n",
               "| Project | Executed | Passed | Failed | Skipped | MTP exit | Outcome |",
               "| --- | ---: | ---: | ---: | ---: | ---: | --- |"]
    for project in projects:
        name = project_name(project)
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
            outcome, message = classify(name, exit_code, counts, baseline, empty)
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
        elif outcome == "declared-empty":
            notice = (f"{name}: {message}; 0 executed cases, explicitly declared "
                      "in empty_projects.")
            print(f"::notice::{notice}")
            notices.append(f"\n**DECLARED EMPTY — {notice}**\n")
        elif outcome == "fail":
            failures.append(f"{name}: {message}")

    for project in sorted(set(empty) - set(rows)):
        failures.append(f"{project}: declared empty but was not run")

    first_baseline = previous is None and (
        previous_document is not None
        or (previous_baseline_state == "absent" and previous_baseline_path is not None))
    if first_baseline and baseline is not None:
        for project, row in baseline.items():
            observed = rows.get(project, {}).get("counts")
            if observed is None:
                continue
            for field in ("executed", "passed"):
                recorded = baseline_count(row, field)
                seen = observed[field]
                if recorded is not None and recorded < seen:
                    failures.append(
                        f"{project}: first baseline {field} count {recorded} is below "
                        f"observed {seen}; record the observed floor")
        for project in sorted(set(rows) - set(baseline) - set(empty)):
            failures.append(
                f"{project}: observed test project was omitted from the first baseline")

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
        {"baseline_recorded": baseline is not None,
         "previous_baseline_recorded": previous is not None,
         "projects": rows, "failures": failures},
        indent=2) + "\n")
    with summary_path.open("a") as output:
        output.write("\n".join(summary) + "\n")
    return int(bool(failures))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--projects", nargs="+", required=True)
    parser.add_argument("--results-directory", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--previous-baseline", type=Path)
    parser.add_argument("--previous-projects", type=Path)
    parser.add_argument("--previous-baseline-state", choices=("present", "absent"))
    parser.add_argument("--previous-projects-state", choices=("present", "absent"))
    parser.add_argument("--summary", type=Path, required=True)
    args = parser.parse_args()
    previous_baseline_state = (args.previous_baseline_state
                               or os.environ.get("PREVIOUS_BASELINE_STATE") or None)
    previous_projects_state = (args.previous_projects_state
                               or os.environ.get("PREVIOUS_PROJECTS_STATE") or None)
    return check_results(args.projects, args.results_directory, args.baseline,
                         args.summary, args.previous_baseline, args.previous_projects,
                         previous_baseline_state, previous_projects_state)


if __name__ == "__main__":
    sys.exit(main())
