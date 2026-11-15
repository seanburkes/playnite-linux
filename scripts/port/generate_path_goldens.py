#!/usr/bin/env python3
"""Generate additive p3-001 evidence, never run the Linux implementation as an oracle.

Invocation from repository root: python3 scripts/port/generate_path_goldens.py
The application suffixes are extracted from v10, not copied from the port. Helper cases
are explicit POSIX translations of the named v10 tests, with intentional differences
documented in tests/golden/paths/README.md. This is source-derived, not a Windows run.
"""
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
SOURCE = "legacy/source/Playnite/Settings/PlaynitePaths.cs"
TESTS = "legacy/source/Tests/Playnite.Tests/PathsTests.cs"
HELPERS = "legacy/source/Playnite/Common/Paths.cs"
OUTPUT = ROOT / "tests/golden/paths"


def provenance(*sources):
    return {"kind": "v10-source-derived POSIX adaptation (not a Windows execution)",
            "invocation": "python3 scripts/port/generate_path_goldens.py",
            "sources": {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in sources}}


def write(name, value):
    OUTPUT.mkdir(parents=True, exist_ok=True)
    (OUTPUT / name).write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main():
    source = (ROOT / SOURCE).read_text(encoding="utf-8-sig")
    constants = dict(re.findall(r'public const string (\w+) = "([^"]*)";', source))
    roots = {"ProgramPath": "/opt/playnite", "ConfigRootPath": "/home/test-user/.config/playnite",
             "DataCachePath": "/home/test-user/.cache/playnite"}
    expected = dict(roots)
    expected.update(Home="/home/test-user", Documents="/home/test-user/Documents",
                    Program=roots["ProgramPath"], Config=roots["ConfigRootPath"],
                    UserData="/home/test-user/.local/share/playnite", Cache=roots["DataCachePath"],
                    State="/home/test-user/.local/state/playnite", Logs="/home/test-user/.local/state/playnite/logs")
    # Explicit XDG relocation decisions; suffix spelling remains v10's oracle.
    data_members = {"ThemesUserDataPath", "ExtensionsUserDataPath", "ExtensionsDataPath"}
    cache_members = {"BrowserCachePath", "JitProfilesPath"}
    state_members = {"SafeStartupFlagFile", "BackupActionFile", "RestoreBackupActionFile"}
    for name, parent, arguments in re.findall(r'(\w+) = Path.Combine\((ProgramPath|ConfigRootPath|DataCachePath), ([^;]+)\);', source):
        if name in {"UninstallerPath", "DesktopExecutablePath", "FullscreenExecutablePath",
                    "BrowserProcessExecutablePath", "PlayniteAssemblyPath", "PlayniteSDKAssemblyPath", "DataCachePath"}:
            continue
        suffix = [part.strip().strip('"') if part.strip().startswith('"') else constants[part.strip()]
                  for part in arguments.split(',')]
        root = roots[parent]
        if name in data_members:
            root = "/home/test-user/.local/share/playnite"
        elif name in cache_members:
            root = roots["DataCachePath"]
        elif name in state_members:
            root = "/home/test-user/.local/state/playnite"
        elif name == "LogPath":
            root = "/home/test-user/.local/state/playnite/logs"
        expected[name] = "/".join([root, *suffix])
    # This unit does not port localisation status, emulation or executable paths.
    for name in ("EmulationDatabasePath",):
        expected.pop(name, None)
    write("application.json", {"provenance": provenance(SOURCE), "paths": expected})

    cases = []

    def case(helper, inputs, expected, origin):
        cases.append(dict(helper=helper, inputs=inputs, expected=expected, origin=origin))

    case("FixSeparators", ["/GOG Games//AAAlbion///DOSBOX//dosbox.exe"],
         "/GOG Games/AAAlbion/DOSBOX/dosbox.exe", "FixSeparatorsTest: repeated separators")
    case("FixSeparators", ["/これはユニコードパステストです//dosbox.exe"],
         "/これはユニコードパステストです/dosbox.exe", "FixSeparatorsTest: Unicode")
    case("FixSeparators", ["/a\\b//c"], "/a\\b/c", "POSIX delta: backslashes are legal filename characters")
    case("FixSeparators", [""], "", "v10 whitespace short-circuit")
    for left, right, equal in [("/test", "/TesT", False), ("test", "test/", True),
                               ("../test/", "../test", True), ("./test/", "test", True),
                               ("file.exe", "./file.exe", True), ("file2.exe", "./file.exe", False),
                               ("", "", False)]:
        case("AreEqual", [left, right], equal, "AreEqualTest; POSIX ordinal case delta")
    for path, full in [("/test", True), ("/test/../test.exe", True), ("test", False), ("./test", False), ("", False)]:
        case("IsFullPath", [path], full, "IsFullPathTest: POSIX absolute paths replace drive/UNC roots")
    for path, valid in [("test.db", True), ("/test.db", True), ("../test.db", True), ("test", False), ("", False)]:
        case("IsValidFilePath", [path], valid, "GetValidFilePathTest; root / exists in fake filesystem")
    case("GetSafePathName", ["test /\x00 aaa "], "test aaa", "GetSafeFilenameTest: POSIX invalid characters replace Windows >>")
    case("ContainsInvalidPathChars", ["/test\x00/aaa.txt"], True, "ContainsInvalidFileNameCharsTest: POSIX NUL")
    case("ContainsInvalidPathChars", ["/test/aaa.txt"], False, "ContainsInvalidFileNameCharsTest")
    for inputs, expected in [(["/test/file.exe", "/test/file2.exe", "/test/"], "/test/"),
                             (["/test/file/aa.exe", "/test/file/bb.ee", "/test/file/cc.ss"], "/test/file/"),
                             (["/file1.aa"] * 3, "/"), (["a/test", "b/test"], "")]:
        case("GetCommonDirectory", inputs, expected, "GetCommonDirectoryTest: drive roots translated to POSIX")
    case("Combine", ["/test/", "child", "file"], "/test/child/file", "PlaynitePaths.UpdateUserDataDir Path.Combine derivations")
    case("Normalize", ["/test/../test/./"], "/test", "AreEqualTest normalization exposed with explicit cwd")
    case("Normalize", ["./file.exe"], "/working/file.exe", "AreEqualTest relative path normalization")
    case("Normalize", ["/"], "/", "POSIX delta: preserve filesystem root")
    write("helpers.json", {"provenance": provenance(TESTS, HELPERS), "cases": cases})


if __name__ == "__main__":
    main()
