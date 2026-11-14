# Dependency matrix

Versions are the current stable lines observed on 2026-11-13. Pin exact versions in
`Directory.Packages.props`; the table is the decision, the props file is the lock.

## 1. Managed packages

| v10 package | v10 | Why it exists | v11 choice | Version | Risk / notes |
| --- | --- | --- | --- | --- | --- |
| Avalonia | — | UI | **Avalonia** | 12.1.6 | Core UI; Skia renderer |
| Avalonia.Desktop | — | App host | **Avalonia.Desktop** | 12.1.6 | |
| Avalonia.Themes.Fluent | — | Base theme | **Avalonia.Themes.Fluent** | 12.1.6 | We layer a functional default theme on it |
| Avalonia.Controls.DataGrid | — | Grid view | **Avalonia.Controls.DataGrid** | 12.1.x | Official package |
| Avalonia.Controls.WebView | — | Embedded browser | **Avalonia.Controls.WebView** | 12.1.2 | Official; WebKitGTK/WPE on Linux |
| Avalonia.HtmlRenderer | — | Description HTML | **Avalonia.HtmlRenderer** | 12.0.0 | Official; use for non-interactive HTML |
| Avalonia.Headless.XUnit | — | UI tests | **Avalonia.Headless.XUnit** | 12.1.6 | Headless render + input |
| Prism.Core / Prism.Wpf | 6.3.0 | Referenced but **unused** in code | **Remove** | — | No Prism references found |
| CommonServiceLocator | 1.3 | Unused | **Remove** | — | |
| Expression.Blend.Sdk | 1.0.2 | WPF behaviours | **Remove** | — | Avalonia.Xaml.Behaviors instead |
| Microsoft.Xaml.Behaviors.Wpf | 1.1.39 | WPF behaviours | **Avalonia.Xaml.Behaviors** | 11.3.0.6 | Verify Avalonia 12 compatibility before pinning |
| Hardcodet.NotifyIcon.Wpf | 1.0.8 | Tray icon | **Avalonia TrayIcon** | in-box | Linux StatusNotifierItem/AppIndicator |
| VirtualizingWrapPanel | 2.5.4 | Grid virtualization | **ItemsRepeater + UniformGridLayout** | in-box | Or a maintained Avalonia virtualizing wrap panel; benchmark first |
| MathConverter | 2.2.1 | XAML math | **Remove / local converter** | — | |
| LargeAddressAware | 1.0.6 | x86 address space | **Remove** | — | x64/ARM64 only |
| CefSharp.OffScreen / Wpf | 152.0.100 | Chromium | **Avalonia.Controls.WebView** | 12.1.2 | WebKitGTK baseline; WPE optional; no CEF |
| AngleSharp | 0.9.9 | HTML parsing | **AngleSharp** | 1.10.3 | Upgrade; API changes |
| HtmlRenderer / HtmlRenderer.WPF | via Deps | HTML render | **Avalonia.HtmlRenderer** | 12.0.0 | WPF version cannot run |
| LiteDB | 4.1.4 | Library storage | **Microsoft.Data.Sqlite + Dapper** | 11.0.0 / 2.1.89 | One-way migration; LiteDB 4 only in importer |
| SQLNado | via Deps | SQLite ORM | **Microsoft.Data.Sqlite + Dapper** | 11.0.0 / 2.1.89 | Replace; no need for a second ORM |
| SharpCompress | via Deps | Archives | **SharpCompress** | 1.0.0 | Check API changes; netstandard |
| DiscordRPC | via Deps | Rich presence | **DiscordRichPresence** | 1.143.0 | Or a maintained DiscordRPC fork |
| Newtonsoft.Json | 10.0.3 | v10 data/settings | **Newtonsoft.Json** | 13.0.4 | Keep for v10 compatibility; known CVE fixed |
| System.Text.Json | — | New internal JSON | **System.Text.Json** | in-box | CLI/internal only |
| YamlDotNet | 5.4.0 | Manifests | **YamlDotNet** | 18.1.0 | API changes; golden tests |
| Nett | 0.10.1 | TOML | **Tomlyn** | 2.11.1 | Or keep Nett if netstandard; Tomlyn is maintained |
| NLog | 4.7.6 | Logging | **NLog** | 6.2.2 | Keep log format; config changes |
| Flurl | 2.7.1 | HTTP | **HttpClient / Microsoft.Extensions.Http** | 11.0.0 | Remove Flurl; use a typed client |
| Polly | 5.1.0 | Retry | **Polly** | 8.9.1 | Resilience pipelines; API rewrite |
| protobuf-net | 2.0.0.668 | Serialization | **protobuf-net** | 3.4.40 | Check whether still used; likely drop |
| NSec.Cryptography | 20.2.0 | Ed25519 signatures | **NSec.Cryptography** | 26.4.0 | Cross-platform libsodium; updater only |
| CommandLineParser | 2.8.0 | CLI | **System.CommandLine** or keep | 2.9.1 / 2.0 | Choose one; no shell parsing |
| Crc32.NET | 1.2.0 | CRC | **System.IO.Hashing** | in-box | `Crc32` |
| DynamicLanguageRuntime | 1.2.1 | Unused? | **Remove** | — | Verify no `Microsoft.Scripting` use |
| System.Buffers / Memory / Numerics.Vectors / Unsafe / ValueTuple / Tasks.Extensions / RuntimeInformation | various | Framework backports | **Remove** | — | In-box on net11 |
| System.IO.Abstractions | 2.1.0.227 | Testable FS | **System.IO.Abstractions** | 23.0.0 | Upgrade, or use temp dirs; API changes |
| System.IO.Abstractions.TestingHelpers | 2.1.0.227 | Test FS | **same** | 23.0.0 | |
| WindowsAPICodePack | 1.1.0 | Shell/Win32 | **Remove** | — | Platform.Linux replacement |
| Playnite.Deps | 10.56.0 | Binary bundle | **Remove** | — | Replace each member with a source package |
| Moq | 4.21.0 | Tests | **NSubstitute** or Moq | 5.x / 4.21 | Moq 4.20+ sponsor/telemetry concerns; choose NSubstitute |
| NUnit | 3.13.2 | Tests | **xUnit v3** or NUnit 4 | 4.0.3 / 4.6.1 | New tests: xUnit v3; legacy reuse: NUnit 4 |
| NUnit3TestAdapter | 3.17.0 | Runner | **NUnit3TestAdapter** | latest | |
| Castle.Core | 5.2.1 | Moq dependency | with NSubstitute path, remove | — | |
| Microsoft.NET.Test.Sdk | — | Runner | **Microsoft.NET.Test.Sdk** | 18.12.0 | |
| CsCheck | — | Property tests | **CsCheck** | 5.0.1 | |
| Stryker.NET | — | Mutation | **dotnet-stryker** | 5.1.0 | Nightly |
| NetArchTest / ArchUnitNET | — | Architecture | **NetArchTest.Rules** | 1.3.2 | Or ArchUnitNET 0.13.5 |
| BannedApiAnalyzers | — | API bans | **Microsoft.CodeAnalysis.BannedApiAnalyzers** | latest | |
| PublicApiAnalyzers | — | SDK surface | **Microsoft.CodeAnalysis.PublicApiAnalyzers** | latest | |
| ApiCompat | — | v6→v7 report | **Microsoft.DotNet.ApiCompat.Tool** | latest | CI report |
| Microsoft.PowerShell.SDK | GAC 5.1 | Script plugins | **Microsoft.PowerShell.SDK** | 7.7.0 | Cross-platform `pwsh`; not v1 required |
| Tmds.DBus | — | Notifications/tray | **Tmds.DBus.Protocol** | latest | If not using Avalonia tray/notify |
| Tmds.DBus.Protocol | — | D-Bus | latest | | |

