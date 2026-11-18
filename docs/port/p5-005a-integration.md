# p5-005a — Linux integration plumbing

Continuation of queue unit `p5-005` (XDG/single-instance/protocol). The base row stays
`todo`; this shard records the delivered slice without claiming the later Desktop wiring.

## Behaviour contract

`Playnite.Platform.Linux.Integration` supplies the pieces the host needs before it opens a
window. The `ISingleInstance` and `IProtocolHandler` contracts live in
`Playnite.Platform.Integration` so consumers depend on the platform boundary; the Linux
composition supplies `XdgDirectories`, `SingleInstanceService`, `PlayniteProtocolHandler` and
`PlayniteUri`. Every facility resolves or validates its inputs, returns a diagnostic on an
environmental failure, and leaves startup alive.

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
  to `/playnite` after the suffix. Empty, whitespace, relative and `~`-prefixed values fall
  back and are recorded in `Diagnostics`.
- A non-absolute `HOME` does not throw at startup: resolution falls back to the captured
  absolute current directory (or `/`) and records a diagnostic naming both values.
- `LinuxPaths` and `XdgDirectories` share the same base-directory rule; `XdgDirectories` is the
  runtime leaf and adds only `XDG_RUNTIME_DIR`.
- The runtime fallback is `$XDG_STATE_HOME/playnite/runtime` (or the HOME fallback under State)
  and appends a diagnostic naming the rejected `XDG_RUNTIME_DIR`. A usable `XDG_RUNTIME_DIR`
  produces `$XDG_RUNTIME_DIR/playnite`.
- `EnsureRuntimeDirectory()` is the only creation call. It is idempotent, creates the runtime
  tree and nothing else, and converts an unwritable/occupied path into a diagnostic string. A
  new or existing runtime directory is tightened to `0700`; failure to make it private is
  reported and the caller continues unguarded.

### Single instance

v10 used a named mutex plus a WCF named pipe (`CheckOtherInstances`, `PipeServer`). The Linux
port keeps the same shape with kernel-owned primitives:

| Concern | Linux behaviour |
| --- | --- |
| Ownership | `FileStream` on `<runtime>/<id>.lock` with `FileShare.None` (an advisory `flock`). The lock file is opened with `OpenOrCreate` (never truncated), its owner record is overwritten in place, and its mode is tightened to 0600. |
| Second instance | `Start` returns `AlreadyRunning` with the lock path only for real lock contention; other I/O failures return `Unavailable` with the underlying error. It never opens a window. |
| Crash release | The kernel drops the lock when the process exits or is SIGKILLed. The lock file may remain; it is inert and never deleted, avoiding the unlink/recreate race that would allow two owners. |
| Activation | Owner listens on `<runtime>/<id>.sock` (Unix domain socket). Permissions are set to 0600 after bind but before `Listen`, so no client can connect through a permissive inode. A later instance writes one validated `playnite://` line, waits for `OK`, and returns `Forwarded`. |
| Subscriber timing | An activation that arrives before the first `Activated` subscriber is queued (up to 16) and delivered on subscription; the sender is not told `Forwarded` for an activation that nobody can receive. |
| No forwarding | A socket path over the Unix limit (or a bind failure) leaves ownership intact and returns a diagnostic; activation then reports `Unavailable`. |
| Runtime failure | An unusable runtime directory makes `Start` return `Unavailable`; the caller may run unguarded rather than crash. |
| Stalled clients | Each accepted connection runs independently with a 5-second read/write bound, so one client that never sends a newline cannot block later activations. |
| Shutdown | Only the process that bound the socket removes it. Disposing a refused second instance leaves the live owner's socket untouched. The lock file stays inert. |

Activation payloads are limited to 4095 UTF-8 bytes plus the newline, must parse as a
`playnite://` URI, and are rejected before the owner sees them when malformed (bad percent
escapes, control characters, wrong scheme, line breaks, an oversized multi-byte value). This
prevents a second process from injecting a command or a raw line into the owner. Instance ids
are lowercase letters, digits, `.`, `_` and `-` (1–64 characters, no `.`/`..`), so they cannot
escape the runtime directory.

