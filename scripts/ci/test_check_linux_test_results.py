import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from check_linux_test_results import check_results


class LinuxTestResultPolicyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.results = self.root / "results"
        self.baseline = self.root / "baseline.json"
        self.summary = self.root / "summary.md"
        self.baseline.write_text(json.dumps({"legacy": {"executed": 281}, "new": None}))

    def project(self, name="Suite", outcomes=(), exit_code=0):
        directory = self.results / name
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "exit-code.txt").write_text(str(exit_code) + "\n")
        counts = {"tests": len(outcomes), "failures": outcomes.count("failure"),
                  "errors": outcomes.count("error"), "skipped": outcomes.count("skipped")}
        report = ET.Element("testsuite", {key: str(value) for key, value in counts.items()})
        for index, outcome in enumerate(outcomes):
            case = ET.SubElement(report, "testcase", name=f"case-{index}")
            if outcome != "pass":
                ET.SubElement(case, outcome)
        ET.ElementTree(report).write(directory / f"{name}.xml")
        return f"/tests/{name}/{name}.csproj"

    def record_baseline(self, projects, removed=None, path=None):
        document = {"legacy": {"executed": 281}, "new": projects}
        if removed is not None:
            document["removed_projects"] = removed
        (path or self.baseline).write_text(json.dumps(document))

    def record_previous(self, projects, present=(), removed=None):
        previous = self.root / "previous-baseline.json"
        self.record_baseline(projects, removed=removed, path=previous)
        projects_file = self.root / "previous-projects.txt"
        projects_file.write_text("".join(f"{name}\n" for name in present))
        return previous, projects_file

    def check(self, *projects, previous=None):
        previous_baseline, previous_projects = previous or (None, None)
        with contextlib.redirect_stdout(io.StringIO()):
            return check_results(projects, self.results, self.baseline, self.summary,
                                 previous_baseline, previous_projects)

    def test_empty_exit_8_is_explicitly_recorded_before_baseline(self):
        self.assertEqual(0, self.check(self.project(exit_code=8)))
        record = json.loads((self.results / "test-counts.json").read_text())
        self.assertFalse(record["baseline_recorded"])
        self.assertEqual(0, record["projects"]["Suite"]["counts"]["executed"])
        self.assertEqual("bootstrap-empty", record["projects"]["Suite"]["outcome"])
        self.assertIn("BOOTSTRAP NOTICE", self.summary.read_text())
        self.assertIn("NO TESTS EXIST YET", self.summary.read_text())

    def test_no_baseline_file_means_no_new_suite_baseline(self):
        self.baseline.unlink()
        self.assertEqual(0, self.check(self.project(exit_code=8)))

    def test_zero_cases_with_exit_0_are_not_classified_as_bootstrap(self):
        self.assertEqual(1, self.check(self.project()))

    def test_other_nonzero_exits_are_never_suppressed(self):
        for code in (1, 2, 3, 4, 5, 7, 9, 10, 11, 12, 13, 42):
            with self.subTest(code=code):
                self.assertEqual(1, self.check(self.project(exit_code=code)))

    def test_exit_8_with_nonempty_results_is_fatal(self):
        self.assertEqual(1, self.check(self.project(outcomes=["pass"], exit_code=8)))

    def test_failed_or_skipped_case_fails_even_if_runner_returns_success(self):
        for outcome in ("failure", "error", "skipped"):
            with self.subTest(outcome=outcome):
                self.assertEqual(1, self.check(self.project(outcomes=[outcome])))

    def test_recording_baseline_closes_empty_policy_even_with_zero_baseline(self):
        self.record_baseline({"Suite": {"executed": 0}})
        self.assertEqual(1, self.check(self.project(exit_code=8)))
        self.assertEqual(1, self.check(self.project(exit_code=0)))

    def test_recorded_case_count_cannot_fall(self):
        self.record_baseline({"Suite": {"executed": 43}})
        for count, result in ((42, 1), (43, 0), (44, 0)):
            with self.subTest(count=count):
                self.assertEqual(result, self.check(self.project(outcomes=["pass"] * count)))

    def test_baseline_closes_zero_policy_for_new_projects_too(self):
        self.record_baseline({"Suite": {"executed": 1}})
        self.assertEqual(1, self.check(self.project(outcomes=["pass"]),
                                       self.project("NewSuite", exit_code=8)))

    def test_baselined_project_cannot_disappear(self):
        self.record_baseline({"Suite": {"executed": 1}, "MissingSuite": {"executed": 43}})
        self.assertEqual(1, self.check(self.project(outcomes=["pass"])))
        self.assertIn("MissingSuite", self.summary.read_text())

    def test_invalid_baseline_never_reopens_bootstrap(self):
        for new in ({}, [], False, {"Suite": {"executed": "43"}}, {"Suite": {"executed": -1}}):
            with self.subTest(new=new):
                self.record_baseline(new)
                self.assertEqual(1, self.check(self.project(exit_code=8)))
        self.baseline.write_text("invalid json")
        self.assertEqual(1, self.check(self.project(exit_code=8)))

    def test_empty_exit_8_requires_a_valid_report_with_consistent_counts(self):
        project = self.project(exit_code=8)
        report = self.results / "Suite/Suite.xml"
        for text in ("<testsuite tests='1'/>", "<testsuite/>", "<unknown/>", "<broken"):
            with self.subTest(text=text):
                report.write_text(text)
                self.assertEqual(1, self.check(project))
        report.unlink()
        self.assertEqual(1, self.check(project))

    def test_all_project_counts_are_collected_after_another_project_fails(self):
        self.assertEqual(1, self.check(self.project("Failed", outcomes=["failure"], exit_code=2),
                                       self.project("Architecture", outcomes=["pass"] * 43)))
        record = json.loads((self.results / "test-counts.json").read_text())
        self.assertEqual(43, record["projects"]["Architecture"]["counts"]["executed"])
        self.assertEqual("pass", record["projects"]["Architecture"]["outcome"])

    def test_no_projects_is_a_failure(self):
        self.assertEqual(1, self.check())

    def test_baseline_lowered_in_the_same_pull_request_fails(self):
        previous = self.record_previous(
            {"Suite": {"executed": 43, "passed": 43}}, present=("Suite",))
        self.record_baseline({"Suite": {"executed": 42, "passed": 42}})
        self.assertEqual(1, self.check(
            self.project(outcomes=["pass"] * 43), previous=previous))
        summary = self.summary.read_text()
        self.assertIn("Suite: baseline executed count 42 is below the previous "
                      "committed baseline 43", summary)
        self.assertIn("Suite: baseline passed count 42 is below the previous "
                      "committed baseline 43", summary)

    def test_passed_count_cannot_fall_while_executed_is_unchanged(self):
        previous = self.record_previous(
            {"Suite": {"executed": 43, "passed": 43}}, present=("Suite",))
        self.record_baseline({"Suite": {"executed": 43, "passed": 42}})
        self.assertEqual(1, self.check(
            self.project(outcomes=["pass"] * 43), previous=previous))
        self.assertIn("Suite: baseline passed count 42 is below the previous "
                      "committed baseline 43", self.summary.read_text())

    def test_baseline_file_cannot_be_deleted(self):
        previous = self.record_previous(None, present=("Suite",))
        self.baseline.unlink()
        self.assertEqual(1, self.check(self.project(exit_code=8), previous=previous))
        self.assertIn("baseline file", self.summary.read_text())
        self.assertIn("was deleted; the committed baseline cannot be removed",
                      self.summary.read_text())

    def test_recorded_baseline_cannot_be_reset_to_null(self):
        previous = self.record_previous(
            {"Suite": {"executed": 43, "passed": 43}}, present=("Suite",))
        self.record_baseline(None)
        self.assertEqual(1, self.check(self.project(exit_code=8), previous=previous))
        self.assertIn("new-suite baseline was reset to null; the committed baseline "
                      "cannot be cleared", self.summary.read_text())

    def test_test_project_deleted_after_baseline_fails(self):
        previous = self.record_previous(
            {"Suite": {"executed": 43, "passed": 43}}, present=("Suite",))
        self.record_baseline({"Other": {"executed": 1, "passed": 1}})
        self.assertEqual(1, self.check(
            self.project("Other", outcomes=["pass"]), previous=previous))
        summary = self.summary.read_text()
        self.assertIn("Suite: test project from the previous committed baseline is "
                      "missing", summary)
        self.assertIn("Suite: test project existed at the base commit but was not run",
                      summary)

    def test_test_project_deleted_before_first_baseline_is_not_zero_tests(self):
        previous = self.record_previous(None, present=("Suite",))
        self.assertEqual(1, self.check(
            self.project("Other", outcomes=["pass"]), previous=previous))
        self.assertIn("Suite: test project existed at the base commit but was not run",
                      self.summary.read_text())

    def test_test_project_emptied_after_baseline_fails(self):
        previous = self.record_previous(
            {"Suite": {"executed": 43, "passed": 43}}, present=("Suite",))
        self.record_baseline({"Suite": {"executed": 0, "passed": 0}})
        self.assertEqual(1, self.check(self.project(), previous=previous))
        summary = self.summary.read_text()
        self.assertIn("Suite: baseline executed count 0 is below the previous "
                      "committed baseline 43", summary)
        self.assertIn("zero executed tests", summary)

    def test_test_project_renamed_after_baseline_fails(self):
        previous = self.record_previous(
            {"OldSuite": {"executed": 43, "passed": 43}}, present=("OldSuite",))
        self.record_baseline({"NewSuite": {"executed": 43, "passed": 43}})
        self.assertEqual(1, self.check(
            self.project("NewSuite", outcomes=["pass"] * 43), previous=previous))
        summary = self.summary.read_text()
        self.assertIn("OldSuite: test project from the previous committed baseline is "
                      "missing", summary)
        self.assertIn("OldSuite: test project existed at the base commit but was not run",
                      summary)

    def test_legitimate_first_baseline_addition_passes(self):
        previous = self.record_previous(None, present=("Suite",))
        self.record_baseline({"Suite": {"executed": 43, "passed": 43}})
        self.assertEqual(0, self.check(
            self.project(outcomes=["pass"] * 43), previous=previous))

    def test_reviewed_removal_record_allows_a_project_to_retire(self):
        previous = self.record_previous(
            {"OldSuite": {"executed": 43, "passed": 43}}, present=("OldSuite",))
        self.record_baseline(
            {"NewSuite": {"executed": 43, "passed": 43}},
            removed={"OldSuite": {"reason": "project folded into NewSuite",
                                  "approved_by": "oracle-change review"}})
        self.assertEqual(0, self.check(
            self.project("NewSuite", outcomes=["pass"] * 43), previous=previous))

    def test_removal_record_needs_a_reviewer(self):
        previous = self.record_previous(
            {"OldSuite": {"executed": 43, "passed": 43}}, present=("OldSuite",))
        self.record_baseline(
            {"NewSuite": {"executed": 1, "passed": 1}},
            removed={"OldSuite": {"reason": "project folded into NewSuite"}})
        self.assertEqual(1, self.check(
            self.project("NewSuite", outcomes=["pass"]), previous=previous))
        self.assertIn("needs a non-empty approved_by", self.summary.read_text())

    def test_removal_record_cannot_name_a_project_that_still_ran(self):
        previous = self.record_previous(
            {"Suite": {"executed": 43, "passed": 43}}, present=("Suite",))
        self.record_baseline(
            None,
            removed={"Suite": {"reason": "gone",
                               "approved_by": "oracle-change review"}})
        self.assertEqual(1, self.check(
            self.project(outcomes=["pass"] * 43), previous=previous))
        self.assertIn("Suite: removal record conflicts with a project that still ran",
                      self.summary.read_text())


if __name__ == "__main__":
    unittest.main()
