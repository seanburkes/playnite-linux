#!/usr/bin/env python3
"""Report frozen v10 language coverage and format-placeholder defects; never write legacy/."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
ENGLISH = "legacy/source/Playnite/Localization/LocSource.xaml"
LOCALIZATION = ROOT / "legacy/source/Playnite/Localization"
REPORT = "docs/port/p3-loc-coverage.json"
XAML_KEY = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"


def declarations(path):
    entries = {}
    for node in ET.parse(path).getroot():
        key = node.get(XAML_KEY)
        if not key:
            continue
        if key in entries:
            raise ValueError(f"Duplicate key in {path.name}: {key}")
        entries[key] = "".join(node.itertext())
    return entries


def placeholders(text):
    found = []
    index = 0
    while index < len(text):
        if text[index] == "{":
            if index + 1 < len(text) and text[index + 1] == "{":
                index += 2
                continue
            close = text.find("}", index + 1)
            if close > index + 1 and placeholder_body(text[index + 1:close]):
                found.append(text[index:close + 1])
                index = close + 1
                continue
        elif text[index] == "}" and index + 1 < len(text) and text[index + 1] == "}":
            index += 2
            continue
        index += 1
    return sorted(found)


def placeholder_body(body):
    position = 0
    if not body or not body[0].isdigit() or not body[0].isascii():
        return False
    while position < len(body) and body[position].isdigit() and body[position].isascii():
        position += 1
    if position < len(body) and body[position] == ",":
        position += 1
        if position < len(body) and body[position] == "-":
            position += 1
        start = position
        while position < len(body) and body[position].isdigit() and body[position].isascii():
            position += 1
        if position == start:
            return False
    if position < len(body) and body[position] == ":":
        return "{" not in body[position + 1:] and "}" not in body[position + 1:]
    return position == len(body)


def coverage(language, path, english):
    translated = declarations(path)
    missing_keys = sorted(key for key in english if key not in translated)
    extra_keys = sorted(key for key in translated if key not in english)
    present = 0
    untranslated = 0
    mismatches = []
    for key in sorted(english):
        if key not in translated:
            continue
        text = translated[key]
        if not text:
            untranslated += 1
            continue
        present += 1
        expected = placeholders(english[key])
        actual = placeholders(text)
        if expected != actual:
            mismatches.append({
                "key": key,
                "englishPlaceholders": expected,
                "translatedPlaceholders": actual,
            })
    return {
        "language": language,
        "file": path.relative_to(ROOT).as_posix(),
        "present": present,
        "missing": len(missing_keys),
        "extra": len(extra_keys),
        "untranslated": untranslated,
        "missingKeys": missing_keys,
        "extraKeys": extra_keys,
        "placeholderMismatches": mismatches,
    }


def generate():
    english = declarations(ROOT / ENGLISH)
    languages = sorted(path for path in LOCALIZATION.glob("*.xaml") if path.name != Path(ENGLISH).name)
    inputs = [ENGLISH, *(path.relative_to(ROOT).as_posix() for path in languages)]
    report = {
        "provenance": {
            "oracle": "Playnite 10.64 frozen legacy tree",
            "generator": "python3 scripts/loc/generate_coverage.py",
            "english": ENGLISH,
            "inputs": {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest()
                       for path in sorted(inputs)},
            "keyCount": len(english),
            "languageCount": len(languages),
            "coverageRule": (
                "present counts non-empty translations of English keys; missing is an English key "
                "not declared by the language file; extra is a declared key absent from English; "
                "untranslated is a declared English key with an empty value, because v10 falls back "
                "to English for empty translations; whitespace-only text is a translation"),
            "placeholderRule": (
                "compare the ordinal multiset of unescaped numbered .NET composite-format "
                "placeholders; {{ and }} are literal braces, so changing or dropping an index or "
                "format is a defect rather than a missing string; named braces such as {name}, "
                "percent tokens and arbitrary brace text are not placeholders"),
            "textContract": (
                "XML-decoded character data, preserving source whitespace; not rendered XAML"),
        },
        "languages": [coverage(path.stem, path, english) for path in languages],
    }
    return {REPORT: json.dumps(report, ensure_ascii=False, indent=2) + "\n"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Verify without modifying any files")
    args = parser.parse_args()
    for relative, content in generate().items():
        path = ROOT / relative
        if args.check:
            if not path.exists() or path.read_bytes() != content.encode("utf-8"):
                raise SystemExit(f"Stale localisation coverage report: {relative}")
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content.encode("utf-8"))
    print("Localisation coverage report verified" if args.check else "Localisation coverage report generated")


if __name__ == "__main__":
    main()
