#!/usr/bin/env python3
"""Compile frozen v10 model sources, never Core, and capture their observations.

Usage: python3 scripts/port/generate_model_goldens.py --output /tmp/opencode/models-v10
Output must be new. Existing oracle bytes are never overwritten. Build files live in
the caller's temporary directory, not legacy/ or the repository's product graph.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    names = "AgeRating AgeRatingOrg Category Company CompletionStatus DatabaseObject Game GameAction GameFeature GameMetadata GameRom GameSource Genre IIdentifiable InstallationStatus InstallSizeGroup Link PastTimeSegment Platform PlaytimeCategory Region ReleaseDate ScoreGroup Series Tag".split()
    sources = [f"legacy/source/PlayniteSDK/Models/{name}.cs" for name in names]
    sources += ["legacy/source/PlayniteSDK/Collections/ObservableObject.cs",
                "legacy/source/PlayniteSDK/Extensions/ListExtensions.cs"]
    sources += [f"legacy/source/PlayniteSDK/Exceptions/{name}.cs"
                for name in ("LocalizedException", "ReferenceException", "TypeMismatchException")]
    sources += ["tests/Playnite.Core.Tests/Models/ModelProbe.cs",
                "scripts/port/model-oracle/Shims.cs", "scripts/port/model-oracle/Program.cs"]
    # A new directory is required even when the caller accidentally points at a committed golden.
    args.output.mkdir(parents=True, exist_ok=False)
    with tempfile.TemporaryDirectory(prefix="model-oracle-", dir=args.output.parent) as work:
        project = Path(work) / "Oracle.csproj"
        links = "\n".join(f'<Compile Include="{escape(str(root / s))}" />' for s in sources)
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>annotations</Nullable>
<DefineConstants>V10_MODELS</DefineConstants><EnableDefaultCompileItems>false</EnableDefaultCompileItems>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
<ItemGroup>{links}</ItemGroup></Project>\n''')
        subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--nologo"], check=True, cwd=root)
        result = subprocess.run(["dotnet", str(Path(work) / "bin/Release/net11.0/Oracle.dll")],
                                check=True, text=True, capture_output=True, cwd=root)
        observations = json.loads(result.stdout)
        (args.output / "equality.json").write_text(json.dumps(observations, indent=2, ensure_ascii=True) + "\n")
    provenance_sources = sources + ["scripts/port/generate_model_goldens.py",
                                    "legacy/source/PlayniteSDK/Data/DataSerialization.cs"]
    provenance = {
        "oracle": "Playnite 10.64 frozen source, compiled unmodified on .NET 11 (not Windows CLR execution)",
        "generator": "python3 scripts/port/generate_model_goldens.py --output <new-directory>",
        "scope": "Pure shapes/equality/hashing/copy/diff; database and resource seams throw if reached. Runtime string and identity hashes are observed relationally, never persisted as numeric constants.",
        "sources": {s: hashlib.sha256((root / s).read_bytes()).hexdigest() for s in provenance_sources},
        "equality_sha256": hashlib.sha256((args.output / "equality.json").read_bytes()).hexdigest(),
        "observations": len(observations),
    }
    (args.output / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    print(f"Captured {len(observations)} observations in {args.output}")


if __name__ == "__main__":
    main()
