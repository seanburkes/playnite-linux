"""Guard the CI policy surface and its self-closing ratchet wiring."""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "scripts" / "ci"
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


def step_block(path, name):
    lines = path.read_text().splitlines()
    for index, line in enumerate(lines):
        if line.strip() == f"- name: {name}":
            block = []
            for following in lines[index:]:
                if block and following.strip().startswith("- name:"):
                    break
                block.append(following)
            return "\n".join(block)
    raise AssertionError(f"{path.name} has no step named {name!r}")


def codeowners():
    owners = {}
    for line in CODEOWNERS.read_text().splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        parts = line.split()
        owners[parts[0]] = parts[1:]
    return owners


def run(script, *args, cwd=ROOT):
    return subprocess.run([sys.executable, str(SCRIPTS / script), *args],
                          cwd=cwd, capture_output=True, text=True, check=False)


def git(repo, *args):
    return subprocess.run(["git", *args], cwd=repo, capture_output=True,
                          text=True, check=True).stdout.strip()


def committed_repo(root):
    git(root, "init", "-q")
    git(root, "config", "user.email", "policy-test@example.invalid")
    git(root, "config", "user.name", "Policy Test")
    (root / "README.md").write_text("base\n")
    git(root, "add", ".")
    git(root, "commit", "-q", "-m", "base")
    return git(root, "rev-parse", "HEAD")


class CiPolicyTests(unittest.TestCase):
    def test_ci_policy_surface_is_protected_and_codeowned(self):
        paths = protected_paths()
        owners = codeowners()
        for path in (".github/workflows/linux-ci.yml",
                     ".github/workflows/plan-guards.yml",
                     ".github/CODEOWNERS",
                     "tests/baseline.json",
                     "scripts/ci"):
            with self.subTest(path=path):
                self.assertIn(path, paths)
                key = path if path in owners else path + "/"
                self.assertIn("@seanburkes", owners.get(key, []))

    def test_policy_workflow_edited_without_the_label_is_gated(self):
        step = step_block(PLAN_GUARDS,
                          "Oracle artifacts require an explicit oracle-change PR")
        self.assertIn("github.event_name == 'pull_request'", step)
        self.assertIn("!contains(github.event.pull_request.labels.*.name, "
                      "'oracle-change')", step)
        self.assertIn("check_protected_paths.py", step)
        self.assertNotIn("continue-on-error", step)

    def test_protected_path_change_exits_nonzero(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base = committed_repo(repo)
            path = repo / ".github" / "workflows" / "linux-ci.yml"
            path.parent.mkdir(parents=True)
            path.write_text("changed\n")
            git(repo, "add", ".")
            git(repo, "commit", "-q", "-m", "change policy")
            result = run("check_protected_paths.py", "--base", base,
                         "--paths", ".github/workflows/linux-ci.yml", cwd=repo)
            self.assertEqual(1, result.returncode, result.stderr)
            self.assertIn("Protected oracle artifacts changed", result.stdout)

    def test_unprotected_path_change_exits_zero(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base = committed_repo(repo)
            (repo / "README.md").write_text("changed\n")
            git(repo, "add", ".")
            git(repo, "commit", "-q", "-m", "change readme")
            result = run("check_protected_paths.py", "--base", base,
                         "--paths", ".github/workflows/linux-ci.yml", cwd=repo)
            self.assertEqual(0, result.returncode, result.stderr)

    def test_merge_queue_and_base_resolution_are_wired(self):
        for workflow in (LINUX_CI, PLAN_GUARDS):
            text = workflow.read_text()
            with self.subTest(workflow=workflow.name):
                self.assertIn("merge_group:", text)
                self.assertIn("resolve_base_sha.py", text)
                self.assertIn("github.event.merge_group.base_sha", text)
        capture = step_block(LINUX_CI, "Capture the committed policy baseline")
        self.assertIn("previous-baseline.json", capture)
        self.assertIn("previous-projects.txt", capture)
        self.assertIn("list_test_projects.py", capture)
        checker = LINUX_CI.read_text()
        self.assertIn("--previous-baseline-state", checker)
        self.assertIn("--previous-projects-state", checker)

    def test_pull_request_with_no_base_sha_fails_closed(self):
        result = run("resolve_base_sha.py", "--event", "pull_request",
                     "--pull-request-base", "")
        self.assertEqual(1, result.returncode)
        self.assertIn("pull_request event has no usable base SHA", result.stdout)

    def test_merge_group_with_no_base_sha_fails_closed(self):
        result = run("resolve_base_sha.py", "--event", "merge_group",
                     "--merge-group-base", "")
        self.assertEqual(1, result.returncode)
        self.assertIn("merge_group event has no usable base SHA", result.stdout)

    def test_merge_group_uses_its_base_sha(self):
        result = run("resolve_base_sha.py", "--event", "merge_group",
                     "--merge-group-base", "a" * 40)
        self.assertEqual(0, result.returncode)
        self.assertEqual("a" * 40, result.stdout.strip())

    def test_first_push_without_a_before_sha_allows_first_baseline(self):
        result = run("resolve_base_sha.py", "--event", "push",
                     "--push-before", "0" * 40)
        self.assertEqual(0, result.returncode)
        self.assertEqual("", result.stdout.strip())

    def test_base_project_discovery_selects_test_projects_only(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            files = {
                "tests/Real.Tests/Real.Tests.csproj":
                    "<Project><PropertyGroup><IsTestProject>true</IsTestProject>"
                    "</PropertyGroup></Project>\n",
                "tests/Other.Suite/Other.Suite.csproj":
                    "<Project><ItemGroup><PackageReference "
                    "Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>\n",
                "tests/fixtures/Fixture.csproj": "<Project />\n",
            }
            for name, content in files.items():
                path = repo / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content)
            committed_repo(repo)
            result = run("list_test_projects.py", "--ref", "HEAD", cwd=repo)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(["Other.Suite", "Real.Tests"],
                             result.stdout.splitlines())


if __name__ == "__main__":
    unittest.main()
