# Native v0.4 manual desktop acceptance

These procedures have not been executed by Linux setup or GitHub hosted CI. Record machine OS/build, revision/artifact checksum, administrator account type, signing status, result and evidence. Do not turn a procedure into a passing test record. CI uses actual restricted tokens with a pre-elevated handoff, not the secure desktop UAC click.

## Windows 10 and Windows 11

Use clean Windows 10 x64 22H2 and Windows 11 x64 supported builds on disposable test machines with Windows security features left enabled. Run the single offline installer, disconnect external networking for offline reuse, start the UI normally, confirm Russian default or preserved saved language, and check EN/AZ, animation, Home strategy selection and Flowseal startup/stop. Diagnostics availability requires actual service checks and does not follow from an engine PID. Run installer repair against a deliberately corrupted component only in the disposable installation; verify rejection before repair. Uninstall must remove the app while preserving documented user data/protected engine revisions. Record interactive installer/tray/autostart behavior and upgrades from the previous native root; do not grant Users write access to protected files.

## Interactive UAC approval/denial

1. Install the exact reviewed artifact normally. Inspect the main Northpass process with an OS token inspector: it must be medium integrity and not elevated.
2. Start Connect to request the trusted privileged helper. On the actual secure-desktop prompt choose **No**. Verify the friendly localized permission-declined message, no owned helper/engine remains, and retry remains available. Do not alter UAC policy or substitute the CI handoff for this check.
3. Retry and approve with the same-account split administrator token. Verify the fixed installed helper image is elevated while the WPF UI remains medium. Connect/Disconnect must stop only owned children. Ordinary Native Split is absent from UI and broker commands.
4. Deny again on a fresh session. Test application exit during an outstanding prompt. Record behavior; do not assert cancellation of Windows' secure desktop from application code without observing it.
5. A standard user supplying a different administrator account is currently unsupported: verify fail-closed authorization and a clear error, not silent use of a different logon. Test separate interactive/logon sessions and low-integrity pipe attempts on the disposable machine; retain actual evidence.

## Controlled lab

Use the documented Windows integration test command, never a general filter or an internet target. The harness creates exact loopback endpoints, ephemeral pinned certificates and 1..20-second scoped workers. The official WinDivert signature and all component hashes/ACLs must pass unchanged. Windows 11 supports the TLS 1.3 Schannel lab; Windows 10 generally requires TLS 1.2 and may not support TLS 1.3. Record unsupported TLS 1.3 as unsupported/unrun rather than enabling experimental system-wide TLS settings or skipping it as passed. Hosted Server 2022/2025 results are distinct from these desktop results.

Check actual first/second send-fault evidence, unchanged endpoint reconstruction on successful sessions, STOP/parent termination/crash cleanup and driver reopen. Compare header-only pre/post traces with optional independent capture inside the disposable lab network. TCP recovery after a partial send is not transaction rollback. Kernel loss remains unknown; packet traces do not prove general losslessness.

Do not test ordinary browsing using experimental injection. Any future non-loopback endpoint work needs a new explicitly authorized isolated topology and reviewed capability boundary. No claim of Russian ISP bypass is permitted from the toy simulator or local TLS success. Independent security review and real affected-connection demonstrations have not occurred.
