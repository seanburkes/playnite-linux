# Frozen upstream snapshot

This directory is a verbatim snapshot of Playnite **10.64**, imported from
`https://github.com/JosefNemec/Playnite` at tag `10.64` (commit `f122c55b`).

It is the **behavioural oracle**, not a working tree:

- Do not edit, format, modernise, or delete anything here.
- Do not build it on Linux; it requires Windows + .NET Framework MSBuild.
- CI builds and tests it on a Windows runner to capture goldens.
- Any change to this directory fails the `legacy-guard` CI job.

Provenance commands:

```sh
git -C /path/to/upstream archive 10.64 | tar -x -C legacy
```

Upstream license: MIT, Copyright (c) 2020 Josef Nemec. See `LICENSE.md`.