## 2. Removed without replacement

| Package | Reason |
| --- | --- |
| Prism.Core / Prism.Wpf | No code references; no region/navigation usage |
| CommonServiceLocator | Unused |
| Expression.Blend.Sdk | WPF-only behaviours |
| Hardcodet.NotifyIcon.Wpf | Avalonia tray |
| LargeAddressAware | 64-bit only |
| MathConverter | Trivial local converter |
| WindowsAPICodePack | Linux platform services |
| Playnite.Deps | Split into source packages |
| CefSharp.* | Avalonia WebView |
| Magick.NET-Q8-x86 | Replace with AnyCPU |
| Magick.NET.SystemWindowsMedia | Windows-only codec path |
| PhotoSauce.MagicScaler | WIC/Windows-only |
| System.Management | Linux has `/proc`, `lsblk`, D-Bus; no WMI |
| System.ServiceProcess | No Windows services |
| System.Web | Use `HttpClient`/`System.Net` |
| `Microsoft.Win32.Registry` | XDG config/secret store |

## 3. Native/runtime dependencies

| Need | Linux choice | Packaging | ARM64 | Notes |
| --- | --- | --- | --- | --- |
| Rendering | Skia (Avalonia) | bundled by Avalonia | yes | |
| Web view | WebKitGTK 4.1 + libsoup 3; WPE optional | Flatpak runtime/bundled | distro-dependent | Ubuntu no WPE; WebKitGTK needs XWayland for embedded |
| SQLite | `SQLitePCLRaw` bundled by Microsoft.Data.Sqlite | bundled | yes | Use bundled `e_sqlite3` |
| Images | Magick.NET-Q8-AnyCPU | bundled native libs | verify | Covers/convert; no WIC |
| Gamepad | SDL2 2.x | system or bundled | yes | `Silk.NET.SDL` or `SDL2-CS` |
| Notifications | D-Bus `org.freedesktop.Notifications` | system | yes | Fallback `notify-send` |
| Tray | StatusNotifierItem/AppIndicator | system extension | yes | GNOME needs an extension; document |
| Secret store | libsecret / `secret-tool` | system | yes | Optional; fall back to 0600 file |
| Sleep inhibit | logind / `systemd-inhibit` | system | yes | |
| Protocol | `.desktop` + `xdg-mime` | packaged | yes | Flatpak portal may differ |
| Audio (later) | PipeWire/PulseAudio | system | yes | Not v1 |

