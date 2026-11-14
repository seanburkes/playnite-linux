#!/usr/bin/env python3
"""Deterministic inventory of the frozen Playnite v10 snapshot.

Usage:
    python3 scripts/port/scan_legacy.py --format md
    python3 scripts/port/scan_legacy.py --format json > inventory.json

The output is intentionally simple and stable so it can be diffed in CI.
No third-party dependencies.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEGACY = ROOT / "legacy" / "source"

PROJECTS = [
    "Playnite",
    "PlayniteSDK",
    "Playnite.DesktopApp",
    "Playnite.FullscreenApp",
    "Playnite.BrowserProcess",
    "Playnite.Deps",
    "Tools",
    "Tests",
]

EXCLUDE_DIRS = {"bin", "obj", ".git", ".vs", "TestResults"}

WINDOWS_PATTERNS = [
    re.compile(r"System\.Windows(?!\.Input\.ICommand)"),
    re.compile(r"System\.Drawing"),
    re.compile(r"Microsoft\.Win32"),
    re.compile(r"System\.Management(?!\.Automation)"),
    re.compile(r"ServiceController"),
    re.compile(r"\bDllImport\b"),
    re.compile(r"\bLibraryImport\b"),
    re.compile(r"WindowsAPICodePack"),
    re.compile(r"System\.Windows\.Forms"),
]

WPF_PATTERN = re.compile(r"System\.Windows|System\.Drawing")

NON_WPF_WINDOWS_PATTERNS = [
    re.compile(r"System\.Drawing"),
    re.compile(r"Microsoft\.Win32"),
    re.compile(r"System\.Management(?!\.Automation)"),
    re.compile(r"ServiceController"),
    re.compile(r"\bDllImport\b"),
    re.compile(r"\bLibraryImport\b"),
    re.compile(r"WindowsAPICodePack"),
    re.compile(r"System\.Windows\.Forms"),
]

PACKAGE_RE = re.compile(
    r'<PackageReference\s+Include="([^"]+)"\s+[Vv]ersion="([^"]+)"', re.IGNORECASE
)
TEST_ATTR_RE = re.compile(r"\[(Test|TestCase|TestCaseSource|TestFixture|Theory|Fact|InlineData)\]")
PUBLIC_TYPE_RE = re.compile(
    r"public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*"
    r"(interface|class|enum|struct|record)\s+([A-Za-z_][A-Za-z0-9_]*)"
)


def iter_files(base: Path, suffix: str):
    for path in base.rglob(f"*{suffix}"):
        if any(part in EXCLUDE_DIRS for part in path.parts):
            continue
        if path.is_file():
            yield path


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8-sig", errors="replace")
    except OSError:
        return ""


def line_count(path: Path) -> int:
    return len(read_text(path).splitlines())


def collect_project(project: str) -> dict:
    base = LEGACY / project
    if not base.exists():
        return {}
    cs = list(iter_files(base, ".cs"))
    xaml = list(iter_files(base, ".xaml"))
    axaml = list(iter_files(base, ".axaml"))
    cs_loc = sum(line_count(p) for p in cs)
    xaml_loc = sum(line_count(p) for p in xaml + axaml)
    windows_files = []
    for p in cs:
        text = read_text(p)
        if any(pat.search(text) for pat in WINDOWS_PATTERNS):
            windows_files.append(str(p.relative_to(LEGACY)))
    wpf_files = [str(p.relative_to(LEGACY)) for p in cs if WPF_PATTERN.search(read_text(p))]
    windows_only_files = []
    drawing_files = []
    wmi_files = []
    powershell_files = []
    for p in cs:
        text = read_text(p)
        rel = str(p.relative_to(LEGACY))
        if any(pat.search(text) for pat in NON_WPF_WINDOWS_PATTERNS):
            windows_only_files.append(rel)
        if "System.Drawing" in text:
            drawing_files.append(rel)
        if "System.Management" in text or "ServiceController" in text:
            wmi_files.append(rel)
        if "System.Management.Automation" in text or "PowerShell" in text:
            powershell_files.append(rel)
    test_attrs = Counter()
    for p in cs:
        for m in TEST_ATTR_RE.finditer(read_text(p)):
            test_attrs[m.group(1)] += 1
    return {
        "cs_files": len(cs),
        "cs_loc": cs_loc,
        "xaml_files": len(xaml) + len(axaml),
        "xaml_loc": xaml_loc,
        "windows_files": sorted(windows_files),
        "windows_only_files": sorted(windows_only_files),
        "wpf_files": sorted(wpf_files),
        "drawing_files": sorted(drawing_files),
        "wmi_files": sorted(wmi_files),
        "powershell_files": sorted(powershell_files),
        "test_attrs": dict(test_attrs),
    }


def collect_packages() -> dict:
    versions = defaultdict(set)
    for path in iter_files(LEGACY, ".csproj"):
        for m in PACKAGE_RE.finditer(read_text(path)):
            versions[m.group(1)].add(m.group(2))
    return {k: sorted(v) for k, v in sorted(versions.items())}


def collect_public_types() -> dict:
    sdk = LEGACY / "PlayniteSDK"
    types = []
    for path in iter_files(sdk, ".cs"):
        for m in PUBLIC_TYPE_RE.finditer(read_text(path)):
            types.append({"kind": m.group(1), "name": m.group(2),
                          "file": str(path.relative_to(LEGACY))})
    return sorted(types, key=lambda t: (t["kind"], t["name"]))


def collect_sdk_metrics() -> dict:
    sdk = LEGACY / "PlayniteSDK"
    files = list(iter_files(sdk, ".cs"))
    interface_names = set()
    public_type_names = set()
    for p in files:
        text = read_text(p)
        interface_names.update(re.findall(r"\bpublic\s+interface\s+([A-Za-z_][A-Za-z0-9_]*)", text))
        public_type_names.update(m.group(2) for m in PUBLIC_TYPE_RE.finditer(text))
    return {
        "files": len(files),
        "loc": sum(line_count(p) for p in files),
        "interfaces": len(interface_names),
        "public_types": len(public_type_names),
    }


def collect_counts() -> dict:
    cs = list(iter_files(LEGACY, ".cs"))
    xaml = list(iter_files(LEGACY, ".xaml"))
    axaml = list(iter_files(LEGACY, ".axaml"))
    tests = list(iter_files(LEGACY / "Tests", ".cs"))
    test_text = "\n".join(read_text(p) for p in tests)
    localization = LEGACY / "Playnite" / "Localization"
    loc_source = localization / "LocSource.xaml"
    loc_keys = 0
    if loc_source.exists():
        loc_keys = len(re.findall(r'x:Key="([^"]+)"', read_text(loc_source)))
    language_files = 0
    if localization.exists():
        language_files = len([
            p for p in localization.glob("*.xaml")
            if p.name != "LocSource.xaml" and "_" in p.stem
        ])
    return {
        "cs_files": len(cs),
        "cs_loc": sum(line_count(p) for p in cs),
        "xaml_files": len(xaml) + len(axaml),
        "xaml_loc": sum(line_count(p) for p in xaml + axaml),
        "test_cases": len(re.findall(r"\[TestCase(?:Source)?\s*\(", test_text)),
        "test_methods": len(re.findall(r"\[Test\]", test_text)),
        "test_fixtures": len(re.findall(r"\[TestFixture\]", test_text)),
        "localization_keys": loc_keys,
        "language_files": language_files,
    }


def scan() -> dict:
    projects = {p: collect_project(p) for p in PROJECTS}
    return {
        "legacy_path": str(LEGACY),
        "counts": collect_counts(),
        "projects": projects,
        "packages": collect_packages(),
        "sdk": collect_sdk_metrics(),
        "public_types": collect_public_types(),
    }


def to_markdown(data: dict) -> str:
    out = []
    out.append("# Generated inventory\n")
    out.append("Generated by `scripts/port/scan_legacy.py` from `legacy/`. Do not edit by hand.\n")
    c = data["counts"]
    out.append(f"- C#: **{c['cs_files']}** files, **{c['cs_loc']:,}** lines")
    out.append(f"- XAML/AXAML: **{c['xaml_files']}** files, **{c['xaml_loc']:,}** lines")
    out.append(f"- Tests: **{c['test_methods']}** `[Test]` methods, **{c['test_cases']}** `[TestCase*]` attributes, **{c['test_fixtures']}** fixtures")
    out.append(f"- Localisation: **{c['localization_keys']}** keys, **{c['language_files']}** language files")
    s = data["sdk"]
    out.append(f"- SDK: **{s['files']}** files, **{s['loc']:,}** lines, "
               f"**{s['interfaces']}** interfaces, **{s['public_types']}** public types")
    out.append("")
    out.append("## Projects\n")
    out.append("| Project | C# files | C# LOC | XAML files | XAML LOC | Windows/WPF-coupled files | WPF-only files | Tests |")
    out.append("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for name, p in data["projects"].items():
        if not p:
            continue
        tests = p["test_attrs"].get("Test", 0) + p["test_attrs"].get("TestCase", 0)
        out.append(f"| {name} | {p['cs_files']} | {p['cs_loc']:,} | {p['xaml_files']} | "
                   f"{p['xaml_loc']:,} | {len(p['windows_files'])} | {len(p['wpf_files'])} | {tests} |")
    out.append("")
    out.append("## Packages\n")
    out.append("| Package | Versions |")
    out.append("| --- | --- |")
    for name, versions in data["packages"].items():
        out.append(f"| {name} | {', '.join(versions)} |")
    out.append("")
    out.append("## Windows-coupled files\n")
    for name, p in data["projects"].items():
        if not p or not p["windows_files"]:
            continue
        out.append(f"### {name}\n")
        for f in p["windows_files"]:
            out.append(f"- `{f}`")
        out.append("")
    out.append("## SDK public types\n")
    out.append("One row per declared public type. This is the seed for the SDK v7 API report.\n")
    out.append("| Kind | Name | File |")
    out.append("| --- | --- | --- |")
    for t in data["public_types"]:
        out.append(f"| {t['kind']} | {t['name']} | `{t['file']}` |")
    out.append("")
    return "\n".join(out)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--format", choices=["md", "json"], default="md")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    if not LEGACY.exists():
        print(f"error: legacy source not found at {LEGACY}", file=sys.stderr)
        return 1

    data = scan()
    text = json.dumps(data, indent=2, sort_keys=True) if args.format == "json" else to_markdown(data)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(text + "\n", encoding="utf-8")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
