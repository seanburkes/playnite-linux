# Steam fixtures

`basic/` is a synthetic Steam installation:

- two library folders (`basic` and `second-library`);
- `appmanifest_220.acf` (Half-Life 2) and `appmanifest_570.acf` (Dota 2);
- `appmanifest_400.acf` (Portal) in the second library, with a Proton prefix at
  `basic/steamapps/compatdata/400/pfx`;
- install directories exist under `steamapps/common/`.

`libraryfolders.vdf` uses `${FIXTURE_ROOT}` instead of an absolute host path. Tests copy the
fixture to a temp directory and substitute that token, so the fixture is portable and does not
depend on `/home/test`.

`broken/` contains an unbalanced VDF and an app manifest whose install directory is missing.
Use it for failure-path tests; do not "fix" it.

No real Steam data is checked in. A redacted real corpus lives outside the repo / in a
private artifact; it is not committed.
