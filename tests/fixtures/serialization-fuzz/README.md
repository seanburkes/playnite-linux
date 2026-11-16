# Serialisation fuzz corpus

Deterministic seed corpus for `tools/Playnite.Fuzz` (the bounded linux-ci target) and the
in-process `tests/Playnite.Core.Tests/Serialization/SerializationFuzzTests.cs` regression
test. The target verifies `manifest.sha256` before mutating anything, so a seed edit is a
reviewable change rather than silent corpus drift.

## Provenance

| Seeds | Derived from |
| --- | --- |
| `json/01-golden.json` | The golden compact document in `JsonSerializationTests` (same object graph: nested objects, arrays, nulls, unicode, escaping) |
| `yaml/01-golden.yaml` | The golden document in `YamlSerializationTests` (folded scalar, nested mapping, sequence, explicit null) |
| `toml/01-golden.toml` | The golden document in `TomlSerializationTests` (tables, arrays of values, unicode, escaped newline) |
| `ini/01-v10-testini.ini` | Byte-for-byte copy of the frozen v10 fixture `legacy/source/Tests/Playnite.Tests/Resources/TestIni.ini`, which v10's own `IniParserTests.StandardTest` reads |
| `json/06-v10-settings.json` | Byte-for-byte copy of the v10 settings golden `tests/golden/settings/config.json` (CRLF preserved) |
| `yaml/06-v10-manifest.yaml` | Byte-for-byte copy of the v10 extension-manifest golden `tests/golden/manifests/TestPlugin.extension.yaml` (CRLF preserved) |
| `*/02-*`, `*/04-*` | Structural, unicode and escape variants of the golden shapes |
| `*/03-duplicate*` | Duplicate-key documents that must fail |
| `*/05-malformed*` and `*/03-malformed.ini` | Truncated or unclassifiable documents that must fail |

`manifest.sha256` records every seed's SHA-256. `SerializationFuzzTests` verifies the
manifest before mutating, so an edited or replaced seed is a deliberate, reviewable change
rather than silent corpus drift.

## Reproducing a failure

The fuzzer is a fixed-seed xorshift generator over these bytes (mutation count and PRNG seed
are constants in the test). A failure message names the seed file, the mutation index, and the
exact mutated input as base64, so the same case can be replayed without keeping CI state.
