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


def base_port_tool():
    """The tool version committed on origin/main (the live base tool)."""
    result = subprocess.run(
        ["git", "-C", str(ROOT), "show",
         "origin/main:scripts/port/build_port_index.py"],
        capture_output=True, text=True, check=False)
    return result.stdout if result.returncode == 0 else PORT_TOOL.read_text()

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
        base_port_tool())
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


def use_head_tool(repo):
    (repo / "scripts" / "port" / "build_port_index.py").write_text(
        PORT_TOOL.read_text())


def run_tool(repo, base_sha, recorded_base=None):
    env = os.environ.copy()
    if recorded_base is not None:
        env["RECORDED_BASE"] = recorded_base
    return subprocess.run(
        [sys.executable, str(repo / "scripts" / "port" / "build_port_index.py"),
         "check", "--shards-only", "--against", base_sha,
         "--forbid-aggregate-changes"],
        cwd=repo, env=env, capture_output=True, text=True, check=False)


def run_step(script, repo, base_sha, recorded_base=None):
    env = os.environ.copy()
    env["BASE_SHA"] = base_sha
    env["RECORDED_BASE"] = recorded_base or base_sha
    env["RUNNER_TEMP"] = str(repo / "runner")
    (repo / "runner").mkdir(exist_ok=True)
    return subprocess.run(["bash", "-e", "-c", script], cwd=repo, env=env,
                          capture_output=True, text=True, check=False)


def outputs(path):
    return dict(line.split("=", 1) for line in path.read_text().splitlines()
                if "=" in line)


def resolve_policy(repo, event, pull_request_base="", merge_group_base="",
                   push_before=""):
    script = run_block(PLAN_GUARDS, "Resolve the comparison base commit")
    output = repo / "policy-output"
    env = os.environ.copy()
    env.update({"EVENT_NAME": event, "PULL_REQUEST_BASE": pull_request_base,
                "MERGE_GROUP_BASE": merge_group_base,
                "PUSH_BEFORE": push_before, "GITHUB_OUTPUT": str(output)})
    result = subprocess.run(["bash", "-e", "-c", script], cwd=repo, env=env,
                            capture_output=True, text=True, check=False)
    return result, outputs(output) if output.exists() else {}


def merge_main(repo, keep_old_aggregates=False):
    _, main_tip = stale_pr_repo(repo)
    git(repo, "merge", "--no-ff", "-q", "-m", "merge main", "main")
    if keep_old_aggregates:
        old = git(repo, "merge-base", main_tip, "HEAD^1")
        git(repo, "checkout", old, "--", "docs/port/queue.tsv",
            "docs/port/ledger.tsv")
        commit(repo, "carry base aggregates")
    return main_tip


def recorded_repo(repo):
    """A fork point with old aggregates and a newer recorded base with new ones."""
    base = base_repo(repo)
    git(repo, "checkout", "-q", "-b", "recorded")
    write_shard(repo, "p-recorded")
    build(repo)
    recorded = commit(repo, "recorded base aggregates")
    git(repo, "checkout", "-q", "main")
    git(repo, "checkout", "-q", "-b", "pr")
    git(repo, "checkout", recorded, "--", "docs/port/queue.tsv",
        "docs/port/ledger.tsv")
    commit(repo, "carry recorded-base aggregates")
    return base, recorded


