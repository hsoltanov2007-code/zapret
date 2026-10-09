# Northpass Native adapter v0.2

NativeEngine implements IDpiEngine for original C++20 NorthpassCore. Flowseal remains the consumer engine; Native is internal idle/scoped-loopback pass-through only, with no DPI bypass claim.

Default launch retains the owned stdin protocol. Explicit internal useNamedPipe opts into parent/logon-authenticated bounded IPC. IEnginePerformanceProvider exposes validated numeric diagnostics with kernel loss always unknown. Protected offline embedded manifests and launch leases remain mandatory.

See [architecture, threat model, test scope and limitations](../../docs/NATIVE_ENGINE.md).
