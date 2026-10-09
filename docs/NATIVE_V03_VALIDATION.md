# Native v0.3 validation record

This record distinguishes executable tests from simulations and tracks the final CI run before delivery.

- Linux local: 29 CTest groups under ASan/UBSan passed; 168 portable .NET tests passed. Full solution cross-build succeeded with no warnings/errors; this is not a Windows WPF runtime test.
- Windows CI baseline: real MSVC native and existing driver tests ran. Initial broker acceptance found ACL sealing, .NET elevated-process wait and foreign-token duplication defects; these were fixed. Actual medium WPF/elevated worker Native/Flowseal no-traffic/repair/replay acceptance passed in run 37870404421 before the default composition switched. Final production-composition validation is in progress.
- Synthetic transformation/parser/flow/queue benchmarks do not certify real hardware throughput or DPI bypass.
- UAC code 1223 mapping has a unit test. CI's pre-elevated handoff exercises real Windows tokens/IPC/driver components, but does not exercise the secure-desktop approval/denial interaction.
- Clean Windows 10/11 installer desktop sessions, alternate-account elevation, low/remote adversarial clients, sustained traffic/hardware/kernel-loss measurements and Russian ISP bypass remain unverified.

Final run, exact test totals, artifact links and remaining scope will be entered after CI completes.