def merge_main_then_advance(repo, edit_aggregate=False):
    _, _ = stale_pr_repo(repo)
    git(repo, "merge", "--no-ff", "-q", "-m", "merge main", "main")
    git(repo, "checkout", "-q", "main")
    write_shard(repo, "p-newer")
    build(repo)
    newer_main = commit(repo, "main advanced again")
    git(repo, "checkout", "-q", "pr")
    git(repo, "merge", "--no-ff", "-q", "-m", "merge newer main", "main")
    if edit_aggregate:
        queue = repo / "docs" / "port" / "queue.tsv"
        queue.write_text(queue.read_text() + "edited in merge\n")
        commit(repo, "edit aggregate in merge")
    return newer_main


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
            self.assertIn("error: pull requests must not modify", result.stderr)
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

    def test_tool_fork_content_passes_with_newer_recorded_base(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base, recorded = recorded_repo(repo)
            git(repo, "checkout", base, "--", "docs/port/queue.tsv",
                "docs/port/ledger.tsv")
            commit(repo, "carry fork aggregates")
            use_head_tool(repo)
            result = run_tool(repo, base, recorded)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_tool_recorded_base_content_passes(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base, recorded = recorded_repo(repo)
            use_head_tool(repo)
            result = run_tool(repo, base, recorded)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            result = run_tool(repo, base, base)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_tool_divergent_content_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base, recorded = recorded_repo(repo)
            queue = repo / "docs" / "port" / "queue.tsv"
            queue.write_text(queue.read_text() + "divergent\n")
            commit(repo, "divergent aggregate")
            use_head_tool(repo)
            result = run_tool(repo, base, recorded)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_tool_add_delete_rename_fail_with_recorded_base(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base, recorded = recorded_repo(repo)
            queue = repo / "docs" / "port" / "queue.tsv"
            queue.unlink()
            commit(repo, "delete aggregate")
            use_head_tool(repo)
            result = run_tool(repo, base, recorded)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base, recorded = recorded_repo(repo)
            queue = repo / "docs" / "port" / "queue.tsv"
            queue.rename(queue.with_name("queue-old.tsv"))
            commit(repo, "rename aggregate")
            use_head_tool(repo)
            result = run_tool(repo, base, recorded)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            git(repo, "init", "-q")
            git(repo, "config", "user.email", "aggregate-test@example.invalid")
            git(repo, "config", "user.name", "Aggregate Test")
            (repo / "scripts" / "port").mkdir(parents=True)
            (repo / "scripts" / "port" / "build_port_index.py").write_text(
                base_port_tool())
            write_shard(repo, "p-test")
            (repo / "README.md").write_text("base\n")
            base = commit(repo, "base without aggregates")
            build(repo)
            commit(repo, "add aggregates")
            use_head_tool(repo)
            result = run_tool(repo, base, base)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_merge_main_pr_with_current_main_aggregates_passes(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            main_tip = merge_main(repo)
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_merge_main_pr_carrying_old_aggregates_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            main_tip = merge_main(repo, keep_old_aggregates=True)
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_merge_commit_with_divergent_aggregate_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            main_tip = merge_main(repo)
            queue = repo / "docs" / "port" / "queue.tsv"
            queue.write_text(queue.read_text() + "divergent\n")
            commit(repo, "divergent aggregate")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_deleting_an_aggregate_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            (repo / "docs" / "port" / "queue.tsv").unlink()
            commit(repo, "delete aggregate")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_renaming_an_aggregate_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            queue = repo / "docs" / "port" / "queue.tsv"
            queue.rename(queue.with_name("queue-old.tsv"))
            commit(repo, "rename aggregate")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_editing_one_aggregate_row_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            queue = repo / "docs" / "port" / "queue.tsv"
            lines = queue.read_text().splitlines()
            lines[1] = lines[1].replace("\ttodo\t", "\tin-progress\t")
            queue.write_text("\n".join(lines) + "\n")
            commit(repo, "edit one aggregate row")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_head_aggregate_tool_substitution_cannot_weaken_checks(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            (repo / "scripts" / "port" / "build_port_index.py").write_text(
                "import sys\nsys.exit(0)\n")
            queue = repo / "docs" / "port" / "queue.tsv"
            queue.write_text(queue.read_text() + "edit\n")
            commit(repo, "weaken head tool and edit aggregate")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_stale_live_pr_whole_chain_passes(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            resolved, values = resolve_policy(repo, "pull_request",
                                              pull_request_base=main_tip)
            self.assertEqual(0, resolved.returncode, resolved.stdout + resolved.stderr)
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, values["sha"], values["recorded"])
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_recorded_base_content_step_passes_after_merge(self):
        # Post-merge reality: the base-materialised tool now understands
        # RECORDED_BASE. A real aggregate edit is still covered by the negative
        # tests below and by test_aggregate_edited_in_merge_commit_whole_chain_fails.
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base, recorded = recorded_repo(repo)
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, base, recorded)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_recorded_base_tolerance_head_tool_passes(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            base, recorded = recorded_repo(repo)
            use_head_tool(repo)
            result = run_tool(repo, base, recorded)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_aggregate_edited_in_merge_commit_whole_chain_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            newer_main = merge_main_then_advance(repo, edit_aggregate=True)
            resolved, values = resolve_policy(repo, "pull_request",
                                              pull_request_base=newer_main)
            self.assertEqual(0, resolved.returncode, resolved.stdout + resolved.stderr)
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, values["sha"], values["recorded"])
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/queue.tsv", result.stderr)

    def test_missing_event_base_whole_chain_fails_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            stale_pr_repo(repo)
            resolved, _ = resolve_policy(repo, "pull_request", pull_request_base="")
            self.assertEqual(1, resolved.returncode)
            self.assertIn("pull_request event has no usable base SHA", resolved.stdout)

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

    def test_first_map_may_be_seeded_by_a_non_product_pr(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            use_head_tool(repo)
            (repo / "docs" / "port" / "units" / "ownership.paths").write_text(
                "path\tunit\tcommit\tpull\n")
            commit(repo, "seed the first ownership map")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("BOOTSTRAP: base has no ownership.paths", result.stdout)

    def test_first_map_seeding_with_a_product_change_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            use_head_tool(repo)
            (repo / "docs" / "port" / "units" / "ownership.paths").write_text(
                "path\tunit\tcommit\tpull\n")
            product = repo / "src" / "App" / "Thing.cs"
            product.parent.mkdir(parents=True)
            product.write_text("// product\n")
            commit(repo, "seed a map with a product change")
            result = run_step(run_block(PLAN_GUARDS, "Port aggregates are generated on main"),
                              repo, main_tip)
            self.assertEqual(1, result.returncode)
            self.assertIn("changed: docs/port/units/ownership.paths", result.stderr)

    def test_stale_pr_shard_history_uses_merge_base(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            _, main_tip = stale_pr_repo(repo)
            result = run_step(run_block(PLAN_GUARDS, "Port shards are valid and history is preserved"),
                              repo, main_tip)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