### Protocol registration and URI validation

`PlayniteProtocolHandler` registers `x-scheme-handler/playnite` through freedesktop data, not
a child process:

- writes `<XDG_DATA_HOME>/applications/playnite-handler.desktop` with
  `Exec="<absolute executable>" --uridata %u` and `MimeType=x-scheme-handler/playnite;`;
- adds or replaces `x-scheme-handler/playnite=playnite-handler.desktop` in the
  `[Default Applications]` section of `<XDG_CONFIG_HOME>/mimeapps.list`, preserving every other
  section, key and comment and the file's CRLF or LF line-ending style;
- writes each file through a same-directory temporary file and an atomic rename; a failed
  `mimeapps.list` update rolls back a desktop entry this call created;
- rewrites a file only when its content changes.

Executables are quoted with Desktop Entry escaping (`\\`, `\"`, `` \` ``, `\$`, `%%`), never
with shell quoting; a path containing control characters is rejected before any write.
Registration is a documented no-op (`Skipped`) inside Flatpak or Snap, where the host desktop
owns the association; an unwritable XDG directory or a non-absolute executable returns `Failed`
with the path included. Nothing throws at startup and no write happens outside the two XDG
directories.

`PlayniteUri.TryParse` accepts the v10 grammar with strict validation:

- the scheme is `playnite`, followed by `//`, then a source segment and optional
  `/<argument>` segments; the scheme is case-insensitive, the source is not
  percent-decoded and is limited to ASCII letters, digits, `.`, `_`, `~`, `-`, excluding `.`
  and `..`;
- at most 32 arguments, each at most 1024 characters, total at most 2048;
- empty path segments are ignored as in v10; `+` decodes to a space;
- percent escapes must be complete and decode as strict UTF-8; raw surrogate pairs decode as
  their single scalar value, while unpaired surrogates and malformed byte sequences are
  rejected. Decoded C0/C1 control characters, raw whitespace, `?`, `#`, `\`, and non-`playnite`
  schemes are rejected;
- decoded values are returned as data. Shell metacharacters such as `$(...)` stay literal and
  are never executed.

### Degradation summary

| Facility | Failure | Result |
| --- | --- | --- |
| XDG base | relative/empty/tilde value | documented fallback plus diagnostic |
| XDG home | non-absolute HOME | current-directory fallback plus diagnostic |
| Runtime | unusable directory | `Unavailable`, caller continues |
| Single instance | lock held | `AlreadyRunning`, clear diagnostic |
| Single instance | non-contention I/O failure | `Unavailable` with the underlying error |
| Activation | path too long, bind/connect failure, oversized payload | `Unavailable`, ownership unaffected |
| Activation | no subscriber yet | queued and delivered on subscription |
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

- `XdgDirectoriesTests`: precedence for all five roots, rejected values and diagnostics, root
  `/`, non-absolute HOME degradation, parity with `LinuxPaths`, no implicit creation,
  runtime-only creation, idempotence, 0700 tightening and unusable-runtime diagnostic.
- `SingleInstanceTests`: ownership, clear second-instance refusal, cross-process contention,
  activation forwarding, queued delivery before subscription, malformed and oversized payload
  rejection, non-owner disposal preserving the owner socket, stalled-client concurrency,
  cancellation, private runtime/lock/socket modes, non-truncating lock open, non-contention
  lock failure, writes confined to the runtime directory, unusable runtime, overlong socket
  path, and SIGKILL crash release through `tools/Playnite.SingleInstance.Host`.
- `PlayniteUriTests`: accepted grammar, percent/UTF-8 decoding including raw and encoded
  non-BMP scalars, unpaired surrogates, C0/C1 controls, maximum lengths, 20 rejection classes
  and a literal-metacharacter proof.
- `ProtocolHandlerTests`: desktop entry and `mimeapps.list` content, preservation of unrelated
  entries and CRLF style, space-padded key replacement, atomic replacement and rollback,
  production filesystem confined to the XDG roots, idempotent writes, Desktop Entry quoting,
  control-character rejection, sandbox skip, unwritable directory.
- Zero skips, warning-as-error Release build, and the existing architecture suite unchanged.
