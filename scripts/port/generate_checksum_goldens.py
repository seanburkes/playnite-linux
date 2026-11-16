#!/usr/bin/env python3
"""Run the frozen v10 checksum source on .NET 11; never compile the port into the oracle.

Invocation: python3 scripts/port/generate_checksum_goldens.py --output /tmp/opencode/checksums-v10.json
Only --output is written in the repository if explicitly requested. Build scratch is outside it.
The oracle compiles legacy/source/Playnite/Common/FileSystem_Checksum.cs unchanged, with the
Crc32.NET version pinned by legacy/source/Playnite/Playnite.csproj. Every expectation is
cross-checked against CPython's zlib.crc32/hashlib before it is written; a disagreement fails
the generator rather than producing an expectation from the port.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile
import zlib

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "legacy/source"
FROZEN = "Playnite/Common/FileSystem_Checksum.cs"
FROZEN_PROJECT = "Playnite/Playnite.csproj"
FROZEN_TEST = "Tests/Playnite.Tests/FileSystemTests.cs"
FROZEN_TEST_FILE = "Tests/Playnite.Tests/Resources/TestIni.ini"

PROGRAM = r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Playnite.Common;

namespace Playnite.SDK { }

public sealed class Pattern { public int length { get; set; } public int step { get; set; } public int offset { get; set; } }
public sealed class ByteSpec { public string hex { get; set; } public Pattern pattern { get; set; } }
public sealed class Case
{
    public string op { get; set; }
    public ByteSpec input { get; set; }
    public List<ByteSpec> chunks { get; set; }
    public List<ByteSpec> inputs { get; set; }
    public int? split { get; set; }
    public string expected { get; set; }
    public string source { get; set; }
    public string cross_check { get; set; }
    public string v10_test_constant { get; set; }
}

public static class Program
{
    private static byte[] Bytes(ByteSpec spec)
    {
        if (spec.hex != null)
        {
            return Convert.FromHexString(spec.hex);
        }

        var pattern = spec.pattern;
        var data = new byte[pattern.length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * pattern.step + pattern.offset) & 0xFF);
        }

        return data;
    }

    private static string Write(string directory, string name, byte[] data)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    public static void Main(string[] args)
    {
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(args[0]));
        var directory = Path.Combine(Path.GetTempPath(), "checksum-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var index = 0;
            foreach (var item in cases)
            {
                try
                {
                    item.expected = Run(item, directory, index++);
                }
                catch (Exception error)
                {
                    item.expected = "!" + error.GetType().Name;
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }

        File.WriteAllText(args[1], JsonSerializer.Serialize(cases,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Run(Case item, string directory, int index)
    {
        switch (item.op)
        {
            case "crc32-stream":
                return item.input == null
                    ? FileSystem.GetCRC32((Stream)null)
                    : FileSystem.GetCRC32(new MemoryStream(Bytes(item.input)));
            case "crc32-file":
                return FileSystem.GetCRC32(Write(directory, "crc-" + index, Bytes(item.input)));
            case "md5-stream":
                return item.input == null
                    ? FileSystem.GetMD5((Stream)null)
                    : FileSystem.GetMD5(new MemoryStream(Bytes(item.input)));
            case "md5-file":
                return FileSystem.GetMD5(Write(directory, "md5-" + index, Bytes(item.input)));
            case "sha256-file":
                return FileSystem.GetSHA256(Write(directory, "sha-" + index, Bytes(item.input)));
            case "crc32-append":
            {
                var crc = 0U;
                foreach (var chunk in item.chunks)
                {
                    var data = Bytes(chunk);
                    crc = Force.Crc32.Crc32Algorithm.Append(crc, data, 0, data.Length);
                }

                return crc.ToString("X8");
            }
            case "crc32-append-split":
            {
                var data = Bytes(item.input);
                var size = item.split ?? 4096;
                var crc = 0U;
                for (var offset = 0; offset < data.Length; offset += size)
                {
                    crc = Force.Crc32.Crc32Algorithm.Append(crc, data, offset,
                        Math.Min(size, data.Length - offset));
                }

                return crc.ToString("X8");
            }
            case "equal":
                return FileSystem.AreFileContentsEqual(
                    Write(directory, "equal-a-" + index, Bytes(item.inputs[0])),
                    Write(directory, "equal-b-" + index, Bytes(item.inputs[1]))).ToString();
            default:
                throw new ArgumentException(item.op);
        }
    }
}
'''


def crc32(data):
    return f"{zlib.crc32(data) & 0xFFFFFFFF:08X}"


def pattern(length, step, offset):
    return bytes(((index * step + offset) & 0xFF) for index in range(length))


def spec(data):
    return {"hex": data.hex()}


def pattern_spec(length, step, offset):
    return {"pattern": {"length": length, "step": step, "offset": offset}}


def cases():
    result = []

    def add(op, expected, cross_check=None, **fields):
        result.append(dict(op=op, expected=expected, source="frozen-v10",
                           cross_check=cross_check or expected, **fields))

    small = [
        ("empty", b""),
        ("check-123456789", b"123456789"),
        ("one-byte", b"a"),
        ("three-bytes", b"abc"),
        ("pangram", b"The quick brown fox jumps over the lazy dog"),
        ("utf8", "\u00e9\U0001F600".encode()),
        ("all-byte-values", bytes(range(256))),
        ("multi-read-5000", pattern(5000, 17, 3)),
    ]
    for name, data in small:
        add("crc32-stream", crc32(data), input=spec(data))
        add("crc32-file", crc32(data), input=spec(data))
        add("md5-stream", hashlib.md5(data).hexdigest().upper(), input=spec(data))
        add("md5-file", hashlib.md5(data).hexdigest().upper(), input=spec(data))
    for name, data in small[1:3] + small[-1:]:
        add("sha256-file", hashlib.sha256(data).hexdigest().upper(), input=spec(data))

    large = pattern(1048576, 31, 7)
    add("crc32-stream", crc32(large), input=pattern_spec(1048576, 31, 7))
    add("crc32-file", crc32(large), input=pattern_spec(1048576, 31, 7))
    add("md5-file", hashlib.md5(large).hexdigest().upper(), input=pattern_spec(1048576, 31, 7))
    add("sha256-file", hashlib.sha256(large).hexdigest().upper(), input=pattern_spec(1048576, 31, 7))

    vector = b"123456789"
    add("crc32-append", crc32(vector), chunks=[spec(b"1234"), spec(b"56789")])
    add("crc32-append", crc32(vector), chunks=[spec(b""), spec(vector), spec(b"")])
    add("crc32-append", crc32(vector), chunks=[spec(bytes([value])) for value in vector])
    add("crc32-append", crc32(b""), chunks=[])
    add("crc32-append-split", crc32(large), input=pattern_spec(1048576, 31, 7), split=65536)

    add("equal", "True", inputs=[spec(b"identical"), spec(b"identical")])
    add("equal", "False", inputs=[spec(b"identical"), spec(b"identicaL")])
    add("equal", "False", inputs=[spec(b"short"), spec(b"longer")])
    add("equal", "True", inputs=[spec(b""), spec(b"")])

    # v10's own CheckSumTests pins these three values for its TestIni.ini resource.
    test_source = (SOURCE / FROZEN_TEST).read_text()
    ini = (SOURCE / FROZEN_TEST_FILE).read_bytes()
    pinned = {
        "md5": re.search(r'"([0-9a-fA-F]{32})", FileSystem\.GetMD5\(testFile\)', test_source).group(1).upper(),
        "sha256": re.search(r'"([0-9a-fA-F]{64})", FileSystem\.GetSHA256\(testFile\)', test_source).group(1).upper(),
        "crc32": re.search(r'"(D8B22F5D)", FileSystem\.GetCRC32\(testFile\)', test_source).group(1).upper(),
    }
    add("crc32-file", crc32(ini), input=spec(ini), v10_test_constant=pinned["crc32"])
    add("md5-file", hashlib.md5(ini).hexdigest().upper(), input=spec(ini),
        v10_test_constant=pinned["md5"])
    add("sha256-file", hashlib.sha256(ini).hexdigest().upper(), input=spec(ini),
        v10_test_constant=pinned["sha256"])

    add("crc32-stream", "!NullReferenceException", cross_check="exception:NullReferenceException",
        input=None)
    add("md5-stream", "!NullReferenceException", cross_check="exception:NullReferenceException",
        input=None)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    project = (SOURCE / FROZEN_PROJECT).read_text()
    pin = re.search(r'<PackageReference Include="Crc32\.NET" Version="([^"]+)" />', project)
    if not pin:
        raise SystemExit("Crc32.NET version pin not found in " + FROZEN_PROJECT)
    version = pin.group(1)
    with tempfile.TemporaryDirectory(prefix="checksum-oracle-", dir="/tmp/opencode") as temp:
        scratch = Path(temp)
        (scratch / "Oracle.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net11.0</TargetFramework><Nullable>disable</Nullable>'
            '<NoWarn>SYSLIB0021</NoWarn></PropertyGroup><ItemGroup>'
            f'<PackageReference Include="Crc32.NET" Version="{version}" />'
            f'<Compile Include="{SOURCE / FROZEN}" />'
            '</ItemGroup></Project>')
        (scratch / "Program.cs").write_text(PROGRAM)
        inputs = cases()
        (scratch / "inputs.json").write_text(json.dumps(inputs, ensure_ascii=False))
        subprocess.run(["dotnet", "run", "--project", str(scratch / "Oracle.csproj"), "--",
                        str(scratch / "inputs.json"), str(scratch / "results.json")], check=True)
        results = json.loads((scratch / "results.json").read_text())

    failures = []
    for item in results:
        check = item["cross_check"]
        expected = item["expected"]
        if check.startswith("exception:"):
            agrees = expected == "!" + check[len("exception:"):]
        else:
            agrees = expected == check
        if item.get("v10_test_constant") and expected != item["v10_test_constant"]:
            agrees = False
        if not agrees:
            failures.append(item)
    if failures:
        raise SystemExit(f"{len(failures)} frozen-v10 expectations disagree with the independent "
                         f"cross-check: {failures[:3]}")
    document = dict(
        provenance=dict(
            invocation="python3 scripts/port/generate_checksum_goldens.py --output /tmp/opencode/checksums-v10.json",
            runtime=subprocess.check_output(["dotnet", "--version"], text=True).strip(),
            rule="Frozen v10 FileSystem checksum bodies on .NET 11, with Crc32.NET pinned exactly as "
                 "legacy/source/Playnite/Playnite.csproj pins it. Each expectation is the value the "
                 "frozen source returned; the cross_check column is an independent CPython "
                 "zlib.crc32/hashlib value for the same bytes, and both must agree. Cases carrying "
                 "v10_test_constant also reproduce the constant v10's own CheckSumTests asserted.",
            sources={
                FROZEN: hashlib.sha256((SOURCE / FROZEN).read_bytes()).hexdigest(),
                FROZEN_PROJECT: hashlib.sha256((SOURCE / FROZEN_PROJECT).read_bytes()).hexdigest(),
                FROZEN_TEST: hashlib.sha256((SOURCE / FROZEN_TEST).read_bytes()).hexdigest(),
                FROZEN_TEST_FILE: hashlib.sha256((SOURCE / FROZEN_TEST_FILE).read_bytes()).hexdigest(),
            },
            crc32_package=f"Crc32.NET {version}",
            cross_check=dict(
                crc32="CPython zlib.crc32 (CRC-32/ISO-HDLC, reflected 0xEDB88320); the standard "
                      "check value for '123456789' is 0xCBF43926",
                md5="CPython hashlib.md5",
                sha256="CPython hashlib.sha256",
                equal="python bytes equality",
            )),
        cases=results)
    args.output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n")
    print(f"Wrote {len(results)} v10-derived checksum cases to {args.output}")


if __name__ == "__main__":
    main()
