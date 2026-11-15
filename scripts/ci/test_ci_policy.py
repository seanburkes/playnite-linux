"""Guard the CI policy surface and its self-closing ratchet wiring."""

from pathlib import Path
import os
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


def run_block(path, name):
    block = step_block(path, name).splitlines()
    for index, line in enumerate(block):
        if line.strip() != "run: |":
            continue
        run_indent = len(line) - len(line.lstrip())
        body = []
        for following in block[index + 1:]:
            if not following.strip():
                body.append("")
                continue
            if len(following) - len(following.lstrip()) <= run_indent:
                break
            body.append(following)
        indent = min(len(line) - len(line.lstrip()) for line in body if line.strip())
        return "\n".join(line[indent:] if line.strip() else "" for line in body)
    raise AssertionError(f"{path.name} step {name!r} has no run block")


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
    git(root, "branch", "-M", "main")
    return git(root, "rev-parse", "HEAD")


def commit(repo, message):
    git(repo, "add", ".")
    git(repo, "commit", "-q", "-m", message)
    return git(repo, "rev-parse", "HEAD")


def materialise(repo, base, runner_temp, output):
    script = run_block(LINUX_CI, "Materialise the base policy scripts")
    env = os.environ.copy()
    env.update({"BASE_SHA": base, "RUNNER_TEMP": str(runner_temp),
                "GITHUB_OUTPUT": str(output)})
    return subprocess.run(["bash", "-e", "-c", script], cwd=repo, env=env,
                          capture_output=True, text=True, check=False)


