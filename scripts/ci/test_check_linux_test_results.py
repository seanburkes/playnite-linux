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

    def record_baseline(self, projects):
        self.baseline.write_text(json.dumps({"legacy": {"executed": 281}, "new": projects}))

    def check(self, *projects):
        with contextlib.redirect_stdout(io.StringIO()):
            return check_results(projects, self.results, self.baseline, self.summary)

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


if __name__ == "__main__":
    unittest.main()
