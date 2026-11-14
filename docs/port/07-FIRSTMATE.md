# Firstmate setup

Firstmate is cloned at `~/src/firstmate` (shallow, MIT). The `playnite-linux` project is
registered as `local-only` because the GitHub repository has not been created yet.

## What was configured

```text
~/src/firstmate/
  data/projects.md        # registry line for playnite-linux
  data/captain.md         # project preferences and escalation rules
  projects/playnite-linux -> /home/sean/src/playnite-linux
```

Registry query:

```sh
cd ~/src/firstmate
FM_HOME=$PWD bin/fm-project-mode.sh playnite-linux
# local-only off
```

The canonical repo remains at `~/src/playnite-linux`. The `projects/` entry is a symlink so
firstmate can resolve and create worktrees from it. If firstmate or treehouse refuses a
symlinked project, use `FM_PROJECTS_OVERRIDE=/home/sean/src` and keep the registry entry.

## Launch

```sh
cd ~/src/firstmate
FM_HOME=$PWD FM_PROJECTS_OVERRIDE=/home/sean/src opencode
```

Launch it **inside Herdr** if you want the Herdr backend:

```sh
# Herdr server must be running; protocol 14+ required (installed client is 0.9.5/protocol 22).
herdr
# then launch firstmate in a Herdr pane; HERDR_ENV=1 is auto-detected.
```

If Herdr is not running, firstmate uses tmux (installed, 3.8). Set the backend explicitly:

```sh
printf 'tmux\n' > ~/src/firstmate/config/backend
```

Current environment fact: the Herdr **server is not running** (`herdr status` shows only the
client). However, firstmate's bootstrap auto-detected a Herdr-app-projected terminal
(`default:t14` carries a `herdr.space` label) and would spawn into the experimental `hc`
backend with Herdr app presentation. That is a firstmate environment fact, not a port
decision. Pin the backend explicitly if you want reproducible behaviour:

```sh
printf 'tmux\n' > ~/src/firstmate/config/backend   # or 'herdr' once its server is running
```

## Missing tools

Bootstrap detect-only currently reports:

```text
MISSING: treehouse
MISSING: no-mistakes
MISSING: gh-axi
MISSING: chrome-devtools-axi
MISSING: tasks-axi
MISSING: quota-axi
PRESENTATION_UNAVAILABLE: lavish-axi
```

`gh` is authenticated as `seanburkes`. Install only after the captain approves; the install
commands are the ones bootstrap prints. `treehouse` is needed for the default worktree
backend; `tasks-axi` is the default backlog backend; `no-mistakes` is only needed if the
project moves to the `no-mistakes` posture. v1 can start with `local-only` + tmux and add tools
as needed.

## When the GitHub repo exists

1. Create `seanburkes/playnite-linux` (public, MIT) and push `main`.
2. Add `origin` to `~/src/playnite-linux`.
3. Change the registry line to `[direct-PR]` (or `[no-mistakes-prod-only]` if the pipeline is
   installed). Do not add `+yolo`; the captain keeps merge authority.
4. Re-run the registry query to confirm.

## Dispatch rules for this project

- One unit per worktree; branch `port/<phase>-<unit>`.
- Workers run only the narrow check named in the task; the parent builds.
- Two blind reviewers per unit, diff only; a fixer applies findings.
- `legacy/`, `tests/golden/`, `tests/fixtures/`, `tests/baseline.json`, and `docs/formal/`
  are protected. An `oracle-change` task is separate and needs a second reviewer.
- At two failed attempts, stop and escalate; do not thrash.
- Check in at every phase gate with a `docs/port/gates/P<phase>-*.md` artifact.
