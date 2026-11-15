"""Guard the CI policy surface and its self-closing ratchet wiring."""

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
PLAN_GUARDS = ROOT / ".github" / "workflows" / "plan-guards.yml"
LINUX_CI = ROOT / ".github" / "workflows" / "linux-ci.yml"
CODEOWNERS = ROOT / ".github" / "CODEOWNERS"


def protected_paths():
    lines = PLAN_GUARDS.read_text().splitlines()
    for index, line in enumerate(lines):
        if line.strip() != "PROTECTED_PATHS: >-":
            continue
        indent = len(line) - len(line.lstrip())
        values = []
        for following in lines[index + 1:]:
            if not following.strip():
                continue
            if len(following) - len(following.lstrip()) <= indent:
                break
            values.extend(following.split())
        return values
    raise AssertionError("plan-guards has no PROTECTED_PATHS block")


def step_block(name):
    lines = PLAN_GUARDS.read_text().splitlines()
    for index, line in enumerate(lines):
        if line.strip() == f"- name: {name}":
            block = []
            for following in lines[index:]:
                if block and following.strip().startswith("- name:"):
                    break
                block.append(following)
            return "\n".join(block)
    raise AssertionError(f"plan-guards has no step named {name!r}")


def codeowners():
    owners = {}
    for line in CODEOWNERS.read_text().splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        parts = line.split()
        owners[parts[0]] = parts[1:]
    return owners


class CiPolicyTests(unittest.TestCase):
    def test_ci_policy_workflow_and_baseline_are_protected(self):
        paths = protected_paths()
        self.assertIn(".github/workflows/linux-ci.yml", paths)
        self.assertIn("tests/baseline.json", paths)
        self.assertIn("scripts/ci", paths)

    def test_policy_workflow_edited_without_the_label_is_gated(self):
        step = step_block("Oracle artifacts require an explicit oracle-change PR")
        self.assertIn("github.event_name == 'pull_request'", step)
        self.assertIn("!contains(github.event.pull_request.labels.*.name, "
                      "'oracle-change')", step)
        self.assertIn("$PROTECTED_PATHS", step)
        self.assertIn("Protected oracle artifacts changed", step)

    def test_policy_surface_is_codeowned(self):
        owners = codeowners()
        for path in (".github/workflows/linux-ci.yml", "tests/baseline.json", "scripts/ci/"):
            with self.subTest(path=path):
                self.assertIn("@seanburkes", owners.get(path, []))

    def test_linux_ci_feeds_the_previous_committed_policy_to_the_checker(self):
        workflow = LINUX_CI.read_text()
        self.assertIn("fetch-depth: 0", workflow)
        self.assertIn("--previous-baseline", workflow)
        self.assertIn("--previous-projects", workflow)


if __name__ == "__main__":
    unittest.main()
