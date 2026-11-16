# Additive adversarial v10 fixture

Derived from the existing Windows-produced `tests/fixtures/v10/library` (see its
parent README/provenance and SHA256SUMS), with **no changes to those source bytes**.
`Generate.cs` copies that library and updates one raw Game document with inert
`_type`/`$type` payloads, nested objects and an array. It uses LiteDB 4.1.4's
LiteEngine API, not BsonMapper. All four original game identities and five metadata
identities remain; expected Steam count is still exactly 3, non-Steam count 1.

Generation: compile Generate.cs in an external net11.0 console project with a direct
assembly reference to the pinned LiteDB 4.1.4 `lib/netstandard2.0/LiteDB.dll`, then
run it with the original library and a nonexistent output library path. SHA256SUMS
pins the checked-in bytes (LiteDB index layout need not reproduce byte-for-byte).
This is a Linux-mutated adversarial derivative, **not** independent Windows oracle
evidence. The original fixture remains the compatibility oracle.
