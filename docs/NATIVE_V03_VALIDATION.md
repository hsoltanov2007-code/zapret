# Native v0.3 validation record

This record distinguishes executable tests from simulations and tracks the final CI run before delivery.

- Linux local: 29 CTest groups under ASan/UBSan passed; 168 portable .NET tests passed. Full solution cross-build succeeded with no warnings/errors; this is not a Windows WPF runtime test.
- Windows CI baseline: real MSVC native and existing driver tests ran. Initial broker acceptance found ACL sealing and .NET elevated-process wait rights defects; those were fixed and are being revalidated. Production broker activation is gated on successful actual medium UI acceptance.
- Synthetic transformation/parser/flow/queue benchmarks do not certify real hardware throughput or DPI bypass.
- UAC code 1223 mapping has a unit test. CI's pre-elevated handoff exercises real Windows tokens/IPC/driver components, but does not exercise the secure-desktop approval/denial interaction.
- Clean Windows 10/11 installer desktop sessions, alternate-account elevation, low/remote adversarial clients, sustained traffic/hardware/kernel-loss measurements and Russian ISP bypass remain unverified.

Final run, exact test totals, artifact links and remaining scope will be entered after CI completes.
