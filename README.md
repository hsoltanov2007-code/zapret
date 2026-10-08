# Northpass 0.3

Windows 10/11 **x64**, C# / .NET 8 / WPF / MVVM. Northpass is independent software; Zapret2 is its initial external engine. `IDpiEngine` remains the replacement boundary for a future native engine.

First launch requests Windows UAC elevation, then asks for engine-installation consent. After consent Northpass acquires a **pinned Git source archive** from the official [bol-van/zapret-win-bundle](https://github.com/bol-van/zapret-win-bundle), verifies its SHA-256 and all 18 selected components, and installs under `%ProgramFiles%\Northpass-Zapret2\<revision>`. Subsequent launches verify and reuse the installation. There is no engine file-picker step and no assumption that the bundle has GitHub Releases.

The Windows offline installer and published application folder include a verified selected-component payload, full notices and corresponding third-party sources. Offline setup makes no network request. Keep the entire published folder with the application. A damaged offline payload fails closed instead of switching silently to a download.

Connect verifies the protected installation again, validates the selected profile, runs the real `--version` / `--dry-run` probes and starts one owned `winws2.exe` child. Disconnect and exit stop the owned process tree and release file/installation locks. Actual logs, exit codes and bounded crash recovery are retained. **A running process or successful dry run does not establish successful DPI bypass.** Use Diagnostics to test an explicit HTTPS URL; the included HTTP/TLS example is not proven effective for your ISP.

The interface supports EN/RU/AZ, including setup progress and consent. Technical diagnostics and the JSON editor remain English. Existing profile editing/import/export, tray, session duration, settings and opt-in elevated logon task remain available. The entire app currently runs elevated; a separate privileged service and automatic strategy discovery are future work. Defender, Firewall, Secure Boot and UAC are never disabled or reconfigured.

## Trusted engine and updates

- Bundle Git revision: `6eb463a6758fb48cd101bc55dfd057e6e9d98af1`.
- Executable: Zapret2 `v1.0.5.2`, embedded source revision `6b6c63e3385fa73f8af3be4a69171e947f5a319d`.
- Reviewed archive/component hashes: [engine/catalog/zapret2.json](engine/catalog/zapret2.json), embedded in the application; editable downloaded metadata cannot authorize execution.
- Components: x64 executable, WinDivert DLL and signed x64 driver, Cygwin 3.4.10 runtime, six Lua files, filters and example data. Upstream command scripts and unrelated utilities are excluded.
- Updates are optional and limited to revisions included in a newer reviewed Northpass build. Check updates compares the installed engine with that trusted catalog; Northpass never installs arbitrary upstream HEAD/latest. A verified previous revision is retained, and Settings offers rollback. The initial v0.3 catalog has one revision, so there is no fabricated update or rollback target.
- See [installation security and pin review](docs/ENGINE_INSTALLATION.md) and [third-party notices](THIRD_PARTY_NOTICES.md).

## Windows development and artifacts

Install .NET SDK **8.0.425**, Python 3.10+ and, for the installer, [Inno Setup 6.3+](https://jrsoftware.org/isinfo.php). Open `Northpass.sln` in Visual Studio with .NET desktop development or use **Administrator PowerShell**:

```powershell
.\scripts\test.ps1
.\scripts\run-dev.ps1
.\scripts\build.ps1 -Installer
```

Tests acquire verified offline inputs first. Development startup can acquire the engine online after consent. Build creates `dist\Northpass\Northpass.exe` (self-contained), the offline payload and notices/sources, and `dist\installer\Northpass-0.3.0-win-x64-setup.exe`. Artifacts are unsigned; CI uploads builds without publishing or merging a release. Use the entire application folder, not an isolated EXE. The installer keeps protected engine revisions and user data on uninstall; it removes the current user's Northpass autostart task.

Profiles/settings live in `%LOCALAPPDATA%\Northpass`. Existing files are preserved. First-time users default to the bundled HTTP/TLS example; existing selections remain selected. Empty drafts cannot connect. Use one `--option=value` per JSON item and `{ENGINE_DIR}` / `{PROFILE_DIR}` paths. Managed Zapret2 permits Lua only inside its verified installation; inline/external Lua, shell/config shortcuts, unknown/abbreviated options and detached lifecycle flags are rejected. Imported JSON does not copy assets. Review strategies before using them.

## Cloud development

```bash
bash scripts/setup-cloud.sh
source /workspace/.northpass-tools/env.sh
python3 scripts/prepare-engine.py
dotnet test tests/Northpass.Tests/Northpass.Tests.csproj -c Release --no-restore
dotnet build Northpass.sln -c Release --no-restore
```

Linux runs portable tests and Windows-target compilation. WPF, Windows ACL enforcement, real PE execution, Task Scheduler, driver initialization and installer execution require Windows. CI has separate Linux and Windows jobs, actual Windows installation/runtime checks, and app/installer compilation. See [development](docs/DEVELOPMENT.md), [architecture](docs/ARCHITECTURE.md), and [Windows acceptance](docs/WINDOWS_ACCEPTANCE.md). The original v0.1 ZIP remains preserved in the repository.
