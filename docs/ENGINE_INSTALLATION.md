# Engine acquisition and security

## Reviewed input (2026-10-08)

The official Windows bundle's Git commit `6eb463a6758fb48cd101bc55dfd057e6e9d98af1` is pinned, not dynamically resolved at runtime. Its HTTPS codeload ZIP is 19,522,338 bytes (the manifest is authoritative) with SHA-256 `5f828239310fed0da587bc09eaa22742615b2a85cf41f55ff28128e22231c6de`. The manifest records each selected Git file's independent SHA-256 and exact size. A deterministic ZIP_STORED offline payload is generated from precisely these verified files and checked against a separate reviewed hash.

Provenance review identified PE x64 `winws2.exe`, embedded Zapret2 version `v1.0.5.2` / source commit `6b6c63e3385fa73f8af3be4a69171e947f5a319d`, Cygwin 3.4.10-1 runtime, statically linked LuaJIT 2.1 and zlib 1.3.1. The bundled WinDivert64.sys exactly matches the official WinDivert 2.2.2-A x64 driver. All nine DLL sections match official 2.2.2-A WinDivert.dll byte-for-byte; PE header differences account for a different whole-file hash. The bundled Lua files were compared with the executable's source revision: five match; zapret-lib.lua has upstream URL delimiter and 64-bit QUIC/LuaJIT handling fixes. Those exact bundled bytes are pinned separately. This is provenance/compatibility review, not a complete audit of compiled code or a guarantee against upstream compromise.

No conventional GitHub Releases endpoint is used for the Windows bundle. Missing, changed, re-compressed or redirected source archives fail integrity checks and require maintainer review. TLS validation stays enabled. Hashes are not regenerated on failure. Upstream command scripts, ARM64/testsigning helpers, service scripts, AV advice and unrelated tools are never executed by setup.

## Installation transaction

`IEngineInstallationManager` is independent of `IDpiEngine`. Services implement verified acquisition; the Windows desktop supplies an ACL policy. Installation is a serialized transaction under a cross-process exclusive file lock:

1. Require Windows x64 administrator rights. Installing the v0.4 app authorizes internal preparation of its included components; no separate engine consent is required.
2. Create/validate the engine root beneath protected Program Files. Reject junctions/symlinks in all ancestor and child paths. Existing unsafe owners/ACLs are errors, not silently accepted.
3. Copy the required bundled offline payload into a unique protected staging folder. Verify archive size and SHA-256 before opening it. Missing/corrupt bundled bytes fail without a network fallback. The reusable service retains verified pinned HTTP acquisition for other compositions, but the production app explicitly disables that fallback.
4. Validate all ZIP names, including unused entries; reject traversal, absolute/drive/ADS paths, reserved Windows device names, duplicate case-insensitive names, links/special files and oversized archives. Extract only manifest-listed files using create-new writes with bounded expansion. Verify every component size/hash.
5. Seal every directory and file with explicit protected DACLs: SYSTEM and Administrators full control; Users read/execute. Owner is Administrators. Verify the complete tree and reject extra files (including DLLs). Probe the actual engine version.
6. Activate by same-volume atomic replacement of the small protected selection file only after successful verification. Failed acquisition/extraction/probes do not activate the new version. Preserve the old revision for optional rollback.

Fresh normal setup reuses a fully verified installation without downloading. A corrupt/quarantined installation fails closed and reports which component needs restoration. It is never executed merely because a path exists. The explicit Repair installation action copies fresh reviewed bytes, keeps the replaced protected tree quarantined and restores the prior directory if repair fails. Users do not need to obtain or locate components manually. Unsafe ACLs/links still require administrator remediation; setup never silently blesses them.

Before launch (including recovery), the manager checks the selection against the compiled catalog, validates all owners/DACLs, verifies all component hashes and rejects extras. Read handles deny write/delete sharing for every installed file throughout probes and the child lifetime; an installation lock prevents updates while running. The executable and working directory are the protected version folder. PATH is restricted to System32 and Lua/Cygwin environment overrides are removed. Managed Lua-init references must stay within the verified engine tree. No untrusted downloaded manifest, PATH lookup, manual EXE selection or external Lua can substitute engine code.

The security boundary is against ordinary users, writable downloads/profile directories and archive content. An administrator, kernel compromise, compromised Northpass binary, or malicious upstream before review is outside that boundary. Northpass and manifests must themselves be distributed authentically; unsigned development CI artifacts do not provide release authenticity. Runtime hashes complement Windows driver signing and security policy; they do not replace it.

## Updates, rollback and modifications

Check updates compares installed revision with the current embedded catalog. An upstream commit cannot become installable until a maintainer reviews it and ships a new Northpass build. UI updates require explicit consent and disconnection. The old verified revision stays selected if the new payload, hash check or version probe fails. Rollback validates/probes the previous trusted revision before atomically selecting it. No rollback is promised for a first installation with no previous version, or for a previous version removed or modified externally.

For a new reviewed pin: preserve the old full manifest in `engine/catalog/previous/<revision>.json` (embedded automatically); update the current manifest with independently reviewed archive/component hashes, sizes, source revision and version; regenerate the deterministic selected payload and review its hash; review any changed PE imports, Lua/options and redistribution sources/notices; run portable and actual Windows integration tests and produce new offline artifacts. Never accept a remote JSON hash as the trust anchor. Keep old manifests in future builds for reuse/rollback; catalog revocations must be explicit.

Third-party licences permit modification and debugging; the managed install checks are not a restriction on those rights. Corresponding Cygwin and WinDivert source plus Zapret2 source and build files accompany the offline distribution. To use an interface-compatible modified library, rebuild it from those sources, review your hashes, update the local trusted catalog/payload and rebuild Northpass with .NET SDK 8.0.425. Program Files ACLs require an administrator to deploy the replacement. Modified drivers must satisfy Windows's signing policy; Northpass never changes that policy. Zapret2 may also be built/run independently using its own source and documentation.

## Status and cleanup

Installation Ready means components are verified, not that a network strategy works. Active means the owned child exists. The driver smoke test uses the literal filter `false` to capture no traffic; its evidence distinguishes initialization from an explicit Windows policy block. A successful parser probe does not validate Lua behavior or ISP effectiveness. Driver/Lua failures propagate actual bounded output and exit codes to Diagnostics.

Disconnect/exit kills only the owned process tree, waits for termination and releases pipes, file handles and install locks. Windows closes the process's WinDivert handles. Northpass does not delete global driver services or kill unrelated engines because other applications may use them. No Windows security setting is disabled, no firewall rule is changed and no bypass success is inferred from liveness.
