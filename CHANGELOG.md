# Changelog

## 0.4.0 — development

- Graphite WPF redesign with custom window chrome and fully dark controls; a simple Home view with one Connect action and truthful access status.
- Automatic bundled first-launch preparation; packaged builds require offline inputs and fail closed without a network fallback.
- Localized product messages, dark in-app confirmation modals, optional advanced tools and an About licence/source viewer.
- Single installer EXE with optional launch, separate installer CI artifact, localized screenshots and actual installed-app preparation/reuse/failure/uninstall acceptance.
- Reviewed pins, archive/component verification, protected ACLs, launch leases, owned process lifecycle and replaceable engine interfaces retained.

## 0.3.0 — development

- Automatic consent-based Zapret2 x64 setup from a reviewed Git pin, archive/component SHA-256 verification, safe bounded extraction and protected Windows ACLs.
- Verified installation reuse, explicit repair with quarantine/failure restoration and launch leases; managed EXE/Lua paths, environment scrubbing, update transaction and catalog-based rollback.
- EN/RU/AZ setup consent/status/progress, reviewed example profile and automatic Connect preparation through the existing engine architecture.
- Reproducible offline payload and installer packaging with full notices and corresponding third-party sources.
- Security regression tests, real Windows offline installation/ACL/version/parser checks and a no-traffic driver lifecycle check with explicit policy-block evidence.
- Preserve actual startup output/exit codes and ownership on cleanup failure; no bypass claim from process liveness.

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
