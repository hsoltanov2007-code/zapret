# Northpass 0.5

Northpass is a Windows 10/11 x64 desktop application with a dark interface and automatic internal component preparation.

Download **Northpass-0.5.0-win-x64-setup.exe**, install, launch, then click **Connect**. This single installer includes the self-contained .NET application, reviewed network components, licences and corresponding third-party sources. No engine download, file selection or archive extraction is required. Windows may ask for administrator approval; Northpass never disables Windows security.

On first launch, Northpass verifies and prepares the included components. Later launches verify and reuse them. Home shows preparation and connection state without implementation details. Settings includes **Repair installation** and an optional **Show advanced tools** switch. Diagnostics retains detailed errors and logs; About provides full licences, notices and source-package access. EN/RU/AZ cover the consumer interface and custom confirmation dialogs.

**Connection started means the owned network process is running, not that blocked websites are accessible.** Home states that access is unverified. Diagnostics separates YouTube/Discord DNS, TCP, TLS and HTTPS outcomes. QUIC, STUN, media playback, login, gateway and voice remain untested. Advanced tools offer manual reviewed-strategy selection, data-only list imports and guided tests with local evidence; no automatic strategy swapping occurs. The included configuration is available automatically, but effectiveness varies by provider; automatic provider-specific strategy discovery is not implemented.

## Development and builds

Use .NET SDK 8.0.425. On Windows, install Python 3.10+ and Inno Setup 6.3+:

```powershell
# Administrator PowerShell for development/runtime integration checks
./scripts/test.ps1
./scripts/run-dev.ps1
./scripts/build.ps1 -Installer
./scripts/test-installer.ps1
```

The build produces `dist/Northpass/` and the single installer in `dist/installer/`. GitHub Actions uploads the installer separately as **Northpass-0.5.0-installer**, the application folder as **Northpass-win-x64**, and Windows results including EN/RU/AZ screenshots. GitHub wraps CI artifacts in ZIP containers; the installer artifact contains one EXE. Release signing and a public release download are separate publishing steps. CI does not publish or merge automatically.

For Linux cloud development, run `bash scripts/setup-cloud.sh`, activate `/workspace/.northpass-tools/env.sh`, and use the portable test suite. Linux cross-compilation cannot validate WPF, Windows elevation, drivers or the installer. See [development instructions](docs/DEVELOPMENT.md) and [Windows acceptance](docs/WINDOWS_ACCEPTANCE.md).

## Internal architecture and security

`IDpiEngine`, its registry and `IEngineInstallationManager` remain replaceable. New installs use **Flowseal/zapret-discord-youtube 1.10.3**, pinned at `865da4f4c3659523bf79bc6edf0446e7d7969614`, through an independent Zapret1 adapter (`winws.exe` v72.9). Five immutable typed strategies cover the reviewed YouTube, Discord and Discord voice filters; Northpass never executes upstream BAT files or service scripts. Valid existing selections/preferences survive upgrades. The previous Zapret2 adapter remains available in Advanced.

Both modules and their sources/notices are bundled. Build-time acquisition verifies pinned archives/components, statically compares typed definitions against reviewed strategy bytes, and generates deterministic payloads. The installed application requires these offline payloads; missing or modified bytes fail closed. See [Flowseal provenance and strategy review](docs/FLOWSEAL_REVIEW.md). The installer is one EXE; its protected installed application has normal runtime files and is not promised to be a literal single-file runtime.

Installed components live in protected Program Files, with verified hashes, owners and explicit ACLs, safe extraction and rejection of links/extra DLLs. Every launch/recovery validates configuration, acquires immutable-file leases, probes the actual version/parser and owns the child until termination. Updates/rollback remain limited to compiled reviewed manifests; the present catalog has one revision and therefore no invented update or rollback target. [Installation security](docs/ENGINE_INSTALLATION.md) describes the trust boundary and [architecture](docs/ARCHITECTURE.md) explains the replacement interfaces.

The whole app currently runs elevated. Artifacts are unsigned development builds. CI exercises real Windows rendering, component/version/parser checks, no-traffic driver initialization, silent installer execution and published bootstrap/reuse/failure/uninstall. It does not prove ISP bypass, normal-user UAC interaction, interactive tray/autostart behavior or clean consumer-machine compatibility. Installed engine revisions and user preferences remain on uninstall so shared loaded drivers and user data are not deleted.

Full third-party terms and copyright notices are retained in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), `docs/licenses/`, and About → Licenses. Offline distribution includes corresponding sources and exact self-contained runtime notices. The starter ZIP is preserved. No Northpass source licence has been invented; the owner must select one before a public source release.
