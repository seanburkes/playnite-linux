import os
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


PACKAGING = Path(__file__).resolve().parent
APP_ID = "io.github.playnite_linux.Playnite"


class PackagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.prefix = self.root / "files"
        for relative in ("bin/playnite", "lib/playnite/cli/Playnite.Cli",
                         "lib/playnite/desktop/Playnite.Desktop",
                         "lib/playnite/steam-import/Playnite.Steam.Import",
                         "lib/playnite/steam-launch/Playnite.Steam.Launch"):
            path = self.prefix / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("#!/bin/sh\nprintf '%s\\n' \"$0\" \"$@\"\n")
            path.chmod(0o755)
        self.desktop = self.prefix / f"share/applications/{APP_ID}.desktop"
        self.desktop.parent.mkdir(parents=True)
        shutil.copyfile(PACKAGING / self.desktop.name, self.desktop)
        self.icon = self.prefix / f"share/icons/hicolor/scalable/apps/{APP_ID}.svg"
        self.icon.parent.mkdir(parents=True)
        shutil.copyfile(PACKAGING / self.icon.name, self.icon)

    def verify(self):
        return subprocess.run(["python3", "-O", str(PACKAGING / "verify-entry.py"),
                               str(self.prefix), "."], capture_output=True, text=True)

    def test_complete_entry_resolves(self):
        self.assertEqual(self.verify().returncode, 0)

    def test_missing_icon_fails(self):
        self.icon.unlink()
        self.assertNotEqual(self.verify().returncode, 0)

    def test_icon_escape_fails(self):
        self.icon.unlink()
        outside = self.root / "outside.svg"
        outside.write_text("outside")
        self.icon.symlink_to(outside)
        self.assertNotEqual(self.verify().returncode, 0)

    def test_missing_desktop_binary_fails(self):
        (self.prefix / "lib/playnite/desktop/Playnite.Desktop").unlink()
        self.assertNotEqual(self.verify().returncode, 0)

    def test_nonexecutable_binary_fails(self):
        (self.prefix / "lib/playnite/cli/Playnite.Cli").chmod(0o644)
        self.assertNotEqual(self.verify().returncode, 0)

    def test_wrong_exec_fails(self):
        self.desktop.write_text(self.desktop.read_text().replace("Exec=playnite", "Exec=missing"))
        self.assertNotEqual(self.verify().returncode, 0)

    def test_launcher_routes_version_and_preserves_desktop_arguments(self):
        launcher = self.prefix / "bin/playnite"
        shutil.copyfile(PACKAGING / "playnite", launcher)
        for args, target, forwarded in (
                (["--version"], "cli/Playnite.Cli", ["--version"]),
                (["--headless", "an argument with spaces"], "desktop/Playnite.Desktop",
                 ["--headless", "an argument with spaces"]),
                (["steam-library-import", "--portal"], "steam-import/Playnite.Steam.Import",
                 ["--portal"]),
                (["steam-launch", "770100"], "steam-launch/Playnite.Steam.Launch",
                 ["770100"])):
            with self.subTest(args=args):
                result = subprocess.run([str(launcher), *args], check=True,
                                        capture_output=True, text=True, cwd="/")
                lines = result.stdout.splitlines()
                self.assertTrue(lines[0].endswith(target), lines)
                self.assertEqual(lines[1:], forwarded)

    def test_launcher_propagates_payload_failure(self):
        launcher = self.prefix / "bin/playnite"
        shutil.copyfile(PACKAGING / "playnite", launcher)
        (self.prefix / "lib/playnite/cli/Playnite.Cli").write_text("#!/bin/sh\nexit 23\n")
        result = subprocess.run([str(launcher), "--version"], env=os.environ.copy())
        self.assertEqual(result.returncode, 23)

    def test_apprun_routes_explicit_steam_acceptance_and_preserves_default_routing(self):
        appdir = self.prefix.parent
        self.prefix.rename(appdir / "usr")
        launcher = appdir / "usr/bin/playnite"
        shutil.copyfile(PACKAGING / "playnite", launcher)
        apprun = appdir / "AppRun"
        shutil.copyfile(PACKAGING / "AppRun", apprun)
        apprun.chmod(0o755)
        harness = appdir / "usr/lib/playnite/steam-acceptance/SteamAcceptance"
        harness.parent.mkdir()
        harness.write_text("#!/bin/sh\nprintf '%s\\n' \"$0\" \"$@\"\nexit 23\n")
        harness.chmod(0o755)
        for args, target, forwarded, status in (
            (["--steam-acceptance", "/library with spaces", "770100"],
             harness, ["/library with spaces", "770100"], 23),
            (["--version"], appdir / "usr/lib/playnite/cli/Playnite.Cli", ["--version"], 0),
            ([], appdir / "usr/lib/playnite/desktop/Playnite.Desktop", [], 0),
        ):
            with self.subTest(args=args):
                result = subprocess.run([str(apprun), *args], cwd="/",
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode, status, result.stderr)
                self.assertEqual(result.stdout.splitlines(), [str(target), *forwarded])

    def test_flatpak_permission_allowlist_rejects_broad_access(self):
        manifest = json.loads((PACKAGING / f"{APP_ID}.json").read_text())
        path = self.root / "manifest.json"
        report = self.root / "permissions.ini"
        narrow = "[Context]\nshared=ipc;\nsockets=x11;wayland;fallback-x11;\ndevices=dri;\n"
        for broad_manifest, broad_effective in ((False, False), (True, False), (False, True)):
            with self.subTest(manifest=broad_manifest, effective=broad_effective):
                content = dict(manifest)
                if broad_manifest:
                    content["finish-args"] = [*content["finish-args"], "--filesystem=home"]
                path.write_text(json.dumps(content))
                report.write_text(narrow + ("filesystems=host;\n" if broad_effective else ""))
                result = subprocess.run(["python3", "-O", str(PACKAGING / "verify-flatpak.py"),
                                         str(path), str(report)], capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, not (broad_manifest or broad_effective),
                                 result.stdout + result.stderr)

    def test_flatpak_runtime_lock_reports_all_refs_before_failing(self):
        locks = json.loads((PACKAGING / "flatpak-runtime-lock.json").read_text())
        flatpak = self.root / "flatpak"
        cases = [(mode, None) for mode in ("match", "missing", "unlisted", "inventory-failure")]
        cases.extend(("mismatch", ref) for ref in locks)
        for mode, mismatch_ref in cases:
            with self.subTest(mode=mode, ref=mismatch_ref):
                commits = dict(locks)
                if mode == "mismatch":
                    commits[mismatch_ref] = "f" * 64
                if mode == "unlisted":
                    commits["org.example.Unlocked/x86_64/1"] = "e" * 64
                flatpak.write_text("#!/usr/bin/env python3\nimport sys\n"
                                   f"commits = {commits!r}\n"
                                   "if sys.argv[1] == 'list':\n"
                                   + ("    print('inventory failed', file=sys.stderr)\n    sys.exit(1)\n"
                                      if mode == "inventory-failure" else
                                      "    print('\\n'.join(commits))\n    sys.exit(0)\n")
                                   + "assert sys.argv[1:4] == ['info', '--user', '--show-commit']\n"
                                   + ("print('runtime not installed', file=sys.stderr)\nsys.exit(1)\n"
                                      if mode == "missing" else "print(commits[sys.argv[4]])\n"))
                flatpak.chmod(0o755)
                result = subprocess.run(
                    ["python3", "-O", str(PACKAGING / "verify-flatpak-runtime.py"),
                     str(PACKAGING / "flatpak-runtime-lock.json"), "Test stage"],
                    env={**os.environ, "PATH": f"{self.root}:{os.environ['PATH']}"},
                    capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, mode == "match", result.stderr)
                if mode == "inventory-failure":
                    self.assertIn("Cannot enumerate installed Flatpak runtime refs", result.stderr)
                    self.assertIn("inventory failed", result.stderr)
                    continue
                for ref, expected in locks.items():
                    actual = "<unavailable>" if mode == "missing" else commits[ref]
                    self.assertIn(f"Test stage: {ref} expected={expected} actual={actual}",
                                  result.stdout)
                if mode != "match":
                    self.assertIn("Flatpak runtime lock mismatch", result.stderr)
                if mode == "missing":
                    self.assertIn("runtime not installed", result.stderr)
                if mode == "unlisted":
                    self.assertIn("org.example.Unlocked/x86_64/1 expected=<unlisted> actual="
                                  + "e" * 64, result.stdout)

    def test_flatpak_workflow_preserves_locked_runtime_boundaries(self):
        workflow = (PACKAGING.parent / ".github/workflows/packaging.yml").read_text()
        setup = workflow.split("- name: Install pinned Flatpak tooling and runtime branch", 1)[1]
        setup_lines = [line.strip() for line in setup.splitlines()]
        self.assertIn('flatpak install --user --noninteractive --no-related --no-deps flathub "$ref"',
                      setup_lines)
        self.assertIn('flatpak update --user --noninteractive --no-related --no-deps '
                      '--commit="$commit" "$ref"', setup_lines)
        self.assertIn("python3 packaging/verify-flatpak-runtime.py "
                      "packaging/flatpak-runtime-lock.json 'After setup'", setup_lines)
        smoke = setup.split("- name: Build, install bundle and smoke the sandboxed Flatpak", 1)[1]
        smoke_lines = [line.strip() for line in smoke.splitlines()]
        self.assertIn("set -euo pipefail", smoke_lines)
        self.assertIn('python3 "$GITHUB_WORKSPACE/packaging/verify-flatpak-runtime.py" \\', smoke_lines)
        self.assertIn('"$GITHUB_WORKSPACE/packaging/flatpak-runtime-lock.json" "$1"', smoke_lines)
        boundaries = [
            "verify_runtime 'Before builder'",
            'flatpak-builder --user --disable-rofiles-fuse --repo=repo build "$id.json"',
            "verify_runtime 'After builder'",
            'flatpak build-bundle repo "artifacts/Playnite-Linux-x86_64.flatpak" "$id"',
            "verify_runtime 'After build-bundle'",
            'flatpak install --user --noninteractive --no-deps artifacts/Playnite-Linux-x86_64.flatpak',
            "verify_runtime 'After bundle install'",
            'version=$(timeout 30s flatpak run --user "$id" --version)',
            'timeout 30s flatpak run --user "$id" --headless --smoke',
            "verify_runtime 'After sandbox smoke'",
            "echo '## Flatpak: PASS'",
        ]
        offset = 0
        for command in boundaries:
            with self.subTest(command=command):
                offset = smoke_lines.index(command, offset) + 1

    def test_assembly_rejects_dirty_input_before_publishing(self):
        repo = self.root / "repo"
        repo.mkdir()
        subprocess.run(["git", "init", "-q", str(repo)], check=True)
        script = repo / "assemble.sh"
        shutil.copyfile(PACKAGING / "assemble.sh", script)
        result = subprocess.run(["bash", str(script), str(self.root / "output")],
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("requires a clean committed checkout", result.stderr)
        self.assertFalse((self.root / "output").exists())
