# Northpass 0.2

Independent Windows 10/11 x64 desktop application built with **C# / .NET 8 / WPF / MVVM**. Zapret2 is the initial external engine; it is not Northpass's own engine and this is not an official Zapret2 product.

The v0.1 ZIP is preserved as an original artifact. Its sources are extracted to the repository root and evolved into the v0.2 projects below. No third-party engine binaries are bundled and no provider-specific working strategy is claimed. The example is intentionally an empty draft.

## Implemented

- Graphite desktop UI with Dashboard, Strategies, Diagnostics, Settings and About; responsive page scrolling, custom icon and subtle button transitions.
- English, Russian and Azerbaijani main-page labels. Technical diagnostics and editor/dialog messages currently use English.
- Asynchronous `IDpiEngine` API with start, stop, restart, live process status and configuration validation. Engines are registered by factory at the composition root, rather than selected in WPF code.
- Local discovery under `engine/winws2.exe`, manual executable selection, required bundle-file checks, profile validation, exact option names and arity from a reviewed Zapret2 revision, Lua/list/blob file checks, documented `--version` and `--dry-run` preflight.
- Serialized process ownership; stdout/stderr logging, actual exit codes, prevention of duplicate starts, termination of the owned process tree and resource cleanup. Optional recovery is limited to three attempts per manually connected session.
- Local JSON strategy manager: add, edit, import, export, validate and persist selection. Atomic writes, duplicate-ID protection and malformed-file diagnostics. Empty drafts can be saved but cannot connect.
- Tray show/connect/disconnect/exit, optional minimize-to-tray, single-instance guard, session duration and explicit errors. Closing exits; minimizing hides only when configured.
- User-requested HTTPS reachability checks, bounded local diagnostic history and log export. An active engine process **does not prove DPI bypass or website accessibility**.
- Local settings, optional elevated Windows autostart task, and opt-in update metadata checks through GitHub. No telemetry, automatic binary downloads or automatic release execution.
- Release x64 publish script, Inno Setup installer definition, Linux portable tests, Windows WPF window smoke test and CI.

**Not implemented:** native C++ engine, automatic strategy selection, automatic strategy switching/search, automatic engine/update downloads, signed releases, or a privileged service separate from the UI. Lua is trusted executable code, not a sandbox. The starter's administrator manifest is retained for WinDivert; do not run untrusted bundles or imported Lua. See [Windows acceptance checks](docs/WINDOWS_ACCEPTANCE.md) before calling v0.2 release-ready.

## Project structure

```text
src/
  Northpass.App/                 WPF views, MVVM, tray and Windows integration
  Northpass.Core/                settings/profile/status models and profile schema validation
  Northpass.Engine.Abstractions/ IDpiEngine, engine registry and process supervisor
  Northpass.Engine.Zapret2/       Zapret2 validation, discovery and integration
  Northpass.Engine.Native/       reserved project, no placeholder engine registered
  Northpass.Services/            engine controller, bounded recovery, profiles, settings,
                                HTTPS diagnostics and update metadata
tests/
  Northpass.Tests/               portable xUnit tests
  Northpass.TestChild/           harmless real process fixture, not an engine
  Northpass.Windows.Tests/       real WPF window smoke test, Windows only
profiles/                       empty example profile
scripts/                        development, testing, publishing and cloud setup
installer/                      Inno Setup 6 definition
```

Open `Northpass.sln` in Visual Studio 2022 with .NET desktop development, or use the pinned .NET SDK **8.0.425** from the [official .NET 8 download](https://dotnet.microsoft.com/download/dotnet/8.0).

## Run on Windows

From the repository root in **PowerShell opened as Administrator** (`dotnet run` cannot request elevation itself):

```powershell
.\scripts\run-dev.ps1
```

Accept Windows elevation for Northpass. In Strategies, select the **official** `winws2.exe`; keep the complete bundle alongside it, including `cygwin1.dll`, `WinDivert.dll`, `WinDivert64.sys`, Lua and strategy assets. Official bundle: [bol-van/zapret-win-bundle](https://github.com/bol-van/zapret-win-bundle). Verify the official release's hashes/signatures when available; keep its licenses and attribution. Do not disable Windows security tools.

Profiles live in `%LOCALAPPDATA%\Northpass\profiles`, with settings in `%LOCALAPPDATA%\Northpass\settings.json`. Bundled templates are copied only when absent. Edit the draft or create/import a trusted strategy with **one `--option=value` per JSON array element**. Use `{ENGINE_DIR}` and `{PROFILE_DIR}` for referenced files. `--lua-init=@{ENGINE_DIR}/script.lua` is documented syntax; inline Lua, command-shell strings, config-file shortcuts, abbreviated options and process-detaching flags are rejected. Import/export covers JSON only; copy and verify referenced assets separately. Stop the current session before choosing another strategy.

The exact option schema was reviewed against [Zapret2 a1adf7b](https://github.com/bol-van/zapret2/tree/a1adf7b868e65c8a77aab608c8bbd9ec8b30a256), `nfq2/nfqws.c` and `docs/manual.en.md`. It is a compatibility boundary, not a promise that every bundle version or strategy works. Northpass also asks your actual executable to parse the arguments with `--dry-run` before interception. This cannot validate Lua behavior. Confirm a bundle/version-specific strategy using its official documentation and Windows tests.

## Tests and executable

```powershell
.\scripts\test.ps1
.\scripts\build.ps1
```

EXE: **`dist\Northpass\Northpass.exe`**, self-contained Windows x64. Keep the published folder and profiles together. The engine remains a separate trusted installation. Autostart requires this EXE at a stable location; `dotnet run` cannot enable autostart. Enabling autostart schedules the app for the current user's logon with elevation; it starts hidden and **does not connect automatically**. Disable it in Settings before moving the published application.

For an installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then:

```powershell
.\scripts\build.ps1 -Installer
```

Installer: `dist\installer\Northpass-0.2.0-win-x64-setup.exe`. It installs Northpass without Zapret2/WinDivert and preserves local user data on uninstall. Generated releases are unsigned until a maintainer supplies a signing workflow. CI uploads build artifacts without publishing a release.

## Linux cloud development

```bash
bash scripts/setup-cloud.sh
source /workspace/.northpass-tools/env.sh
dotnet test tests/Northpass.Tests/Northpass.Tests.csproj -c Release --no-restore
dotnet build Northpass.sln -c Release --no-restore
```

The cloud setup installs the pinned SDK with Microsoft's SHA-512 verified and restores the test dependency lockfiles. Linux supports portable tests and **Windows-target cross-compilation**. WPF execution, Task Scheduler, WinDivert and actual Windows engine tests require Windows. See [development details](docs/DEVELOPMENT.md), [architecture](docs/ARCHITECTURE.md), [acceptance checks](docs/WINDOWS_ACCEPTANCE.md), [changelog](CHANGELOG.md), and [third-party notices](THIRD_PARTY_NOTICES.md).