def outputs(path):
    return dict(line.split("=", 1) for line in path.read_text().splitlines()
                if "=" in line)


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
        self.assertIn("steps.policy_scripts.outputs.gate", step)
        self.assertNotIn("continue-on-error", step)

    def test_merge_queue_protected_path_step_runs(self):
        step = step_block(
            PLAN_GUARDS,
            "Oracle artifacts require an explicit oracle-change merge-queue entry")
        self.assertIn("github.event_name == 'merge_group'", step)
        self.assertIn("gh api", step)
        self.assertIn("oracle-change", step)
        self.assertIn("steps.policy_scripts.outputs.gate", step)
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

    def test_pr_branched_before_main_protected_change_is_not_charged(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            committed_repo(repo)
            git(repo, "checkout", "-q", "-b", "pr")
            (repo / "README.md").write_text("pr change\n")
            commit(repo, "pr change")
            git(repo, "checkout", "-q", "main")
            policy = repo / ".github" / "workflows" / "linux-ci.yml"
            policy.parent.mkdir(parents=True)
            policy.write_text("main policy change\n")
            main_tip = commit(repo, "main policy change")
            git(repo, "checkout", "-q", "pr")
            result = run("check_protected_paths.py", "--base", main_tip,
                         "--paths", ".github/workflows/linux-ci.yml", cwd=repo)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_pr_own_protected_change_is_still_charged(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            committed_repo(repo)
            git(repo, "checkout", "-q", "-b", "pr")
            policy = repo / ".github" / "workflows" / "linux-ci.yml"
            policy.parent.mkdir(parents=True)
            policy.write_text("pr policy change\n")
            commit(repo, "pr policy change")
            git(repo, "checkout", "-q", "main")
            policy.parent.mkdir(parents=True, exist_ok=True)
            policy.write_text("main policy change\n")
            main_tip = commit(repo, "main policy change")
            git(repo, "checkout", "-q", "pr")
            result = run("check_protected_paths.py", "--base", main_tip,
                         "--paths", ".github/workflows/linux-ci.yml", cwd=repo)
            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn("Protected oracle artifacts changed", result.stdout)

    def test_unresolvable_merge_base_fails_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            committed_repo(repo)
            result = run("check_protected_paths.py", "--base", "does-not-exist",
                         "--paths", ".github/workflows/linux-ci.yml", cwd=repo)
            self.assertEqual(1, result.returncode)
            self.assertIn("Cannot compare protected paths", result.stdout)

    def test_base_policy_script_is_used_when_present(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            gate = repo / "scripts" / "ci" / "check_protected_paths.py"
            gate.parent.mkdir(parents=True)
            gate.write_text("import sys\nsys.exit(1)\n")
            (repo / "scripts" / "ci" / "check_linux_test_results.py").write_text(
                "import sys\nsys.exit(0)\n")
            base = committed_repo(repo)
            gate.write_text("import sys\nsys.exit(0)\n")
            commit(repo, "weaken head gate")
            runner_temp = repo / "runner"
            output = repo / "output"
            result = materialise(repo, base, runner_temp, output)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            values = outputs(output)
            self.assertEqual("base", values["gate_source"])
            self.assertEqual("base", values["checker_source"])
            selected = Path(values["gate"])
            self.assertTrue(selected.is_relative_to(runner_temp))
            run_gate = subprocess.run([sys.executable, str(selected)],
                                      capture_output=True, text=True, check=False)
            self.assertEqual(1, run_gate.returncode)

    def test_unreadable_base_policy_script_fails_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            gate = repo / "scripts" / "ci" / "check_protected_paths.py"
            gate.mkdir(parents=True)
            (gate / "not-a-script").write_text("x\n")
            base = committed_repo(repo)
            result = materialise(repo, base, repo / "runner", repo / "output")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("could not be materialised", result.stdout)

    def test_edited_head_checker_cannot_mask_base_checker_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            checker = repo / "scripts" / "ci" / "check_linux_test_results.py"
            checker.parent.mkdir(parents=True)
            checker.write_text("import sys\nsys.exit(1)\n")
            base = committed_repo(repo)
            checker.write_text("import sys\nsys.exit(0)\n")
            commit(repo, "weaken head checker")
            output = repo / "output"
            result = materialise(repo, base, repo / "runner", output)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            values = outputs(output)
            self.assertEqual("base", values["checker_source"])
            run_checker = subprocess.run([sys.executable, values["checker"]],
                                         capture_output=True, text=True, check=False)
            self.assertEqual(1, run_checker.returncode)

    def test_bootstrap_notice_when_base_script_is_absent(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base = committed_repo(repo)
            (repo / "scripts" / "ci").mkdir(parents=True)
            (repo / "scripts" / "ci" / "check_protected_paths.py").write_text(
                "import sys\nsys.exit(0)\n")
            commit(repo, "add policy")
            output = repo / "output"
            result = materialise(repo, base, repo / "runner", output)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("BOOTSTRAP", result.stdout)
            values = outputs(output)
            self.assertEqual("head", values["gate_source"])
            self.assertEqual("scripts/ci/check_protected_paths.py", values["gate"])

    def test_head_copy_pass_cannot_mask_base_copy_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            gate = repo / "scripts" / "ci" / "check_protected_paths.py"
            gate.parent.mkdir(parents=True)
            gate.write_text("import sys\nsys.exit(1)\n")
            base = committed_repo(repo)
            gate.write_text("import sys\nsys.exit(0)\n")
            commit(repo, "weaken head gate")
            output = repo / "output"
            result = materialise(repo, base, repo / "runner", output)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            values = outputs(output)
            run_gate = subprocess.run([sys.executable, values["gate"]],
                                      capture_output=True, text=True, check=False)
            self.assertEqual(1, run_gate.returncode)

    def test_merge_queue_and_base_resolution_are_wired(self):
        for workflow in (LINUX_CI, PLAN_GUARDS):
            text = workflow.read_text()
            with self.subTest(workflow=workflow.name):
                self.assertIn("merge_group:", text)
                self.assertIn("resolve_base_sha.py", text)
                self.assertIn("github.event.merge_group.base_sha", text)
        self.assertIn("fetch-depth: 0", LINUX_CI.read_text())
        capture = step_block(LINUX_CI, "Capture the committed policy baseline")
        self.assertIn("previous-baseline.json", capture)
        self.assertIn("previous-projects.txt", capture)
        self.assertIn("list_test_projects.py", capture)
        step_block(LINUX_CI, "Materialise the base policy scripts")
        checker = LINUX_CI.read_text()
        self.assertIn("PREVIOUS_BASELINE_STATE", checker)
        self.assertIn("PREVIOUS_PROJECTS_STATE", checker)
        self.assertIn("steps.policy_scripts.outputs.checker", checker)
        self.assertIn("--previous-baseline ", checker)
        self.assertIn("--previous-projects ", checker)

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

    def test_worktree_project_discovery_uses_the_same_filter(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            files = {
                "tests/Real.Tests/Real.Tests.csproj":
                    "<Project><PropertyGroup><IsTestProject>true</IsTestProject>"
                    "</PropertyGroup></Project>\n",
                "tests/fixtures/Fixture.csproj": "<Project />\n",
                "tests/Real.Tests/obj/Generated.csproj":
                    "<Project><PropertyGroup><IsTestProject>true</IsTestProject>"
                    "</PropertyGroup></Project>\n",
            }
            for name, content in files.items():
                path = repo / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content)
            result = run("list_test_projects.py", "--worktree", str(repo))
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual([str((repo / "tests/Real.Tests/Real.Tests.csproj").resolve())],
                             result.stdout.splitlines())

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
