# Development and validation

## Windows 10/11 x64

1. Install .NET SDK 8.0.425 or Visual Studio 2022 with .NET desktop development and that SDK. `global.json` pins the 8.0.4xx patch family.
2. Install Visual Studio 2022 C++ build tools/Windows SDK and CMake 3.24+. Clone the repository and open `Northpass.sln`. `scripts/build-native.ps1` builds the original C++20 project and creates its embedded offline catalog before managed compilation.
3. Install Python 3.10+ and run `scripts/test.ps1` from Administrator PowerShell. Portable xUnit tests exercise a real harmless .NET child process plus model/service tests. Windows checks exercise real WPF automatic preparation/reuse, localized consumer text, screenshots and custom modals, protected offline engine installation, ACL/file lock failures and the actual pinned PE version/parser. A separate no-traffic driver test records initialized versus policy-blocked outcomes explicitly.
4. Open PowerShell as Administrator and run `scripts/run-dev.ps1`. The development CLI cannot request elevation itself. A published `Northpass.exe` requests elevation when launched normally. The development launcher prepares verified offline inputs before compiling. Fresh settings automatically select the included configuration; strategies are selected on Home; production hides advanced tools. A Debug-only `--dev-tools` argument enables the internal typed/guided tools. Follow `WINDOWS_ACCEPTANCE.md` for the driver, tray, scheduler and crash scenarios.
5. Run `scripts/build.ps1`; use `-Installer` after installing Inno Setup 6.3+ to create the installer. The pinned Flowseal fallback and experimental NorthpassCore pass-through payloads, full licences and corresponding source archives are included. Checksums in the published folder describe that build, not an externally authenticated release signature.

PowerShell scripts propagate nonzero exit status and run from the repository root. The installer includes the self-contained app, bundled draft and documentation. Data is stored outside the install directory. Do not move the published EXE while its scheduled autostart task is enabled.

## Cloud Linux x64

`bash scripts/setup-cloud.sh` installs .NET 8.0.425 below `/workspace/.northpass-tools/dotnet` with an SHA-512 from official Microsoft release metadata. It reuses that installed SDK, writes `/workspace/.northpass-tools/env.sh`, restores locked test dependencies and builds the entire solution. No worktree is needed: cloud tasks already have an isolated checkout.

For each new shell/task:

```bash
cd /workspace/zapret
source /workspace/.northpass-tools/env.sh
dotnet test tests/Northpass.Tests/Northpass.Tests.csproj -c Release --no-restore --logger trx --results-directory /workspace/.northpass-tools/test-results
dotnet build Northpass.sln -c Release --no-restore
```

Do **not** run Windows test assemblies or WPF on Linux. Cross-compilation with `EnableWindowsTargeting=true` emits Windows assemblies but proves no Windows UI, driver, tray or engine behavior. A self-contained `win-x64` publish can likewise be produced on Linux but requires Windows to execute.

Required network destinations: `builds.dotnet.microsoft.com` for the SDK; `api.nuget.org` and NuGet package endpoints for restore; platform GitHub HTTPS proxy for Git; `api.github.com` for PR/CI API access and optional release metadata. `codeload.github.com` supplies pinned engine/redistribution source archives. No new secrets are required when the platform's existing Git/GitHub bindings work. No background service is required for portable development.

## Critical tests

The portable suite checks malformed/draft profiles, path traversal, duplicate IDs/import preservation, atomic settings round trips and corruption, exact option names/arity, dependency and referenced-file detection, known incompatible flags, non-Windows engine rejection, process launch/stop/restart/cancellation, duplicate-start serialization, stdout/stderr, literal arguments, exit codes, descendant termination, adapter injection, recovery limits/cancellation and diagnostic HTTP semantics. HTTP unit tests use an injected handler rather than asserting remote Internet behavior.

On Linux the process fixture is .NET, not a Windows executable or production engine. Container PID 1 may retain killed descendants as zombies; the process test confirms they cannot execute rather than interpreting a zombie PID as an active child. Windows tests use Windows process state directly.

GitHub Actions has separate Linux and Windows jobs. The Windows job runs both suites, cross-project Release compilation through the tests, x64 publish and installer compilation. Windows tests use the actual pinned official components. CI also runs the built installer and published bootstrap/reuse/missing-component/uninstall checks on its Windows runner. These do not establish ISP-specific bypass or clean consumer-machine compatibility. See the manual acceptance checklist for those checks.


### v0.5 release signing

The default CI artifacts are unsigned. On a Windows release machine with Windows SDK `signtool.exe` and an authorized code-signing certificate in the current-user store, run `./scripts/build.ps1 -Installer -SignCertificateThumbprint <40-hex-thumbprint>`. The optional `-TimestampUrl` must use HTTPS. This signs and verifies Northpass's own executable/project DLLs before generating distribution checksums and then signs/verifies the final single setup EXE. It never changes the pinned engine/driver/DLL payload bytes. A missing tool/certificate, signing error or verification failure blocks that release build. Signing requires real release credentials and was not performed for development artifacts. Inno Setup's installed uninstaller signing remains a separate release policy; validate it before a public signed release.


v0.6 uses three fixed web probes instead of a manual URL field. Availability tests and WPF screenshots use explicitly labelled endpoint fixtures. Actual engine tests still use the reviewed PE and no-traffic driver; they do not claim real service/ISP access. The installer test seeds known obsolete product filenames as inert fixtures to exercise cleanup, rather than claiming a complete historical-version upgrade was run. Full upgrade/high-DPI/ordinary-user/provider checks remain manual.


## v0.7 presentation

First launch uses Russian; existing EN/RU/AZ preferences remain. The native-name/vector-flag selector saves language immediately. No standard advanced-tools opt-in exists; historical saved fields are retained as inert data. For internal development only, compile Debug and pass `--dev-tools` to Northpass (the Release entry point ignores it). No signing/elevation/integrity checks are bypassed by that argument.

Windows UI acceptance includes actual localized splash/main-window transition and close-during-preparation cleanup, visible native language labels/flags, 100/150/200% vector-flag raster checks, language changes through the control, hidden legacy opt-ins, and reduced-motion/loading-clock cleanup. UI endpoint/session outcomes remain fixtures. `TestResults` includes all three locales for Home/About/Settings/Splash; CI annotations carry bounded Russian previews. Real multi-monitor/DPI interaction, tray/UAC/animation-policy changes and live ISP checks remain manual acceptance.

## Native Engine v0.3

Run `bash scripts/setup-native-cloud.sh` after cloud .NET setup; source `/workspace/.northpass-tools/native-env.sh` for the checksum-pinned CMake toolchain. Native CTest uses address/undefined sanitizers on Linux; actual Windows interception tests use dedicated loopback sockets only. See [NATIVE_ENGINE.md](NATIVE_ENGINE.md) for restricted scope, immutable offline trust, parent-death/drain behavior, exact Windows build commands, LGPL modification instructions and unverified real-network work. Native code signing, when configured, occurs before generating/embedding its hash manifest.

Native v0.3 uses a protected native elevation bootstrap and managed broker. Normal WPF startup is `asInvoker`; elevation is limited to network preparation/owned engine children. User-writable `dotnet run` output cannot use this privileged trust boundary. `scripts/run-dev.ps1` builds/installs a protected development package and asks the interactive desktop shell to launch the UI. GUI shell behavior and secure-desktop UAC remain manual desktop checks. `scripts/test-broker.ps1` uses the actual published WPF application with a real restricted medium token and a pre-elevated CI handoff, never a mock token or broad native filter. Read `BROKER_SECURITY.md` and `NATIVE_V03_VALIDATION.md` before interpreting results.