## 4. Licensing and supply chain

- All proposed replacements are MIT/Apache-2.0/BSD-compatible. Confirm each package's license
  in CI (`dotnet-project-licenses` or a NuGet license scan) and fail on a denylist.
- `Playnite.Deps` bundled binaries must be replaced by source packages or rebuilt from their
  upstream repositories. Do not copy the bundled DLLs into the new app.
- Generate an SBOM (CycloneDX) per release.
- Pin package versions centrally; no floating versions.
- Enable NuGet lock files (`RestorePackagesWithLockFile`) for reproducible restores.
- Audit transitive packages; the v10 tree has known vulnerabilities (LiteDB 4 critical,
  Newtonsoft 10 high, AngleSharp 0.9 moderate). The new tree starts clean.

## 5. Central package management

`Directory.Packages.props`:
- `ManagePackageVersionsCentrally=true`.
- One `PackageVersion` per package, exact.
- `Directory.Build.props` sets `net11.0`, nullable **disabled initially** (enabling nullable
  across 143k lines is a separate project), `TreatWarningsAsErrors=true`,
  `AnalysisLevel=latest`, `EnforceCodeStyleInBuild=true`, deterministic builds.
- `RestorePackagesWithLockFile=true`; commit `packages.lock.json`.
- `NuGetAudit=true`, `NuGetAuditMode=all`.

## 6. Version policy

- Patch updates: monthly, automated PR, CI verifies.
- Minor updates: per dependency, with a contract test for behaviour changes.
- Major updates (Avalonia, SQLite, WebKit): ADR + pilot + full regression.
- A dependency replacement is a **second migration**: budget a contract test against recorded
  traffic/fixtures for each replaced package, per the doctrine. Examples: DiscordRPC presence
  payloads, SharpCompress entry ordering, Newtonsoft property-name resolution, YamlDotNet
  anchor/alias behaviour.
