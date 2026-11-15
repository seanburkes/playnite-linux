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
                         "lib/playnite/desktop/Playnite.Desktop"):
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
        for args, target in ((["--version"], "cli/Playnite.Cli"),
                             (["--headless", "an argument with spaces"], "desktop/Playnite.Desktop")):
            with self.subTest(args=args):
                result = subprocess.run([str(launcher), *args], check=True,
                                        capture_output=True, text=True, cwd="/")
                lines = result.stdout.splitlines()
                self.assertTrue(lines[0].endswith(target), lines)
                self.assertEqual(lines[1:], args)

    def test_launcher_propagates_payload_failure(self):
        launcher = self.prefix / "bin/playnite"
        shutil.copyfile(PACKAGING / "playnite", launcher)
        (self.prefix / "lib/playnite/cli/Playnite.Cli").write_text("#!/bin/sh\nexit 23\n")
        result = subprocess.run([str(launcher), "--version"], env=os.environ.copy())
        self.assertEqual(result.returncode, 23)

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
