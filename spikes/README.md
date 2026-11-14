# Spikes

Time-boxed experiments that answer a Phase 1 question. They are not production code and do not
ship.

## `litedb-net11`

Question: can the unmaintained LiteDB 4.1.4 reader used by v10 run on `net11.0` on Linux?

Result (2026-11-14): **yes** for basic database create/open/insert/query/collection-name
enumeration. It restores and runs on `net11.0` with a critical-severity NuGet advisory, which
is exactly why the importer is a separate, sandboxed process and the main app never links it.

Still to verify with a real v10 fixture:

- v10's custom `BsonMapper` mappings and per-collection `.db` layout;
- the `ItemCollection` open/load/repair path;
- file locking and WAL behaviour under the importer's temp copy.

Run:

```sh
cd spikes/litedb-net11
dotnet run -c Release
```
