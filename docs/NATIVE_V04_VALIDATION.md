# Native v0.4 validation record

[GitHub Actions run 37877523880](https://github.com/hsoltanov2007-code/zapret/actions/runs/37877523880), workflow head `612ac6ae90297e47f363fef453ebfbbff46db4eb`, completed successfully on 2026-10-09. Later revisions require their own passing workflow; final PR checks and packaged SourceRevision identify the actual build. This record does not certify future commits.

| Actual check | Recorded result |
|---|---|
| Linux C++ ASan/UBSan | 39 CTest groups passed |
| Windows Server 2025 C++ | 39 MSVC x64 groups passed |
| Windows Server 2022 C++ | 39 MSVC x64 groups passed |
| Portable .NET | 182 passed on Linux and 182 on Windows; none skipped |
| Server 2025 integration | 12 cases passed, including actual executable/driver TLS lab |
| Server 2022 TLS lab | Two cases passed, actual driver/endpoints |
| Medium UI / elevated broker | Seven actual published-component scenarios passed |
| Offline EXE | Actual install/reuse, corruption rejection/restoration, repeat repair and silent uninstall passed |

## Synthetic results

Deterministic original C++ fixtures cover complete/truncated/malformed ClientHello lengths, extension bounds, unsupported layouts, unknown/incorrect handshake state, wrapping sequences, timestamp headers, IPv4/6, varied MTUs/payload sizes, complete/partial retransmissions, duplicate proposals, gaps/overlap, checksum corruption, lifecycle, commit visibility and irreversible send failure. The 2000-transform Server 2025 synthetic benchmark reported approximately 2.10 microseconds per proposal/validation transaction for a 396-byte packet. It is not actual driver/hardware performance.

The toy packet-local simulator recognized the original complete ClientHello, missed split pieces and accepted exact reassembly. It has no TCP reassembly and models no real ISP. Malformed synthetic packets were not injected into Windows networking.

## Actual Windows loopback results

On each Windows OS, eight sessions covered IPv4/IPv6, TLS 1.2/1.3 and original pass-through/Split. Each authenticated three independent connections and reconstructed fixed 1/4096/32768-byte patterns exactly in both directions. Each Split session accepted three ClientHellos and submitted six segments successfully. Separate sniff-only handles captured post-injection sequences/lengths matching committed segments. Locally generated TLS certificates were exactly pinned, not installed in certificate stores. Temporary per-user Schannel keys were disposed after testing.

On Server 2025 the four real Split sessions reported proposal/validation latency 12.3..16.867 microseconds, private memory 2,441,216..2,478,080 bytes and short-sample CPU percentages 5.903..28.455. These are brief whole-process/loopback samples, not sustained hardware benchmarks. Known drops were zero in successful sessions; kernel losses were explicitly unknown. Destination reconstruction was measured by the endpoint, never inferred from send counts/PID.

Post-injection loopback packets can have zero omitted checksum fields even after valid prepared segments. Traces distinguish absent/unverified fields from valid checksums; nonzero invalid checksums are refused. Loopback evidence does not prove external wire checksum/offload behavior.

First/second send-fault cases deliberately suppressed an actual captured lab segment: successful prior sends were zero/one; workers exited nonzero, reported incomplete-original forwarding, forbade rollback and released the driver. Both endpoints recovered through normal TCP retransmission after interception stopped in this run. That is not rollback or guaranteed recovery. These injected faults are not naturally occurring hardware/driver failures. STOP during authenticated TLS application writing preserved reconstruction after cleanup; a one-second hard stop ended interception and subsequent authenticated TLS succeeded. The actual driver reopened afterward.

Existing real tests also covered queue saturation/skipped observations, UDP/TCP integrity, injected driver initialization failure, actual worker termination, parent death, duplicate startup, cancellation and native IPC authentication/substitution/replay. Broker scenarios retained reviewed Flowseal `filter=false` reconnects/data-only lists, offline repair, hostile peer/replay rejection, active disconnect, parent death and worker crash with child/session cleanup. Repeated installer execution repaired a corrupted native DLL from its offline payload; silent uninstall removed the app and retained documented engine revisions/user data. UI/service diagnostic fixtures and screenshots are not internet availability tests.

## Recorded artifacts

- [Offline Windows x64 installer EXE package](https://github.com/hsoltanov2007-code/zapret/actions/runs/37877523880/artifacts/11592719220)
- [NorthpassCore 0.4.0 executable/payload/original and corresponding sources](https://github.com/hsoltanov2007-code/zapret/actions/runs/37877523880/artifacts/11593625001)
- [Windows evidence, TLS JSON/packet traces and screenshots](https://github.com/hsoltanov2007-code/zapret/actions/runs/37877523880/artifacts/11592534684)
- [Server 2022 lab evidence](https://github.com/hsoltanov2007-code/zapret/actions/runs/37877523880/artifacts/11592883075)

Native is 0.4.0; consumer UI/installer stays 0.7.0. Downloads require GitHub sign-in and follow Actions retention. Source manifests record the checked-out build revision; workflow head and PR head are separately visible.

## Unverified and limitations

Interactive UAC approval/denial and clean desktop Windows 10/11, tray/autostart/historical upgrade, hostile low-integrity/remote matrices, independent security review, signed Northpass distribution, sustained hardware/queue/driver pressure and independent external capture remain unverified. CI uses real medium tokens with a pre-elevated handoff, not a secure-desktop click. See [manual procedures](NATIVE_V04_MANUAL.md).

No general-network interception, ordinary Native modifications, stream/fragment reconstruction, HRR/0-RTT evaluation or Russian ISP effectiveness demonstration occurred. Partial/ambiguous sends can interrupt a connection; known-drop counts are lower bounds and kernel loss remains unknown. Bootstrap termination/power loss can retain protected temporary data. Same-account split-token authorization only; privileged administrators, compromised authorized UI/kernel/build system are outside this boundary. Official driver signing is retained; Northpass is unsigned unless real signing credentials are supplied. Flowseal remains the consumer engine.

Next: independent broker/strategy review, interactive desktop acceptance, explicitly authorized controlled external-endpoint capture, richer retransmission/duplicate/loss scheduling and sustained measurements. Do not automatically enable or broaden Split based on the simulator or these lab results.
