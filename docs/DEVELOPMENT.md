# Development and validation

## Windows 10/11 x64

1. Install .NET SDK 8.0.425 or Visual Studio 2022 with .NET desktop development and that SDK. `global.json` pins the 8.0.4xx patch family.
2. Clone the repository and open `Northpass.sln`.
3. From the root run `scripts/test.ps1`. Portable xUnit tests exercise a real harmless .NET child process plus model/service tests. The Windows test opens an actual WPF window on an STA thread, visits all five pages and closes it without starting an engine.
4. Run `scripts/run-dev.ps1` and accept elevation. Configure the official bundle and a trusted version-specific profile. Follow `WINDOWS_ACCEPTANCE.md` for the driver, tray, scheduler and crash scenarios.
5. Run `scripts/build.ps1`; use `-Installer` after installing Inno Setup 6 to create the installer. No engine bundle is included. Checksums in the published folder describe that build, not an externally authenticated release signature.

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

Required network destinations: `builds.dotnet.microsoft.com` for the SDK; `api.nuget.org` and NuGet package endpoints for restore; platform GitHub HTTPS proxy for Git; `api.github.com` for PR/CI API access and optional release metadata. No new secrets are required when the platform's existing Git/GitHub bindings work. No background service is required for portable development.

## Critical tests

The portable suite checks malformed/draft profiles, path traversal, duplicate IDs/import preservation, atomic settings round trips and corruption, exact option names/arity, dependency and referenced-file detection, known incompatible flags, non-Windows engine rejection, process launch/stop/restart/cancellation, duplicate-start serialization, stdout/stderr, literal arguments, exit codes, descendant termination, adapter injection, recovery limits/cancellation and diagnostic HTTP semantics. HTTP unit tests use an injected handler rather than asserting remote Internet behavior.

On Linux the process fixture is .NET, not a Windows executable and not Zapret2. Container PID 1 may retain killed descendants as zombies; the process test confirms they cannot execute rather than interpreting a zombie PID as an active child. Windows tests use Windows process state directly.

GitHub Actions has separate Linux and Windows jobs. The Windows job runs both suites, cross-project Release compilation through the tests, x64 publish and installer compilation. It still cannot establish ISP-specific bypass or test a missing official driver bundle. See the manual acceptance checklist for those checks.
