# Northpass v0.6 behavior

## Product surfaces

Home presents status, one manually chosen reviewed strategy, Connect/Disconnect and three web-check cards. Strategy labels are localized Balanced, Alternate I/II, Simple and Adaptive, with a Custom suffix for retained custom copies. They are product names, not promises of provider effectiveness. Switching is disabled while a child/session is owned. Only reviewed canonical Flowseal profiles enter the Home list; arbitrary engine IDs, changed argument arrays and old drafts remain inert user files.

About presents the original Northpass route mark, version, product description, diagnostic support and friendly update information. No upstream names or raw update-service messages appear in its normal presentation. Legal opens the full notices/licences/source viewer; nothing legally required is hidden or discarded. Advanced tools (typed ports, data-only lists, explicit guided tests and local evidence) remain opt-in.

## Built-in diagnostics

Three fixed HTTPS endpoints: www.youtube.com, discord.com/api/v10/gateway and web.telegram.org. Each has a bounded 30-second check, including DNS, TCP 443 (up to eight addresses with four-second attempts), TLS certificate/hostname validation and HTTP/1.1 status. Checks run concurrently. They never change resolver/hosts/network/security settings, send account credentials/cookies, follow redirects or perform logins.

- Available: all four web stages Passed.
- Limited: some web stages succeeded or TLS/HTTPS failed.
- Unavailable: DNS or TCP failed.
- Unknown: no current result/all stages unknown.

The checked endpoint and limitations are visible on both Home and Diagnostics. The labels do not represent streaming, messaging, calls or successful DPI bypass. A 2xx response alone cannot certify native Telegram protocol, Discord gateway/voice or YouTube media/CDN. HTTP redirects/errors are conservatively Limited. DNS/TCP failures cannot claim all services are globally down. Detailed errors/addresses are collapsed in Diagnostics; the status indicator for the owned engine remains independent.

Automatic checks start after first preparation and explicit connect/disconnect. They run independently from Busy/Connect. Cancellation and a generation counter reject stale results after strategy/session changes; shutdown cancels and awaits the tracked checks before disposing the controller. No periodic background polling, telemetry or account data collection is added. The privacy text describes these automatic endpoint contacts. Local logs can contain host/IP/path/provider information; review exports before sharing.

## Upgrade and distribution

v0.6 starts from v0.5. The old adapter/source catalog/profiles/runtime payload and manual executable/profile JSON UI are removed. Engine-neutral interfaces/controller/process ownership remain. The sole reviewed Flowseal pin, component hashes and strategies are unchanged. Installed verified Flowseal files are reused; old-engine selections fall back to the included starter and save that new selection. Language, recovery/tray/autostart/advanced preferences survive. Unsupported user profile files are preserved and excluded, never executed. A modified/missing starter is not overwritten: a canonical included recovery copy gets a new ID.

The in-place installer deletes only the named obsolete product DLL/payload/bundled profiles/source package in the application directory. It does not erase user data or the old protected engine roots, terminate external engines or delete global drivers. Collision detection still recognizes external winws2 processes for safety; this is not a product engine path. Each distribution starts from a clean publish output and contains only one runtime payload. Uninstall retains reviewed installed engine revisions/user data as before.

The original vector logo and seven-size ICO ship as tracked branding assets, with geometry matching WPF. CI renders Home/About in all three languages and exercises actual WinDivert with filter=false, without capturing traffic. Fixture web outcomes only demonstrate UI/classification/cancellation; no live service or Russian ISP result is fabricated. Signing, ordinary-user UAC, clean-machine/high-DPI/tray/autostart and real playback/call/messaging checks remain acceptance work.
