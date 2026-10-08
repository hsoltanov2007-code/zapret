# Changelog

## 0.2.0 — development

- Extracted the original v0.1 starter into the repository, retaining its ZIP.
- Split WPF, core models, engine abstractions, Zapret2 adapter, services and tests.
- Replaced synchronous engine calls and engine-specific UI logic with MVVM commands, constructor injection and `IDpiEngine` asynchronous lifecycle.
- Added configuration/dependency/file checks, reviewed exact Zapret2 option schema, version/dry-run preflight and literal argument passing.
- Added serialized ownership, live status, crash diagnostics, process-tree cleanup and bounded optional recovery.
- Added JSON profile editing/import/export with duplicate/path checks and atomic local settings/profile writes.
- Added five-page graphite UI, custom icon, tray controls, session clock and English/Russian/Azerbaijani main UI labels.
- Added separate HTTPS reachability tests and log export, opt-in GitHub update metadata and elevated per-user autostart.
- Added portable process/service tests, a Windows WPF smoke test, pinned SDK/locks, repeatable cloud setup, release scripts, installer definition and CI.

Windows driver/engine and desktop acceptance remain required before release. Native C++ engine, automatic strategy search, automatic update installation and signing are not implemented.

## 0.1.0 — starter

Single .NET 8 WPF app, external Zapret2 adapter, JSON profiles, safe argument-list invocation, logs and administrator manifest. No engine binaries or functioning preset strategies included.
