# Northpass 0.7

Northpass is a Windows 10/11 x64 desktop application with a premium black interface, reviewed offline components and EN/RU/AZ localization.

Install **Northpass-0.7.0-win-x64-setup.exe**, launch, choose an included strategy on Home, and click **Connect**. This one installer includes the self-contained .NET runtime, network module, licences and corresponding third-party sources. No component download or file selection is needed. Windows may request administrator approval; Northpass never disables Windows security.

A branded splash presents real preparation progress, followed by a short fade into the main window; cached startup has no minimum display time and never waits for web checks or optional update metadata. System animation preferences are respected. Home shows status, the selected strategy, Connect / Disconnect and a quick view of YouTube, Discord and Telegram web access. Built-in DNS, TCP, authenticated TLS and HTTPS checks run automatically after initial preparation and connection changes; **Check again** refreshes them. They do not block Connect. Changing a strategy requires disconnection and clears old diagnostic results. Detailed stage outcomes, addresses and logs are available in Diagnostics → Details & local logs. No URL entry is required.

**Available means the checked website passed all four stages, not that video, calls, messaging or a DPI bypass works.** Limited means partial web access; Unavailable means DNS/TCP failed; Unknown means no current evidence. A running process alone never changes a service card to Available. Telegram checks its web endpoint, not its native client protocol. Other IP families, media/CDN, login/gateway, QUIC/STUN and voice still require real-world testing.

New installations open in Russian. Saved EN/RU/AZ preferences are preserved; the vector flag/native-name selector in Settings saves language changes immediately. No advanced-tools toggle is shown. Legacy saved opt-ins no longer expose advanced pages. About focuses on Northpass, support and optional application updates; full attribution, licences and source access are separate under **Licenses & legal**. The original route logo is in `branding/` and appears in the title, About, app/tray and installer icons.

## Production engine and native preview

v0.6 removes the old Zapret2 adapter, catalog, bundled profiles, installer payload, executable picker and engine switching. The pinned Flowseal-derived Zapret1 module remains the consumer default/fallback. Five reviewed typed strategies preserve argument ordering, repeated options and YouTube/Discord/voice filters without running BAT files or upstream service scripts. The protected install, integrity verification, safe extraction, data-list snapshots, repair/rollback and owned-child lifecycle remain intact. `IDpiEngine` and installer/data interfaces remain replaceable internally.

**Northpass Native Engine v0.4** adds original Northpass Split: strictly validated TCP/ClientHello segment proposals, immutable originals, pre-send rollback and a hard-deadline loopback TLS laboratory with actual packet traces and a toy DPI simulator. Experimental injection cannot be enabled through the UI or privileged broker; normal Native behavior remains pass-through and Flowseal remains the consumer engine. Simulator and local TLS success are **not evidence of ISP bypass**. The offline installer preserves verification, licences and corresponding sources. Read [Split architecture and laboratory](docs/NATIVE_SPLIT.md), [broker security](docs/BROKER_SECURITY.md) and [unexecuted manual desktop/UAC procedures](docs/NATIVE_V04_MANUAL.md).

Valid Flowseal selections/preferences survive upgrades. Unsupported old selections migrate to the included starter while existing user files are retained. The installer removes only known legacy product files from an in-place app upgrade; it does not terminate external programs or delete protected shared drivers. Read [v0.7 UI/startup behavior](docs/UI_V0.7.md), [v0.6 behavior and migration](docs/PRODUCT_V0.6.md), [reviewed input](docs/FLOWSEAL_REVIEW.md), [architecture](docs/ARCHITECTURE.md) and [security](docs/ENGINE_INSTALLATION.md).

## Development and builds

Use .NET SDK 8.0.425, Visual Studio 2022 C++ tools/Windows SDK, CMake 3.24+, Python 3.10+ and Inno Setup 6.3+ on Windows:

```powershell
# Administrator PowerShell for actual Windows integration
./scripts/test.ps1
./scripts/run-dev.ps1
./scripts/build.ps1 -Installer
./scripts/test-installer.ps1
```

Output: `dist/Northpass/` and `dist/installer/Northpass-0.7.0-win-x64-setup.exe`. GitHub Actions uploads the app, **Northpass-0.7.0-installer**, and Windows evidence/EN/RU/AZ Home/About/Settings/Splash screenshots. GitHub wraps artifacts in ZIPs; the installer artifact contains one EXE. Installed runtime files are ordinary protected files, not a promised single-file runtime. CI does not merge or publish releases automatically.

Linux cloud development: `bash scripts/setup-cloud.sh`, `source /workspace/.northpass-tools/env.sh`, then run portable tests and cross-compile. Linux cannot run WPF, Windows drivers, ACL/elevation or installer acceptance. See [development instructions](docs/DEVELOPMENT.md).

The whole application currently runs elevated; development artifacts are unsigned unless release signing is explicitly configured. Website/controller outcomes in unit/UI tests are labelled fixtures. Windows integration runs the real reviewed PE for version/parser and a no-traffic driver session, and actually executes the installer and published bootstrap/uninstaller. These do not certify Russian ISP bypass or clean consumer-machine/UAC compatibility. Follow [Windows/ISP acceptance](docs/WINDOWS_ACCEPTANCE.md).

Full legal terms are retained in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and About → Licenses & legal. No licence has been invented for original Northpass code; the owner must select one before a public source release. Historical source/licence records remain in Git; unused legacy components are excluded from the product.
