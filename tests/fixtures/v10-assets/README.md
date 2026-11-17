# Additive v10 asset fixture

`files/` contains byte-for-byte copies of the two frozen v10 theme PNGs named in
`assets.json`, including SHA-256, size and PNG IHDR dimensions. The original
filenames are retained. No legacy files or pre-existing fixtures were changed.

`references.json` is a synthetic supplement to the Windows-captured `../v10`
library's first Steam game. Tests apply these three fields to its reader JSON;
this is not a claim that a new LiteDB fixture was captured on Windows.
The path spelling and `files/<parent-GUID>/<filename>` layout derive from
`legacy/source/Playnite/Database/GameDatabase.cs`, `GetFullFilePath` and `AddFile`
(lines 796–798, 833–935). The third reference deliberately has no file.

These additive protected-path changes require the `oracle-change` PR label.
