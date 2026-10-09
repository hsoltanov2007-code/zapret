# Native v0.3 validation record

The production composition passed [GitHub Actions run 37871057819](https://github.com/hsoltanov2007-code/zapret/actions/runs/37871057819) at commit `1465bf2`. Later commits rerun the same required workflow; the PR checks and artifact SourceRevision identify each exact tested build. This recorded run is not a claim that future revisions passed.

| Check | Actual result in the recorded green run |
|---|---|
| Linux original C++ | 29 CTest groups, ASan/UBSan, passed |
| Windows original C++ | 29 CTest groups, MSVC x64, passed |
| Portable .NET | 168 tests on Linux and 168 on Windows, zero skipped/failed |
| Windows integration | 10 cases, actual WPF/runtime/ACL/offline/WinDivert/control fixtures, passed |
| Privileged broker | Seven actual published WPF medium-token scenarios passed |
| Installer | Actual one-file EXE install, published entry points, reuse, missing-component rejection/restoration and uninstall passed |

Broker scenarios: Native dedicated ::1 UDP pass-through; reviewed Flowseal `filter=false`; offline repair of a corrupted native DLL; replay rejection; IPC disconnect with active engine; abrupt UI parent termination; owned privileged worker crash. Each used 64 unchanged 128-byte datagrams and actual numeric native metrics before shutdown/fault. Every scenario also rejected a real unrelated pipe client and verified the native child exited. Crash snapshots are pre-crash observations, not proof that crash-time packets were delivered. Job cleanup is verified, not losslessness.

Native fault case: actual executable with typed scoped fault hooks tested injected driver-open failure, suppressed reinjection of a real captured loopback packet (known drop/fatal reported), delayed observation/queue saturation with 128 original UDP datagrams delivered unchanged and skipped observations reported, and worker termination with exit 91. The real driver was reopened after these failures. The injected driver-open error is not a naturally occurring hardware failure. No malformed synthetic traffic was injected into Windows networking.

The separate Native IPC suite covers wrong PID/token, replay, pipe squatting/substituted server and malformed frames. Existing Native lifecycle tests cover duplicate startup, cancellation, owner death, file leases, actual IPv4 UDP/TCP and IPv6 UDP integrity, and synthetic TLS framing. Existing WPF/service availability results are fixtures and screenshots, distinct from actual driver/loopback cases. No external DNS/TLS/web or Russian ISP bypass claim follows from those fixtures.

The broker gate passed in [run 37870404421](https://github.com/hsoltanov2007-code/zapret/actions/runs/37870404421) before production activation. Initial acceptance exposed ACL sealing, excessive .NET process-wait rights and foreign-token duplication defects; those were corrected and the tests passed before switching the UI to `asInvoker`. Production per-user startup uses HKCU Run; the installer removes the old highest-privilege task.

Artifacts from the recorded complete run:

- [Windows x64 single EXE installer](https://github.com/hsoltanov2007-code/zapret/actions/runs/37871057819/artifacts/11590087568)
- [Native executable/offline payload/source](https://github.com/hsoltanov2007-code/zapret/actions/runs/37871057819/artifacts/11590042621)
- [Self-contained Windows application](https://github.com/hsoltanov2007-code/zapret/actions/runs/37871057819/artifacts/11590087562)
- [Windows test evidence and localized screenshots](https://github.com/hsoltanov2007-code/zapret/actions/runs/37871057819/artifacts/11590850871)

## Explicit limitations

- CI launches a **real restricted medium-integrity token** and actual published components, but replaces the interactive UAC click with a pre-elevated test handoff. Error 1223 mapping is unit-tested; secure-desktop UAC approval/denial is **not** tested by that mapping or handoff. Clean Windows 10/11 manual approval/denial remains required.
- Same-account split-token authorization is supported; alternate administrator credentials are rejected. Low-integrity/remote adversarial desktop matrices and independent broker security review remain outstanding.
- Actual silent installer acceptance on Windows CI is not clean-machine, interactive installer/tray/autostart/mixed-DPI/historical-upgrade certification. Repeat EXE repair acceptance is added after the recorded run and must be assessed in its own final CI result.
- Synthetic packet/flow/queue benchmarks are not driver/hardware throughput measurements. Short real loopback CPU/memory/latency samples do not certify sustained performance or kernel loss rates. Thread stress is not proof of race freedom.
- Kernel loss totals remain unknown. IPv6 extension/routing pseudo-header checksum semantics, fragmentation reconstruction and TCP/TLS stream reassembly remain unsupported. Unsupported observations forward the original unchanged.
- Native does not modify live traffic or implement DPI bypass. No ordinary native capture or real Russian ISP/YouTube/Discord voice/Telegram effectiveness test was performed. Flowseal remains the consumer engine.
- Northpass binaries are unsigned unless real signing credentials are supplied; reviewed WinDivert retains its official driver signature. Local administrator, compromised authorized UI/kernel and build-system compromise are outside the protection boundary.

For v0.4, evaluate the first original TCP/TLS proposal in a controlled endpoint/capture laboratory with deterministic immutable-original/rollback/checksum fixtures. Add Windows desktop UAC/security tests, sustained hardware/queue measurements and independent review before considering wider interception. Preserve Flowseal and keep ordinary live transformations disabled throughout that evaluation.
