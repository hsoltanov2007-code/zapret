# Native Engine v0.1 adapter

NativeEngine implements IDpiEngine for the original C++20 NorthpassCore process.
NativeCatalog accepts only typed idle/dedicated-loopback pass-through scopes.
No DPI bypass is implemented. Flowseal remains the consumer default/fallback.
Build scripts generate an immutable embedded offline manifest before compiling
the Windows app. No downloaded manifest, script or arbitrary filter can authorize
execution. See [architecture, trust and tests](../../docs/NATIVE_ENGINE.md).
