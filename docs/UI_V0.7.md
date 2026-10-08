# Northpass v0.7 presentation

v0.7 builds on the v0.6 branch. The reviewed engine/catalog/payload/strategies and integrity/ownership boundaries are unchanged; this release refines presentation and startup lifetime.

## Language and internal tools

Fresh, missing-language or unsupported-language preferences resolve to Russian. Explicit saved `ru`, `en` and `az` stay intact, including on startup/error dialogs and the splash. Changing the flag/native-name selector immediately persists only the language/selection through the existing atomic store; unrelated pending settings still use Save. An unreadable settings file is preserved and reported, never silently replaced. Flags are DrawingImage vector geometry, independent of color-emoji/font support. Native labels remain recognizable in every locale.

The visible advanced checkbox and all translations of its text are removed. The old JSON field remains inert for compatibility; it cannot expose the advanced tab or make page index 1 selectable. Only Debug composition with `--dev-tools` enables the internal typed-input/guided-test surface. Release composition ignores that argument. There is no standard navigation/settings opt-in.

## Startup and motion

The localized dark splash shows the route mark, initialization and actual preparation state. The interface is revealed after initialization, while automatic service checks continue independently. There is no minimum splash dwell or fake percentage/time. A short 160 ms presentation fade overlaps the main window; its completion wait is bounded to 220 ms if a Windows policy change removes an animation clock. Cached startup uses the same fast path. Tray startup skips the splash. Alt+F4 during preparation/transition requests the main owner's ordinary cancellation/owned-process cleanup; the presentation waits for its close before returning cancellation.

Motion attaches to view elements only: 240 ms entrance/page/result fades, 110 ms hover/press easing and a quiet loading arc. Windows client-area/UI-effects preferences and inherited reduced-motion state disable movement. Loading arcs stop when hidden/unloaded or when the policy changes; policy subscriptions are removed with their view. Buttons retain visible keyboard focus and disabled states. No animation modifies network outcomes or delays Connect/Disconnect.

Home retains reviewed strategy selection, independent engine/process status, the Connect/Disconnect action, three service cards and Check again. A web-refresh indicator shows actual diagnostic activity. About retains product/support/update presentation and a separate legal/source viewer. No upstream engineering language is added to these main surfaces. Full required notices/sources remain packaged.

## Evidence and limits

Portable tests cover new/default/missing/unsupported preferences, existing EN/RU/AZ persistence, corruption preservation and version comparison. Windows UI tests exercise real WPF layout, localized Home/About/Settings/Splash screenshots, flag/native-label binding, language changes through the selector, inert historical opt-ins, debug-only model composition, splash/main lifetime, cancellation during preparation, reduced-motion/loading cleanup and exact Russian stripe colors at 100/150/200% pixel densities. Session/web outcomes are explicitly fixtures; actual engine/driver/installer checks remain separate.

CI density tests concern the vector asset, not interactive whole-window high-DPI/mixed-monitor acceptance. Ordinary-user UAC, tray/autostart, keyboard/screen-reader interaction, clean consumer machines, signing and real Russian ISP media/voice/native messaging checks remain manual. The app is elevated and development artifacts are unsigned unless the signing hook is configured. Web availability remains scoped to fixed HTTPS endpoints and never comes from a running process.
