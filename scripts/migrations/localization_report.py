#!/usr/bin/env python3
"""Verify the explicit migration against immutable inputs; write only its small report."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SOURCE = "legacy/source/Playnite/Localization/LocSource.xaml"
CONSTANTS = "legacy/source/Playnite/Localization/LocalizationKeys.cs"
GENERATOR = "legacy/build/buildLocConstants.ps1"
ENGLISH = "src/Playnite.Core/Localization/English.json"
MAPPING = "src/Playnite.Core/Migrations/localization-v10.json"
REPORT = "docs/port/p3-009-migration-report.json"


def read_source():
    root = ET.parse(ROOT / SOURCE).getroot()
    key_name = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"
    return {node.attrib[key_name]: node.text for node in root
            if node.tag == "{clr-namespace:System;assembly=mscorlib}String"
            and node.attrib.get(key_name) and node.text}


def render_report():
    source = read_source()
    target = json.loads((ROOT / ENGLISH).read_text(encoding="utf-8"))
    data = json.loads((ROOT / MAPPING).read_text(encoding="utf-8"))
    mapping = {}
    groups = []
    for group in data["groups"]:
        for key, value in group["mapping"].items():
            if key in mapping:
                raise ValueError(f"Duplicate mapping: {key}")
            mapping[key] = value
        groups.append({"id": group["id"], "count": len(group["mapping"]),
                       "provenance": group["provenance"]})
    migrated = {}
    unmapped, obsolete = [], []
    for key, value in sorted(source.items()):
        if key not in mapping or (mapping[key] is not None and mapping[key] not in target):
            unmapped.append(key)
        elif mapping[key] is None:
            obsolete.append(key)
        elif mapping[key] in migrated:
            raise ValueError(f"Colliding destination: {mapping[key]}")
        else:
            migrated[mapping[key]] = value
    report = {
        "command": "python3 scripts/migrations/localization_report.py --check",
        "inputs": {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest()
                   for path in [SOURCE, CONSTANTS, GENERATOR, ENGLISH, MAPPING]},
        "groups": groups,
        "counts": {"source": len(source), "target": len(target), "mapped": len(migrated),
                   "unmapped": len(unmapped), "obsolete": len(obsolete)},
        "unmapped": unmapped, "obsolete": obsolete,
        "orphanMappings": sorted(set(mapping) - set(source)),
        "diff": {"added": sorted(set(migrated) - set(target)),
                 "removed": sorted(set(target) - set(migrated)),
                 "changed": sorted(key for key in set(target) & set(migrated)
                                   if target[key] != migrated[key])},
    }
    return json.dumps(report, ensure_ascii=False, indent=2) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    expected = render_report()
    path = ROOT / REPORT
    if args.check:
        if path.read_text(encoding="utf-8") != expected:
            raise SystemExit("Migration report is stale")
        print("Migration report matches fixed inputs")
    else:
        path.write_text(expected, encoding="utf-8")
    report = json.loads(expected)
    if (report["counts"]["source"] != 1134 or report["counts"]["mapped"] != 1134
            or report["unmapped"] or report["obsolete"] or report["orphanMappings"]
            or any(report["diff"].values())):
        raise SystemExit("English migration is incomplete; see report")


if __name__ == "__main__":
    main()
