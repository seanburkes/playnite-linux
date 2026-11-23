import os
import json
from pathlib import Path
import shutil
import struct
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
                         "lib/playnite/launch/Playnite.Launch"):
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
                (["launch", "770100"], "launch/Playnite.Launch", ["770100"])):
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
        lock_data = json.loads((PACKAGING / "flatpak-runtime-lock.json").read_text())
        flatpak = self.root / "flatpak"
        for arch, locks in lock_data.items():
            cases = [(mode, None) for mode in ("match", "missing", "unlisted", "inventory-failure")]
            cases.extend(("mismatch", ref) for ref in locks)
            for mode, mismatch_ref in cases:
                with self.subTest(arch=arch, mode=mode, ref=mismatch_ref):
                    commits = dict(locks)
                    if mode == "mismatch":
                        commits[mismatch_ref] = "f" * 64
                    if mode == "unlisted":
                        commits[f"org.example.Unlocked/{arch}/1"] = "e" * 64
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
                         "--arch", arch, str(PACKAGING / "flatpak-runtime-lock.json"), "Test stage"],
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
                        self.assertIn(f"org.example.Unlocked/{arch}/1 expected=<unlisted> actual="
                                      + "e" * 64, result.stdout)

        unsupported = subprocess.run(
            ["python3", "-O", str(PACKAGING / "verify-flatpak-runtime.py"),
             "--arch", "riscv64", str(PACKAGING / "flatpak-runtime-lock.json"), "Test stage"],
            capture_output=True, text=True)
        self.assertNotEqual(unsupported.returncode, 0)
        self.assertIn("Unsupported Flatpak runtime lock architecture: riscv64", unsupported.stderr)

    def test_flatpak_runtime_lock_covers_x64_and_arm64_with_matching_refs(self):
        lock_data = json.loads((PACKAGING / "flatpak-runtime-lock.json").read_text())
        self.assertEqual(set(lock_data), {"x86_64", "aarch64"})
        normalized = {}
        for arch, refs in lock_data.items():
            normalized[arch] = {ref.replace(f"/{arch}/", "/{arch}/") for ref in refs}
            for ref, commit in refs.items():
                self.assertIn(f"/{arch}/", ref)
                self.assertGreaterEqual(len(commit), 63, ref)
                int(commit, 16)
        self.assertEqual(normalized["x86_64"], normalized["aarch64"])

    def test_flatpak_workflow_preserves_locked_runtime_boundaries(self):
        workflow = (PACKAGING.parent / ".github/workflows/packaging.yml").read_text()
        setup = workflow.split("- name: Install pinned Flatpak tooling and runtime branch", 1)[1]
        setup_lines = [line.strip() for line in setup.splitlines()]
        self.assertIn('flatpak install --user --noninteractive --no-related --no-deps flathub "$ref"',
                      setup_lines)
        self.assertIn('flatpak update --user --noninteractive --no-related --no-deps '
                      '--commit="$commit" "$ref"', setup_lines)
        self.assertIn("python3 packaging/verify-flatpak-runtime.py "
                      "--arch \"$TARGET_ARCH\" packaging/flatpak-runtime-lock.json 'After setup'",
                      setup_lines)
        smoke = setup.split("- name: Build, install bundle and smoke the sandboxed Flatpak", 1)[1]
        smoke_lines = [line.strip() for line in smoke.splitlines()]
        self.assertIn("set -euo pipefail", smoke_lines)
        self.assertIn('python3 "$GITHUB_WORKSPACE/packaging/verify-flatpak-runtime.py" \\', smoke_lines)
        self.assertIn('--arch "$TARGET_ARCH" \\', smoke_lines)
        self.assertIn('"$GITHUB_WORKSPACE/packaging/flatpak-runtime-lock.json" "$1"', smoke_lines)
        boundaries = [
            "verify_runtime 'Before builder'",
            'flatpak-builder --user --disable-rofiles-fuse --repo=repo build "$id.json"',
            "verify_runtime 'After builder'",
            'flatpak build-bundle repo "artifacts/Playnite-Linux-${TARGET_ARCH}.flatpak" "$id"',
            "verify_runtime 'After build-bundle'",
            'flatpak install --user --noninteractive --no-deps "artifacts/Playnite-Linux-${TARGET_ARCH}.flatpak"',
            "verify_runtime 'After bundle install'",
            'version=$(timeout 30s flatpak run --user "$id" --version)',
            'timeout 30s flatpak run --user "$id" --headless --smoke',
            "verify_runtime 'After sandbox smoke'",
            'echo "## Flatpak ($TARGET_ARCH): PASS"',
        ]
        offset = 0
        for command in boundaries:
            with self.subTest(command=command):
                offset = smoke_lines.index(command, offset) + 1

    def test_flatpak_workflow_covers_x64_and_arm64(self):
        workflow = (PACKAGING.parent / ".github/workflows/packaging.yml").read_text()
        for snippet in (
            "job_name: Flatpak packaging smoke",
            "job_name: Flatpak ARM64 packaging smoke",
            "runner: ubuntu-24.04",
            "runner: ubuntu-24.04-arm",
            "rid: linux-x64",
            "rid: linux-arm64",
            "arch: x86_64",
            "arch: aarch64",
            'python3 "$GITHUB_WORKSPACE/packaging/verify-entry.py" --arch "$TARGET_ARCH" "$installed/files" .',
        ):
            with self.subTest(snippet=snippet):
                self.assertIn(snippet, workflow)

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

    def test_assembly_rejects_unsupported_rid(self):
        repo = self.root / "repo"
        repo.mkdir()
        subprocess.run(["git", "init", "-q", str(repo)], check=True)
        subprocess.run(["git", "-C", str(repo), "config", "user.email", "test@example.com"], check=True)
        subprocess.run(["git", "-C", str(repo), "config", "user.name", "Test"], check=True)
        script = repo / "assemble.sh"
        shutil.copyfile(PACKAGING / "assemble.sh", script)
        subprocess.run(["git", "-C", str(repo), "add", "assemble.sh"], check=True)
        subprocess.run(["git", "-C", str(repo), "commit", "-q", "-m", "init"], check=True)
        result = subprocess.run(["bash", str(script), str(self.root / "output"), "win-x64"],
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Unsupported runtime identifier: win-x64", result.stderr)
        self.assertFalse((self.root / "output").exists())

    @staticmethod
    def _elf64_header(machine: int) -> bytes:
        header = bytearray(64)
        header[0:4] = b"\x7fELF"
        header[4] = 2  # ELFCLASS64
        header[5] = 1  # ELFDATA2LSB
        header[6] = 1  # EV_CURRENT
        struct.pack_into("<HHI", header, 16, 3, machine, 1)  # ET_DYN, e_machine, EV_CURRENT
        return bytes(header)

    def test_verify_entry_checks_elf_architecture_for_x64_and_arm64(self):
        apphosts = (
            "lib/playnite/cli/Playnite.Cli",
            "lib/playnite/desktop/Playnite.Desktop",
            "lib/playnite/steam-import/Playnite.Steam.Import",
            "lib/playnite/launch/Playnite.Launch",
            "lib/playnite/desktop/legacy-import/Playnite.LegacyImport",
        )
        for arch, machine, other_machine in (("x86_64", 62, 183), ("aarch64", 183, 62)):
            with self.subTest(arch=arch):
                for relative in apphosts:
                    path = self.prefix / relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(self._elf64_header(machine))
                    path.chmod(0o755)
                ok = subprocess.run(
                    ["python3", "-O", str(PACKAGING / "verify-entry.py"),
                     "--arch", arch, str(self.prefix), "."],
                    capture_output=True, text=True)
                self.assertEqual(ok.returncode, 0, ok.stdout + ok.stderr)
                self.assertIn(f"Verified ELF ({arch}):", ok.stdout)

                # Mismatched ELF architecture must fail.
                (self.prefix / apphosts[0]).write_bytes(self._elf64_header(other_machine))
                bad = subprocess.run(
                    ["python3", "-O", str(PACKAGING / "verify-entry.py"),
                     "--arch", arch, str(self.prefix), "."],
                    capture_output=True, text=True)
                self.assertNotEqual(bad.returncode, 0)
                self.assertIn("Unexpected ELF machine", bad.stderr)

    def test_appimage_workflow_covers_x64_and_arm64_with_pinned_checksums(self):
        workflow = (PACKAGING.parent / ".github/workflows/packaging.yml").read_text()
        for snippet in (
            "job_name: AppImage packaging smoke",
            "runner: ubuntu-24.04",
            "rid: linux-x64",
            "arch: x86_64",
            "job_name: AppImage ARM64 packaging smoke",
            "runner: ubuntu-24.04-arm",
            "rid: linux-arm64",
            "arch: aarch64",
            "ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0",
            "2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d",
            "f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158",
            "00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444",
            'python3 "$GITHUB_WORKSPACE/packaging/verify-entry.py" --arch "$TARGET_ARCH" squashfs-root',
        ):
            with self.subTest(snippet=snippet):
                self.assertIn(snippet, workflow)
