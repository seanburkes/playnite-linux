# p5-005a — Linux integration plumbing

Continuation of queue unit `p5-005` (XDG/single-instance/protocol). The base row stays
`todo`; this shard records the delivered slice without claiming the later Desktop wiring.

## Behaviour contract

`Playnite.Platform.Linux.Integration` supplies the pieces the host needs before it opens a
window. Contracts live in `Playnite.Platform.Integration` so `Desktop` and `Cli` depend on the
platform boundary rather than on the Linux assembly. Every facility resolves or validates its
inputs, returns a diagnostic on an environmental failure, and leaves startup alive.

### XDG directory precedence

`XdgDirectories.Resolve(IEnvironment)` reads captured values only. Resolution is lexical
normalization; it never creates a directory and never consults the host environment again.

| Directory | Override | Fallback when unset/empty/whitespace/relative/`~` | Application suffix |
| --- | --- | --- | --- |
| Config | `XDG_CONFIG_HOME` | `HOME/.config` | `playnite` |
| Data | `XDG_DATA_HOME` | `HOME/.local/share` | `playnite` |
| Cache | `XDG_CACHE_HOME` | `HOME/.cache` | `playnite` |
| State | `XDG_STATE_HOME` | `HOME/.local/state` | `playnite` |
| Runtime | `XDG_RUNTIME_DIR` | `State/runtime` | `playnite` for a usable override |

- A base directory is used only when it is an absolute path. Root `/` is valid and normalizes
  to `/playnite` after the suffix.
- `HOME` must be absolute; a relative value is rejected instead of consulting the passwd entry.
- The runtime fallback is `$XDG_STATE_HOME/playnite/runtime` (or the HOME fallback under State)
  and appends a diagnostic naming the rejected `XDG_RUNTIME_DIR`. It is private to the user but
  persistent; a usable `XDG_RUNTIME_DIR` produces `$XDG_RUNTIME_DIR/playnite`.
- `EnsureRuntimeDirectory()` is the only creation call. It is idempotent, creates the runtime
  tree and nothing else, and converts an unwritable/occupied path into a diagnostic string.

### Single instance

v10 used a named mutex plus a WCF named pipe (`CheckOtherInstances`, `PipeServer`). The Linux
port keeps the same shape with kernel-owned primitives:

| Concern | Linux behaviour |
| --- | --- |
| Ownership | `FileStream` on `<runtime>/<id>.lock` with `FileShare.None` (an advisory `flock`). |
| Second instance | `Start` returns `AlreadyRunning` with the lock path in the diagnostic; it never opens a window. |
| Crash release | The kernel drops the lock when the process exits or is SIGKILLed. The lock file may remain; it is inert and never deleted, avoiding the unlink/recreate race that would allow two owners. |
| Activation | Owner listens on `<runtime>/<id>.sock` (Unix domain socket, mode 0600). A later instance writes one validated `playnite://` line, waits for `OK`, and returns `Forwarded`. |
| No forwarding | A socket path over the Unix limit (or a bind failure) leaves ownership intact and returns a diagnostic; activation then reports `Unavailable`. |
| Runtime failure | An unusable runtime directory makes `Start` return `Unavailable`; the caller may run unguarded rather than crash. |
| Shutdown | Disposal removes the socket, leaves the lock file, closes the lock and completes the accept loop. |

Activation payloads are limited to 4096 bytes, must parse as a `playnite://` URI, and are
rejected before the owner sees them when malformed (bad percent escapes, control characters,
wrong scheme, line breaks). This prevents a second process from injecting a command or a raw
line into the owner. Instance ids are lowercase letters, digits, `.`, `_` and `-` (1–64
characters, no `.`/`..`), so they cannot escape the runtime directory.

### Protocol registration and URI validation

`PlayniteProtocolHandler` registers `x-scheme-handler/playnite` through freedesktop data, not
a child process:

- writes `<XDG_DATA_HOME>/applications/playnite-handler.desktop` with
  `Exec="<absolute executable>" --uridata %u` and `MimeType=x-scheme-handler/playnite;`;
