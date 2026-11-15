"""Plan-guards aggregate ownership must compare against the fork point."""

from pathlib import Path
import os
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
PLAN_GUARDS = ROOT / ".github" / "workflows" / "plan-guards.yml"
PORT_TOOL = ROOT / "scripts" / "port" / "build_port_index.py"

QUEUE_HEADER = "id\twave\tarea\tunit\tdepends\tstatus\towner\toracle\ttest\tnotes\n"


def run_block(path, name):
    lines = path.read_text().splitlines()
    for index, line in enumerate(lines):
        if line.strip() != f"- name: {name}":
            continue
        block = []
        for following in lines[index:]:
            if block and following.strip().startswith("- name:"):
                break
            block.append(following)
        for position, entry in enumerate(block):
            if entry.strip() != "run: |":
                continue
            run_indent = len(entry) - len(entry.lstrip())
            body = []
            for following in block[position + 1:]:
                if not following.strip():
                    body.append("")
                    continue
                if len(following) - len(following.lstrip()) <= run_indent:
                    break
                body.append(following)
            indent = min(len(line) - len(line.lstrip())
                         for line in body if line.strip())
            return "\n".join(line[indent:] if line.strip() else "" for line in body)
    raise AssertionError(f"no run block for {name!r}")


def git(repo, *args):
    return subprocess.run(["git", *args], cwd=repo, capture_output=True,
                          text=True, check=True).stdout.strip()


def commit(repo, message):
    git(repo, "add", ".")
    git(repo, "commit", "-q", "-m", message)
    return git(repo, "rev-parse", "HEAD")


def write_shard(repo, unit, notes="shard"):
    path = repo / "docs" / "port" / "units" / f"{unit}.queue.tsv"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(QUEUE_HEADER + f"{unit}\tW2\ttest\t{unit}\t-\ttodo\tfm/test\t-\t-\t{notes}\n")


def build(repo):
    subprocess.run([sys.executable, str(repo / "scripts" / "port" / "build_port_index.py"),
                    "build"], cwd=repo, capture_output=True, text=True, check=True)


def base_repo(root):
    git(root, "init", "-q")
    git(root, "config", "user.email", "aggregate-test@example.invalid")
    git(root, "config", "user.name", "Aggregate Test")
    (root / "scripts" / "port").mkdir(parents=True)
    (root / "scripts" / "port" / "build_port_index.py").write_text(
        PORT_TOOL.read_text())
    write_shard(root, "p-test")
    build(root)
    (root / "README.md").write_text("base\n")
    base = commit(root, "base")
    git(root, "branch", "-M", "main")
    return base


def stale_pr_repo(root):
    base = base_repo(root)
    git(root, "checkout", "-q", "-b", "pr")
    (root / "README.md").write_text("pr change\n")
    commit(root, "pr change")
    git(root, "checkout", "-q", "main")
    write_shard(root, "p-main")
    build(root)
    main_tip = commit(root, "main aggregate regeneration")
    git(root, "checkout", "-q", "pr")
    return base, main_tip


def run_step(script, repo, base_sha):
    env = os.environ.copy()
    env["BASE_SHA"] = base_sha
    return subprocess.run(["bash", "-e", "-c", script], cwd=repo, env=env,
                          capture_output=True, text=True, check=False)


class AggregateOwnershipTests(unittest.TestCase):
    def test_stale_pr_passes_after_unrelated_main_aggregate_regeneration(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_pr_editing_an_aggregate_still_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            (repo / "docs" / "port" / "queue.tsv").write_text(
                (repo / "docs" / "port" / "queue.tsv").read_text() + "\n")
            commit(repo, "edit aggregate")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(1, result.returncode)
            self.assertIn("error: pull requests must not modify the generated aggregates. "
                          "Edit only your unit shard under docs/port/units/ and leave "
                          "docs/port/queue.tsv and docs/port/ledger.tsv to the post-merge "
                          "port-index workflow; drop those file changes from this PR.",
                          result.stderr)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_pr_editing_only_its_shard_passes(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            write_shard(repo, "p-new")
            commit(repo, "add own shard")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_missing_merge_base_fails_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            stale_pr_repo(repo)
            missing = "a" * 40
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, missing)
            self.assertEqual(1, result.returncode)
            self.assertIn(f"::error::Cannot compute the aggregate merge base for {missing}",
                          result.stdout)

    def test_stale_pr_shard_history_uses_merge_base(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            result = run_step(run_block(PLAN_GUARDS, "Port shards are valid and history is preserved"),
                              repo, main_tip)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