- adds or replaces `x-scheme-handler/playnite=playnite-handler.desktop` in the
  `[Default Applications]` section of `<XDG_CONFIG_HOME>/mimeapps.list`, preserving every other
  section, key and comment;
- rewrites a file only when its content changes.

Executables are quoted with Desktop Entry escaping (`\\`, `\"`, `` \` ``, `\$`, `%%`), never
with shell quoting. Registration is a documented no-op (`Skipped`) inside Flatpak or Snap,
where the host desktop owns the association; an unwritable XDG directory or a non-absolute
executable returns `Failed` with the path included. Nothing throws at startup and no write
happens outside the two XDG directories.

`PlayniteUri.TryParse` accepts the v10 grammar with strict validation:

- the scheme is `playnite`, followed by `//`, then a source segment and optional
  `/<argument>` segments; the scheme is case-insensitive, the source is not
  percent-decoded and is limited to ASCII letters, digits, `.`, `_`, `~`, `-`, excluding `.`
  and `..`;
- at most 32 arguments, each at most 1024 characters, total at most 2048;
- empty path segments are ignored as in v10; `+` decodes to a space;
- percent escapes must be complete and decode as strict UTF-8; decoded control characters,
  raw whitespace, control characters, `?`, `#`, `\`, and non-`playnite` schemes are rejected;
- decoded values are returned as data. Shell metacharacters such as `$(...)` stay literal and
  are never executed.

### Degradation summary

| Facility | Failure | Result |
| --- | --- | --- |
| XDG base | relative/empty/tilde value | documented fallback plus diagnostic |
| Runtime | unusable directory | `Unavailable`, caller continues |
| Single instance | lock held | `AlreadyRunning`, clear diagnostic |
| Activation | path too long, bind/connect failure | `Unavailable`, ownership unaffected |
| Protocol | Flatpak/Snap | `Skipped`, no writes |
| Protocol | unwritable XDG / relative executable | `Failed`, no throw |

## Oracle and intentional differences

Read/adapted `legacy/source/Playnite/PlayniteUriHandler.cs` (`ParseUri`, source/argument split,
`+` as space), `legacy/source/Playnite/SystemIntegration.cs`
(`RegisterPlayniteUriProtocol`), `legacy/source/Playnite/App/PlayniteApplication.cs`
(`CheckOtherInstances`, `ProcessUriRequest`) and `legacy/source/Playnite/PipeServer.cs`.
Windows registry, named mutex/WCF pipe and `--masterinstance` are replaced by XDG files, a
kernel file lock and a Unix socket. Linux deliberately rejects malformed URIs that v10 would
pass to a handler, and registers the default handler without executing `xdg-mime`, so no shell
or child process is involved.

Deferred, with no reachable stubs: wiring the contracts into Desktop startup and command-line
dispatch (`--uridata`), plugin `RegisterSource`, and the process launcher that p5-004 owns.
Flatpak/Snap portal registration is host-owned and reported as `Skipped`.

## Verification

- `XdgDirectoriesTests`: precedence for all five roots, rejected values, root `/`, no implicit
  creation, runtime-only creation and idempotence, unusable runtime diagnostic, relative HOME.
- `SingleInstanceTests`: ownership, clear second-instance refusal, activation forwarding,
  malformed activation rejection, disposal leaving only the inert lock file, writes confined
  to the runtime directory, unusable runtime, overlong socket path, and SIGKILL crash release
  through `tools/Playnite.SingleInstance.Host` with zero stale-lock survivors.
- `PlayniteUriTests`: accepted grammar, percent/UTF-8 decoding, maximum lengths, 20 rejection
  classes and a literal-metacharacter proof.
- `ProtocolHandlerTests`: desktop entry and `mimeapps.list` content, preservation of unrelated
  entries, idempotent writes, Desktop Entry quoting, sandbox skip, unwritable directory.
- Zero skips, warning-as-error Release build, and the existing architecture suite unchanged.
